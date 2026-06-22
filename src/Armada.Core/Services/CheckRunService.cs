namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Executes structured check runs using workflow profiles and persists the results.
    /// </summary>
    public class CheckRunService
    {
        /// <summary>
        /// Optional callback invoked whenever a check run is created or updated.
        /// </summary>
        public Action<CheckRun>? OnCheckRunChanged { get; set; }

        private readonly string _Header = "[CheckRunService] ";
        private readonly DatabaseDriver _Database;
        private readonly WorkflowProfileService _WorkflowProfiles;
        private readonly VesselReadinessService _Readiness;
        private readonly LoggingModule _Logging;
        private readonly TimeSpan _DefaultTimeout = TimeSpan.FromMinutes(30);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _PendingRunLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CheckRunService(
            DatabaseDriver database,
            WorkflowProfileService workflowProfiles,
            VesselReadinessService readiness,
            LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _WorkflowProfiles = workflowProfiles ?? throw new ArgumentNullException(nameof(workflowProfiles));
            _Readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <summary>
        /// Execute a check run synchronously and persist the result.
        /// </summary>
        public async Task<CheckRun> RunAsync(AuthContext auth, CheckRunRequest request, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.VesselId)) throw new ArgumentNullException(nameof(request.VesselId));

            Vessel vessel = await ReadAccessibleVesselAsync(auth, request.VesselId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Vessel not found or not accessible.");

            VesselReadinessResult readiness = await _Readiness.EvaluateAsync(
                auth,
                vessel,
                request.WorkflowProfileId,
                String.IsNullOrWhiteSpace(request.CommandOverride) ? request.Type : null,
                request.EnvironmentName,
                includeWorkflowRequirements: String.IsNullOrWhiteSpace(request.CommandOverride),
                token: token).ConfigureAwait(false);
            if (!readiness.IsReady)
            {
                string message = String.Join(" ", readiness.Issues
                    .Where(issue => issue.Severity == ReadinessSeverityEnum.Error)
                    .Select(issue => issue.Message)
                    .Distinct(StringComparer.Ordinal));
                throw new InvalidOperationException(String.IsNullOrWhiteSpace(message)
                    ? "This vessel is not ready for the requested check run."
                    : message);
            }

            if (String.IsNullOrWhiteSpace(vessel.WorkingDirectory) || !Directory.Exists(vessel.WorkingDirectory))
                throw new InvalidOperationException("This vessel does not have a usable working directory.");

            WorkflowProfile? profile = await _WorkflowProfiles.ResolveForVesselAsync(auth, vessel, request.WorkflowProfileId, token).ConfigureAwait(false);
            if (profile == null && String.IsNullOrWhiteSpace(request.CommandOverride))
                throw new InvalidOperationException("No active workflow profile could be resolved for this vessel.");

            string command = !String.IsNullOrWhiteSpace(request.CommandOverride)
                ? request.CommandOverride!.Trim()
                : _WorkflowProfiles.ResolveCommand(profile!, request.Type, request.EnvironmentName)
                    ?? throw new InvalidOperationException("No command is configured for " + request.Type + ".");

            CheckRun run = new CheckRun
            {
                TenantId = vessel.TenantId,
                UserId = auth.UserId,
                WorkflowProfileId = profile?.Id,
                VesselId = vessel.Id,
                MissionId = request.MissionId,
                VoyageId = request.VoyageId,
                DeploymentId = request.DeploymentId,
                Label = request.Label,
                Type = request.Type,
                Source = CheckRunSourceEnum.Armada,
                Status = CheckRunStatusEnum.Running,
                EnvironmentName = request.EnvironmentName,
                Command = command,
                WorkingDirectory = vessel.WorkingDirectory,
                BranchName = request.BranchName,
                CommitHash = request.CommitHash,
                StartedUtc = DateTime.UtcNow,
                CreatedUtc = DateTime.UtcNow,
                LastUpdateUtc = DateTime.UtcNow
            };

            run = await _Database.CheckRuns.CreateAsync(run, token).ConfigureAwait(false);
            OnCheckRunChanged?.Invoke(run);

            string? isolatedCheckoutPath = null;

            Stopwatch sw = Stopwatch.StartNew();
            CommandExecutionResult execution;

            try
            {
                string executionDirectory = run.WorkingDirectory!;
                string executionCommand = run.Command;

                if (IsIsolatedCheckoutType(run.Type))
                {
                    string? repoSource = ResolveRepoSource(vessel);
                    if (repoSource != null)
                    {
                        isolatedCheckoutPath = await TryCloneToTempAsync(repoSource, run.CommitHash, run.BranchName, vessel.DefaultBranch, token).ConfigureAwait(false);
                        if (isolatedCheckoutPath != null)
                        {
                            executionDirectory = isolatedCheckoutPath;
                            executionCommand = StripNoRestore(run.Command);
                        }
                        else
                        {
                            return await CompleteExistingRunAsFailureAsync(
                                run,
                                "Isolated checkout could not be created: clone failed for the configured repo source. The check will not execute in the live working directory.",
                                token).ConfigureAwait(false);
                        }
                    }
                }

                execution = await ExecuteCommandAsync(executionCommand, executionDirectory, _DefaultTimeout, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                SafeDeleteDirectory(isolatedCheckoutPath);
                run.Status = CheckRunStatusEnum.Pending;
                run.LastUpdateUtc = DateTime.UtcNow;
                run = await _Database.CheckRuns.UpdateAsync(run, CancellationToken.None).ConfigureAwait(false);
                OnCheckRunChanged?.Invoke(run);
                throw;
            }
            catch (Exception ex)
            {
                execution = new CommandExecutionResult
                {
                    ExitCode = -1,
                    Output = ex.Message
                };
            }

            sw.Stop();

            run.ExitCode = execution.ExitCode;
            run.Output = execution.Output;
            run.DurationMs = Convert.ToInt64(Math.Round(sw.Elapsed.TotalMilliseconds));
            run.CompletedUtc = DateTime.UtcNow;
            run.LastUpdateUtc = DateTime.UtcNow;
            run.Status = execution.ExitCode == 0 ? CheckRunStatusEnum.Passed : CheckRunStatusEnum.Failed;

            string artifactDirectory = isolatedCheckoutPath ?? run.WorkingDirectory!;
            run.Artifacts = CollectArtifacts(artifactDirectory, profile?.ExpectedArtifacts);
            run.TestSummary = CheckRunParsingService.ParseTestSummary(run.Output, artifactDirectory, run.Artifacts);
            run.CoverageSummary = CheckRunParsingService.ParseCoverageSummary(artifactDirectory, run.Artifacts);
            run.Summary = BuildSummary(run, profile);

            SafeDeleteDirectory(isolatedCheckoutPath);

            run = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
            OnCheckRunChanged?.Invoke(run);
            return run;
        }

        /// <summary>
        /// Execute an existing pending check run in-place, preserving its durable links.
        /// </summary>
        public async Task<CheckRun> RunPendingAsync(
            AuthContext auth,
            string id,
            bool allowDeploymentExecution = false,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            SemaphoreSlim runLock = _PendingRunLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
            await runLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await RunPendingCoreAsync(auth, id, allowDeploymentExecution, token).ConfigureAwait(false);
            }
            finally
            {
                runLock.Release();
            }
        }

        private async Task<CheckRun> RunPendingCoreAsync(
            AuthContext auth,
            string id,
            bool allowDeploymentExecution,
            CancellationToken token)
        {
            CheckRun? run = await _Database.CheckRuns.ReadAsync(id, BuildScopeQuery(auth), token).ConfigureAwait(false);
            if (run == null) throw new InvalidOperationException("Check run not found.");
            if (run.Status != CheckRunStatusEnum.Pending) return run;

            if (!String.IsNullOrWhiteSpace(run.DeploymentId) && !allowDeploymentExecution)
            {
                throw new InvalidOperationException("Deployment-linked checks must be executed through the deployment workflow.");
            }

            if (IsDeploymentExecutionType(run.Type))
            {
                if (String.IsNullOrWhiteSpace(run.DeploymentId))
                    throw new InvalidOperationException(run.Type + " checks must be linked to a deployment.");
            }

            if (String.IsNullOrWhiteSpace(run.VesselId))
                return await CompleteExistingRunAsFailureAsync(run, "Check run has no vessel association.", token).ConfigureAwait(false);

            Vessel? vessel = await ReadAccessibleVesselAsync(auth, run.VesselId!, token).ConfigureAwait(false);
            if (vessel == null)
                return await CompleteExistingRunAsFailureAsync(run, "Vessel not found or not accessible.", token).ConfigureAwait(false);

            bool needsProfileCommand = ShouldResolvePendingCommand(run);
            VesselReadinessResult readiness = await _Readiness.EvaluateAsync(
                auth,
                vessel,
                run.WorkflowProfileId,
                needsProfileCommand ? run.Type : null,
                run.EnvironmentName,
                includeWorkflowRequirements: needsProfileCommand,
                token: token).ConfigureAwait(false);
            if (!readiness.IsReady)
            {
                string message = String.Join(" ", readiness.Issues
                    .Where(issue => issue.Severity == ReadinessSeverityEnum.Error)
                    .Select(issue => issue.Message)
                    .Distinct(StringComparer.Ordinal));
                return await CompleteExistingRunAsFailureAsync(run, String.IsNullOrWhiteSpace(message)
                    ? "This vessel is not ready for the requested check run."
                    : message, token).ConfigureAwait(false);
            }

            WorkflowProfile? profile = !String.IsNullOrWhiteSpace(run.WorkflowProfileId) || needsProfileCommand
                ? await _WorkflowProfiles.ResolveForVesselAsync(auth, vessel, run.WorkflowProfileId, token).ConfigureAwait(false)
                : null;

            if (needsProfileCommand && profile == null)
                return await CompleteExistingRunAsFailureAsync(run, "No active workflow profile could be resolved for this pending check run.", token).ConfigureAwait(false);

            if (needsProfileCommand && profile != null)
            {
                string? resolved = _WorkflowProfiles.ResolveCommand(profile, run.Type, run.EnvironmentName);
                if (!String.IsNullOrWhiteSpace(resolved))
                {
                    run.Command = resolved;
                }
                else
                {
                    return await CompleteExistingRunAsFailureAsync(run, "No command is configured for " + run.Type + ".", token).ConfigureAwait(false);
                }
            }

            return await ExecuteExistingRunAsync(run, vessel, profile, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Execute a matching pending check run when one exists; otherwise create and execute a new run.
        /// </summary>
        public async Task<CheckRun> RunPendingOrNewAsync(
            AuthContext auth,
            CheckRunRequest request,
            bool allowDeploymentExecution = false,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (request == null) throw new ArgumentNullException(nameof(request));

            CheckRun? pending = await FindMatchingPendingRunAsync(auth, request, token).ConfigureAwait(false);
            if (pending != null)
                return await RunPendingAsync(auth, pending.Id, allowDeploymentExecution, token).ConfigureAwait(false);

            return await RunAsync(auth, request, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Import an externally-executed check run into Armada history.
        /// </summary>
        public async Task<CheckRun> ImportAsync(AuthContext auth, CheckRunImportRequest request, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.VesselId)) throw new ArgumentNullException(nameof(request.VesselId));

            Vessel vessel = await ReadAccessibleVesselAsync(auth, request.VesselId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Vessel not found or not accessible.");

            WorkflowProfile? profile = await ResolveImportProfileAsync(auth, vessel, request.WorkflowProfileId, token).ConfigureAwait(false);
            CheckRun run = BuildImportedRun(auth, vessel, profile, request);
            run = await _Database.CheckRuns.CreateAsync(run, token).ConfigureAwait(false);
            OnCheckRunChanged?.Invoke(run);
            return run;
        }

        /// <summary>
        /// Import an externally-executed check run, updating an existing record when provider and external ID match.
        /// </summary>
        public async Task<CheckRun> ImportOrUpdateAsync(AuthContext auth, CheckRunImportRequest request, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.VesselId)) throw new ArgumentNullException(nameof(request.VesselId));

            Vessel vessel = await ReadAccessibleVesselAsync(auth, request.VesselId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Vessel not found or not accessible.");

            WorkflowProfile? profile = await ResolveImportProfileAsync(auth, vessel, request.WorkflowProfileId, token).ConfigureAwait(false);
            CheckRun run = BuildImportedRun(auth, vessel, profile, request);

            CheckRun? existing = await FindImportedRunAsync(auth, vessel.Id, run.ProviderName, run.ExternalId, token).ConfigureAwait(false);
            if (existing == null)
            {
                run = await _Database.CheckRuns.CreateAsync(run, token).ConfigureAwait(false);
            }
            else
            {
                run.Id = existing.Id;
                run.CreatedUtc = existing.CreatedUtc;
                run.LastUpdateUtc = DateTime.UtcNow;
                run = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
            }

            OnCheckRunChanged?.Invoke(run);
            return run;
        }

        /// <summary>
        /// Persist a completed Armada-generated check run without executing a shell command.
        /// </summary>
        public async Task<CheckRun> RecordCompletedAsync(CheckRun run, CancellationToken token = default)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (String.IsNullOrWhiteSpace(run.VesselId))
                throw new ArgumentNullException(nameof(run.VesselId));

            if (run.StartedUtc == null)
                run.StartedUtc = DateTime.UtcNow;
            if (run.CompletedUtc == null)
                run.CompletedUtc = DateTime.UtcNow;
            if (!run.DurationMs.HasValue && run.CompletedUtc.HasValue && run.StartedUtc.HasValue)
            {
                run.DurationMs = Convert.ToInt64(Math.Round((run.CompletedUtc.Value - run.StartedUtc.Value).TotalMilliseconds));
            }

            run.CreatedUtc = run.CreatedUtc == default ? DateTime.UtcNow : run.CreatedUtc;
            run.LastUpdateUtc = DateTime.UtcNow;
            if (String.IsNullOrWhiteSpace(run.Summary))
                run.Summary = BuildSummary(run, null);

            CheckRun? pending = await FindMatchingPendingRunForCompletedAsync(run, token).ConfigureAwait(false);
            if (pending != null)
            {
                run.Id = pending.Id;
                run.CreatedUtc = pending.CreatedUtc;
                run.LastUpdateUtc = DateTime.UtcNow;
                CheckRun updated = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
                OnCheckRunChanged?.Invoke(updated);
                return updated;
            }

            CheckRun created = await _Database.CheckRuns.CreateAsync(run, token).ConfigureAwait(false);
            OnCheckRunChanged?.Invoke(created);
            return created;
        }

        /// <summary>
        /// Retry a previously completed check run.
        /// </summary>
        public async Task<CheckRun> RetryAsync(AuthContext auth, string id, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            CheckRunQuery scope = BuildScopeQuery(auth);
            CheckRun? prior = await _Database.CheckRuns.ReadAsync(id, scope, token).ConfigureAwait(false);
            if (prior == null) throw new InvalidOperationException("Check run not found.");
            if (!String.IsNullOrWhiteSpace(prior.DeploymentId))
                throw new InvalidOperationException("Deployment-linked checks must be retried through the deployment workflow.");
            if (prior.Status == CheckRunStatusEnum.Pending)
                return await RunPendingAsync(auth, prior.Id, allowDeploymentExecution: false, token).ConfigureAwait(false);

            return await RunAsync(auth, new CheckRunRequest
            {
                VesselId = prior.VesselId ?? String.Empty,
                WorkflowProfileId = prior.WorkflowProfileId,
                MissionId = prior.MissionId,
                VoyageId = prior.VoyageId,
                DeploymentId = prior.DeploymentId,
                Type = prior.Type,
                EnvironmentName = prior.EnvironmentName,
                Label = prior.Label,
                BranchName = prior.BranchName,
                CommitHash = prior.CommitHash,
                CommandOverride = prior.Command
            }, token).ConfigureAwait(false);
        }

        private CheckRunQuery BuildScopeQuery(AuthContext auth)
        {
            return new CheckRunQuery
            {
                TenantId = auth.IsAdmin ? null : auth.TenantId,
                UserId = auth.IsAdmin || auth.IsTenantAdmin ? null : auth.UserId
            };
        }

        private async Task<CheckRun?> FindMatchingPendingRunAsync(
            AuthContext auth,
            CheckRunRequest request,
            CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(request.VesselId)) return null;

            CheckRunQuery query = BuildScopeQuery(auth);
            query.VesselId = request.VesselId;
            query.Type = request.Type;
            query.Status = CheckRunStatusEnum.Pending;
            query.Source = CheckRunSourceEnum.Armada;
            query.PageNumber = 1;
            query.PageSize = 200;

            if (!String.IsNullOrWhiteSpace(request.DeploymentId))
                query.DeploymentId = request.DeploymentId;
            if (!String.IsNullOrWhiteSpace(request.MissionId))
                query.MissionId = request.MissionId;
            if (!String.IsNullOrWhiteSpace(request.VoyageId))
                query.VoyageId = request.VoyageId;
            if (!String.IsNullOrWhiteSpace(request.EnvironmentName))
                query.EnvironmentName = request.EnvironmentName;

            EnumerationResult<CheckRun> results = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
            return results.Objects
                .Where(run => String.IsNullOrWhiteSpace(request.WorkflowProfileId)
                    || String.Equals(run.WorkflowProfileId, request.WorkflowProfileId, StringComparison.OrdinalIgnoreCase))
                .Where(run => String.IsNullOrWhiteSpace(request.Label)
                    || String.Equals(run.Label, request.Label, StringComparison.OrdinalIgnoreCase))
                .Where(run => String.IsNullOrWhiteSpace(request.BranchName)
                    || String.IsNullOrWhiteSpace(run.BranchName)
                    || String.Equals(run.BranchName, request.BranchName, StringComparison.OrdinalIgnoreCase))
                .Where(run => String.IsNullOrWhiteSpace(request.CommitHash)
                    || String.IsNullOrWhiteSpace(run.CommitHash)
                    || String.Equals(run.CommitHash, request.CommitHash, StringComparison.OrdinalIgnoreCase))
                .OrderBy(run => run.CreatedUtc)
                .FirstOrDefault();
        }

        private static bool ShouldResolvePendingCommand(CheckRun run)
        {
            return String.IsNullOrWhiteSpace(run.Command)
                || String.Equals(run.Command, "echo", StringComparison.Ordinal);
        }

        private static bool IsDeploymentExecutionType(CheckRunTypeEnum type)
        {
            return type == CheckRunTypeEnum.Deploy || type == CheckRunTypeEnum.Rollback;
        }

        private async Task<CheckRun?> FindMatchingPendingRunForCompletedAsync(CheckRun run, CancellationToken token)
        {
            CheckRunQuery query = new CheckRunQuery
            {
                TenantId = run.TenantId,
                UserId = run.UserId,
                VesselId = run.VesselId,
                Type = run.Type,
                Status = CheckRunStatusEnum.Pending,
                Source = CheckRunSourceEnum.Armada,
                PageNumber = 1,
                PageSize = 200
            };

            if (!String.IsNullOrWhiteSpace(run.DeploymentId))
                query.DeploymentId = run.DeploymentId;
            if (!String.IsNullOrWhiteSpace(run.MissionId))
                query.MissionId = run.MissionId;
            if (!String.IsNullOrWhiteSpace(run.VoyageId))
                query.VoyageId = run.VoyageId;
            if (!String.IsNullOrWhiteSpace(run.EnvironmentName))
                query.EnvironmentName = run.EnvironmentName;

            EnumerationResult<CheckRun> results = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
            return results.Objects
                .Where(candidate => String.Equals(candidate.WorkflowProfileId ?? String.Empty, run.WorkflowProfileId ?? String.Empty, StringComparison.OrdinalIgnoreCase))
                .Where(candidate => String.Equals(candidate.Label ?? String.Empty, run.Label ?? String.Empty, StringComparison.OrdinalIgnoreCase))
                .Where(candidate => String.IsNullOrWhiteSpace(run.BranchName)
                    || String.IsNullOrWhiteSpace(candidate.BranchName)
                    || String.Equals(candidate.BranchName, run.BranchName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate.CreatedUtc)
                .FirstOrDefault();
        }

        private async Task<CheckRun> ExecuteExistingRunAsync(
            CheckRun run,
            Vessel vessel,
            WorkflowProfile? profile,
            CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(vessel.WorkingDirectory) || !Directory.Exists(vessel.WorkingDirectory))
                return await CompleteExistingRunAsFailureAsync(run, "This vessel does not have a usable working directory.", token).ConfigureAwait(false);

            if (String.IsNullOrWhiteSpace(run.Command))
                return await CompleteExistingRunAsFailureAsync(run, "No command is configured for " + run.Type + ".", token).ConfigureAwait(false);

            run.WorkingDirectory = vessel.WorkingDirectory;
            run.Status = CheckRunStatusEnum.Running;
            run.StartedUtc = DateTime.UtcNow;
            run.LastUpdateUtc = DateTime.UtcNow;
            run = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
            OnCheckRunChanged?.Invoke(run);

            string? isolatedCheckoutPath = null;
            string executionDirectory = run.WorkingDirectory!;
            string executionCommand = run.Command;

            if (IsIsolatedCheckoutType(run.Type))
            {
                string? repoSource = ResolveRepoSource(vessel);
                if (repoSource != null)
                {
                    isolatedCheckoutPath = await TryCloneToTempAsync(repoSource, run.CommitHash, run.BranchName, vessel.DefaultBranch, token).ConfigureAwait(false);
                    if (isolatedCheckoutPath != null)
                    {
                        executionDirectory = isolatedCheckoutPath;
                        executionCommand = StripNoRestore(run.Command);
                    }
                    else
                    {
                        return await CompleteExistingRunAsFailureAsync(
                            run,
                            "Isolated checkout could not be created: clone failed for the configured repo source. The check will not execute in the live working directory.",
                            token).ConfigureAwait(false);
                    }
                }
            }

            Stopwatch sw = Stopwatch.StartNew();
            CommandExecutionResult execution;

            try
            {
                execution = await ExecuteCommandAsync(executionCommand, executionDirectory, _DefaultTimeout, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                execution = new CommandExecutionResult
                {
                    ExitCode = -1,
                    Output = ex.Message
                };
            }

            sw.Stop();

            run.ExitCode = execution.ExitCode;
            run.Output = execution.Output;
            run.DurationMs = Convert.ToInt64(Math.Round(sw.Elapsed.TotalMilliseconds));
            run.CompletedUtc = DateTime.UtcNow;
            run.LastUpdateUtc = DateTime.UtcNow;
            run.Status = execution.ExitCode == 0 ? CheckRunStatusEnum.Passed : CheckRunStatusEnum.Failed;

            string artifactDirectory = isolatedCheckoutPath ?? run.WorkingDirectory!;
            run.Artifacts = CollectArtifacts(artifactDirectory, profile?.ExpectedArtifacts);
            run.TestSummary = CheckRunParsingService.ParseTestSummary(run.Output, artifactDirectory, run.Artifacts);
            run.CoverageSummary = CheckRunParsingService.ParseCoverageSummary(artifactDirectory, run.Artifacts);
            run.Summary = BuildSummary(run, profile);

            SafeDeleteDirectory(isolatedCheckoutPath);

            run = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
            OnCheckRunChanged?.Invoke(run);
            return run;
        }

        private async Task<CheckRun> CompleteExistingRunAsFailureAsync(CheckRun run, string output, CancellationToken token)
        {
            DateTime now = DateTime.UtcNow;
            run.Status = CheckRunStatusEnum.Failed;
            run.ExitCode = -1;
            run.Output = output;
            run.StartedUtc ??= now;
            run.CompletedUtc = now;
            run.DurationMs ??= 0;
            run.LastUpdateUtc = now;
            run.Summary = BuildSummary(run, null);

            run = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
            OnCheckRunChanged?.Invoke(run);
            return run;
        }

        private async Task<WorkflowProfile?> ResolveImportProfileAsync(
            AuthContext auth,
            Vessel vessel,
            string? workflowProfileId,
            CancellationToken token)
        {
            WorkflowProfile? profile = null;
            if (!String.IsNullOrWhiteSpace(workflowProfileId))
            {
                profile = await _WorkflowProfiles.ResolveForVesselAsync(auth, vessel, workflowProfileId, token).ConfigureAwait(false);
                if (profile == null)
                    throw new InvalidOperationException("The supplied workflow profile is not accessible for this vessel.");
            }

            return profile;
        }

        private CheckRun BuildImportedRun(AuthContext auth, Vessel vessel, WorkflowProfile? profile, CheckRunImportRequest request)
        {
            DateTime timestamp = request.CompletedUtc?.ToUniversalTime()
                ?? request.StartedUtc?.ToUniversalTime()
                ?? DateTime.UtcNow;
            string command = !String.IsNullOrWhiteSpace(request.Command)
                ? request.Command.Trim()
                : request.Type + " (external)";

            CheckRun run = new CheckRun
            {
                TenantId = vessel.TenantId,
                UserId = auth.UserId,
                WorkflowProfileId = profile?.Id,
                VesselId = vessel.Id,
                MissionId = request.MissionId,
                VoyageId = request.VoyageId,
                DeploymentId = request.DeploymentId,
                Label = request.Label,
                Type = request.Type,
                Source = CheckRunSourceEnum.External,
                Status = request.Status,
                ProviderName = NormalizeValue(request.ProviderName),
                ExternalId = NormalizeValue(request.ExternalId),
                ExternalUrl = NormalizeValue(request.ExternalUrl),
                EnvironmentName = NormalizeValue(request.EnvironmentName),
                Command = command,
                WorkingDirectory = vessel.WorkingDirectory,
                BranchName = NormalizeValue(request.BranchName),
                CommitHash = NormalizeValue(request.CommitHash),
                ExitCode = request.ExitCode,
                Output = request.Output,
                Summary = NormalizeValue(request.Summary),
                TestSummary = request.TestSummary ?? CheckRunParsingService.ParseTestSummary(request.Output),
                CoverageSummary = request.CoverageSummary,
                Artifacts = request.Artifacts ?? new List<CheckRunArtifact>(),
                DurationMs = request.DurationMs,
                StartedUtc = request.StartedUtc?.ToUniversalTime(),
                CompletedUtc = request.CompletedUtc?.ToUniversalTime(),
                CreatedUtc = timestamp,
                LastUpdateUtc = DateTime.UtcNow
            };

            if (String.IsNullOrWhiteSpace(run.Summary))
                run.Summary = BuildSummary(run, profile);

            return run;
        }

        private async Task<CheckRun?> FindImportedRunAsync(
            AuthContext auth,
            string vesselId,
            string? providerName,
            string? externalId,
            CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(providerName) || String.IsNullOrWhiteSpace(externalId))
                return null;

            CheckRunQuery query = BuildScopeQuery(auth);
            query.VesselId = vesselId;
            query.Source = CheckRunSourceEnum.External;
            query.ProviderName = providerName;
            query.ExternalId = externalId;
            query.PageNumber = 1;
            query.PageSize = 5;

            EnumerationResult<CheckRun> results = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
            return results.Objects.FirstOrDefault();
        }

        private async Task<Vessel?> ReadAccessibleVesselAsync(AuthContext auth, string vesselId, CancellationToken token)
        {
            if (auth.IsAdmin)
                return await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (auth.IsTenantAdmin)
                return await _Database.Vessels.ReadAsync(auth.TenantId!, vesselId, token).ConfigureAwait(false);
            return await _Database.Vessels.ReadAsync(auth.TenantId!, auth.UserId!, vesselId, token).ConfigureAwait(false);
        }

        private async Task<CommandExecutionResult> ExecuteCommandAsync(string command, string workingDirectory, TimeSpan timeout, CancellationToken token)
        {
            bool isWindows = OperatingSystem.IsWindows();

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/sh",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (isWindows)
            {
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(command);
            }
            else
            {
                startInfo.ArgumentList.Add("-lc");
                startInfo.ArgumentList.Add(command);
            }

            using Process process = new Process
            {
                StartInfo = startInfo
            };

            if (!process.Start())
                throw new InvalidOperationException("Failed to start check command.");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(true);
                }
                catch
                {
                }

                throw new TimeoutException("Check command timed out after " + timeout.TotalMinutes.ToString("0") + " minutes.");
            }

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            string output = CombineOutput(stdout, stderr);

            _Logging.Debug(_Header + "command exited with code " + process.ExitCode + ": " + FirstNonEmptyLine(stderr, stdout));

            return new CommandExecutionResult
            {
                ExitCode = process.ExitCode,
                Output = output
            };
        }

        private static string CombineOutput(string stdout, string stderr)
        {
            string trimmedStdout = stdout?.Trim() ?? String.Empty;
            string trimmedStderr = stderr?.Trim() ?? String.Empty;

            if (String.IsNullOrWhiteSpace(trimmedStderr)) return trimmedStdout;
            if (String.IsNullOrWhiteSpace(trimmedStdout)) return trimmedStderr;
            return trimmedStdout + Environment.NewLine + Environment.NewLine + "--- STDERR ---" + Environment.NewLine + trimmedStderr;
        }

        private static string FirstNonEmptyLine(string? primary, string? secondary)
        {
            foreach (string source in new[] { primary ?? String.Empty, secondary ?? String.Empty })
            {
                foreach (string line in source.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = line.Trim();
                    if (!String.IsNullOrWhiteSpace(trimmed))
                        return trimmed;
                }
            }

            return String.Empty;
        }

        private static List<CheckRunArtifact> CollectArtifacts(string workingDirectory, List<string>? expectedArtifacts)
        {
            List<CheckRunArtifact> results = new List<CheckRunArtifact>();
            if (String.IsNullOrWhiteSpace(workingDirectory) || expectedArtifacts == null) return results;

            string root = Path.GetFullPath(workingDirectory);
            foreach (string relativePath in expectedArtifacts.Where(path => !String.IsNullOrWhiteSpace(path)))
            {
                try
                {
                    string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
                    if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(fullPath)) continue;

                    FileInfo info = new FileInfo(fullPath);
                    results.Add(new CheckRunArtifact
                    {
                        Path = relativePath.Replace('\\', '/'),
                        SizeBytes = info.Length,
                        LastWriteUtc = info.LastWriteTimeUtc
                    });
                }
                catch
                {
                }
            }

            return results;
        }

        private static string BuildSummary(CheckRun run, WorkflowProfile? profile)
        {
            string label = !String.IsNullOrWhiteSpace(run.Label)
                ? run.Label!
                : run.Type.ToString();

            string? testDetails = BuildTestSummaryText(run.TestSummary);
            string? coverageDetails = BuildCoverageSummaryText(run.CoverageSummary);

            if (run.Status == CheckRunStatusEnum.Passed)
            {
                List<string> parts = new List<string>();
                if (!String.IsNullOrWhiteSpace(testDetails))
                    parts.Add(testDetails);
                if (run.Artifacts.Count > 0)
                    parts.Add("collected " + run.Artifacts.Count + " artifact(s)");
                if (!String.IsNullOrWhiteSpace(coverageDetails))
                    parts.Add(coverageDetails);

                if (parts.Count > 0)
                    return label + " passed. " + String.Join("; ", parts) + ".";
                return label + " passed.";
            }

            if (!String.IsNullOrWhiteSpace(testDetails))
            {
                if (!String.IsNullOrWhiteSpace(coverageDetails))
                    return label + " failed. " + testDetails + "; " + coverageDetails + ".";
                return label + " failed. " + testDetails + ".";
            }

            string details = FirstNonEmptyLine(run.Output, null);
            if (String.IsNullOrWhiteSpace(details))
                details = "Exit code " + (run.ExitCode?.ToString() ?? "unknown");

            return label + " failed. " + details;
        }

        private static string? BuildTestSummaryText(CheckRunTestSummary? summary)
        {
            if (summary == null)
                return null;

            List<string> parts = new List<string>();
            if (summary.Passed.HasValue)
                parts.Add(summary.Passed.Value + " passed");
            if (summary.Failed.HasValue)
                parts.Add(summary.Failed.Value + " failed");
            if (summary.Skipped.HasValue && summary.Skipped.Value > 0)
                parts.Add(summary.Skipped.Value + " skipped");
            if (summary.Total.HasValue)
                parts.Add(summary.Total.Value + " total");
            if (summary.DurationMs.HasValue)
                parts.Add("in " + FormatDuration(summary.DurationMs.Value));

            return parts.Count > 0 ? String.Join(", ", parts) : null;
        }

        private static string? BuildCoverageSummaryText(CheckRunCoverageSummary? summary)
        {
            if (summary == null)
                return null;

            if (summary.Lines?.Percentage.HasValue == true)
                return "line coverage " + summary.Lines.Percentage.Value.ToString("0.##") + "%";
            if (summary.Statements?.Percentage.HasValue == true)
                return "statement coverage " + summary.Statements.Percentage.Value.ToString("0.##") + "%";
            if (summary.Functions?.Percentage.HasValue == true)
                return "function coverage " + summary.Functions.Percentage.Value.ToString("0.##") + "%";
            if (summary.Branches?.Percentage.HasValue == true)
                return "branch coverage " + summary.Branches.Percentage.Value.ToString("0.##") + "%";
            return null;
        }

        private static string FormatDuration(long durationMs)
        {
            if (durationMs < 1000)
                return durationMs + " ms";

            TimeSpan duration = TimeSpan.FromMilliseconds(durationMs);
            if (duration.TotalMinutes >= 1)
                return duration.TotalMinutes.ToString("0.##") + " min";

            return duration.TotalSeconds.ToString("0.##") + " s";
        }

        private static string? NormalizeValue(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static bool IsIsolatedCheckoutType(CheckRunTypeEnum type)
        {
            return type == CheckRunTypeEnum.Build || type == CheckRunTypeEnum.UnitTest;
        }

        private static string? ResolveRepoSource(Vessel vessel)
        {
            if (!String.IsNullOrWhiteSpace(vessel.LocalPath) && Directory.Exists(vessel.LocalPath))
                return vessel.LocalPath;
            if (!String.IsNullOrWhiteSpace(vessel.RepoUrl))
                return vessel.RepoUrl;
            return null;
        }

        private static string StripNoRestore(string command)
        {
            string result = Regex.Replace(command, @"\s*--no-restore(?=\s|$)", String.Empty, RegexOptions.IgnoreCase);
            return result.Trim();
        }

        private static void SafeDeleteDirectory(string? path)
        {
            if (String.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                    }
                }
                Directory.Delete(path, true);
            }
            catch { }
        }

        private async Task<string?> TryCloneToTempAsync(
            string repoSource,
            string? commitHash,
            string? branchName,
            string defaultBranch,
            CancellationToken token)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "armada-chk-" + Guid.NewGuid().ToString("N"));
            try
            {
                int cloneExit = await RunGitAsync(
                    Path.GetTempPath(),
                    TimeSpan.FromMinutes(10),
                    token,
                    "clone", "--", repoSource, tempPath).ConfigureAwait(false);

                if (cloneExit != 0)
                {
                    _Logging.Warn(_Header + "isolated checkout: git clone failed for " + repoSource);
                    SafeDeleteDirectory(tempPath);
                    return null;
                }

                string checkoutRef = !String.IsNullOrWhiteSpace(commitHash)
                    ? commitHash!
                    : !String.IsNullOrWhiteSpace(branchName)
                        ? branchName!
                        : defaultBranch;

                if (!String.IsNullOrWhiteSpace(checkoutRef)
                    && !String.Equals(checkoutRef, defaultBranch, StringComparison.OrdinalIgnoreCase))
                {
                    // Place the ref BEFORE the "--" separator so git treats it as a branch/commit to
                    // switch to, not as a pathspec to restore. "checkout -- <ref>" silently fails for any
                    // branch/commit (the ref is interpreted as a path), leaving HEAD on the clone default.
                    int checkoutExit = await RunGitAsync(
                        tempPath,
                        TimeSpan.FromMinutes(2),
                        token,
                        "checkout", checkoutRef, "--").ConfigureAwait(false);

                    if (checkoutExit != 0 && !String.IsNullOrWhiteSpace(commitHash))
                    {
                        string fallbackRef = !String.IsNullOrWhiteSpace(branchName)
                            ? branchName!
                            : defaultBranch;
                        if (!String.Equals(fallbackRef, checkoutRef, StringComparison.OrdinalIgnoreCase))
                        {
                            await RunGitAsync(
                                tempPath,
                                TimeSpan.FromMinutes(2),
                                token,
                                "checkout", fallbackRef, "--").ConfigureAwait(false);
                        }
                    }
                }

                _Logging.Debug(_Header + "isolated checkout created at " + tempPath);
                return tempPath;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                SafeDeleteDirectory(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "isolated checkout failed: " + ex.Message);
                SafeDeleteDirectory(tempPath);
                return null;
            }
        }

        private static async Task<int> RunGitAsync(
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken token,
            params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            using Process process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return -1;

            Task<string> drainStdout = process.StandardOutput.ReadToEndAsync();
            Task<string> drainStderr = process.StandardError.ReadToEndAsync();

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                throw;
            }

            await drainStdout.ConfigureAwait(false);
            await drainStderr.ConfigureAwait(false);
            return process.ExitCode;
        }

        private sealed class CommandExecutionResult
        {
            public int ExitCode { get; set; } = -1;
            public string Output { get; set; } = String.Empty;
        }
    }
}
