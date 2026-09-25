namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
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

        /// <summary>
        /// Directory-name prefix of the private checkout a check run executes in. The check run id
        /// follows it, so a storage sweep can recognize the checkout of a live check.
        /// </summary>
        public const string CheckoutDirectoryPrefix = "armada-chk-";

        /// <summary>
        /// Per-stream budget for a check command's output: 4 MiB. Past it the beginning and the end are kept, where
        /// a build's first errors and a test runner's totals are, with a marker naming the omitted bytes.
        /// </summary>
        public const int CheckCommandOutputLimitBytes = 4 * 1024 * 1024;

        private const string _WorktreeLockReason = "armada check run in progress";

        private readonly string _Header = "[CheckRunService] ";
        private readonly DatabaseDriver _Database;
        private readonly WorkflowProfileService _WorkflowProfiles;
        private readonly VesselReadinessService _Readiness;
        private readonly LoggingModule _Logging;
        private readonly Func<IReadOnlyList<BannedDiffPatternRule>>? _BannedDiffPatterns;
        private readonly TimeSpan _DefaultTimeout = TimeSpan.FromMinutes(30);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _PendingRunLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _CheckoutRepoLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CheckRunService(
            DatabaseDriver database,
            WorkflowProfileService workflowProfiles,
            VesselReadinessService readiness,
            LoggingModule logging,
            Func<IReadOnlyList<BannedDiffPatternRule>>? bannedDiffPatterns = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _WorkflowProfiles = workflowProfiles ?? throw new ArgumentNullException(nameof(workflowProfiles));
            _Readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _BannedDiffPatterns = bannedDiffPatterns;
        }

        /// <summary>
        /// Cancel every Check left Running by an earlier process. A check executes inside the
        /// Admiral process, so a record that was Running before this process started has no command
        /// behind it any more and can never reach a verdict. Left alone it counts as unresolved
        /// forever, which holds a Judge PASS and then rejects it. A record started by this process
        /// is untouched.
        /// </summary>
        /// <param name="processStartUtc">The moment this process started.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of records cancelled.</returns>
        public async Task<int> CancelInterruptedRunsAsync(DateTime processStartUtc, CancellationToken token = default)
        {
            List<CheckRun> interrupted = new List<CheckRun>();
            CheckRunQuery query = new CheckRunQuery
            {
                Status = CheckRunStatusEnum.Running,
                PageNumber = 1,
                PageSize = 200
            };

            while (true)
            {
                EnumerationResult<CheckRun> page = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
                foreach (CheckRun run in page.Objects)
                {
                    if (run == null) continue;
                    // A record with no start time cannot have been started by this process either.
                    if (run.StartedUtc.HasValue && run.StartedUtc.Value >= processStartUtc) continue;
                    interrupted.Add(run);
                }

                if (page.Objects.Count < query.PageSize || query.PageNumber >= page.TotalPages) break;
                query.PageNumber++;
            }

            int cancelled = 0;
            foreach (CheckRun run in interrupted)
            {
                DateTime now = DateTime.UtcNow;
                run.Status = CheckRunStatusEnum.Canceled;
                run.Summary = "admiral restart: this Check was Running when the admiral process stopped, so its "
                    + "command never finished and the record carries no verdict.";
                run.Output = String.IsNullOrWhiteSpace(run.Output) ? run.Summary : run.Output;
                run.CompletedUtc = now;
                run.LastUpdateUtc = now;
                CheckRun updated = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
                OnCheckRunChanged?.Invoke(updated);
                cancelled++;
            }

            if (cancelled > 0)
                _Logging.Warn(_Header + "cancelled " + cancelled + " check run" + (cancelled == 1 ? "" : "s") + " left Running by a stopped admiral");

            return cancelled;
        }

        /// <summary>
        /// Refusal message for a command override from a caller that is not a global administrator.
        /// </summary>
        public const string CommandOverrideRefusal =
            "Only a global administrator can run a check with a command override; other callers run the command the workflow profile resolves.";

        /// <summary>
        /// Execute a check run synchronously and persist the result.
        /// </summary>
        /// <remarks>
        /// A command override is a raw shell command run as the server process, so only a global administrator may
        /// send one; anyone else gets <see cref="UnauthorizedAccessException"/> before any record or process exists.
        /// Deploy and Rollback checks run only through the deployment workflow, which passes its approval.
        /// </remarks>
        public Task<CheckRun> RunAsync(AuthContext auth, CheckRunRequest request, CancellationToken token = default)
        {
            return RunCoreAsync(auth, request, false, token);
        }

        private async Task<CheckRun> RunCoreAsync(AuthContext auth, CheckRunRequest request, bool allowDeploymentExecution, CancellationToken token)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.VesselId)) throw new ArgumentNullException(nameof(request.VesselId));

            if (!String.IsNullOrWhiteSpace(request.CommandOverride) && !auth.IsAdmin)
                throw new UnauthorizedAccessException(CommandOverrideRefusal);

            if (IsDeploymentExecutionType(request.Type))
            {
                if (String.IsNullOrWhiteSpace(request.DeploymentId))
                    throw new InvalidOperationException(request.Type + " checks must be linked to a deployment.");
                if (!allowDeploymentExecution)
                    throw new InvalidOperationException("Deployment-linked checks must be executed through the deployment workflow.");
            }

            Vessel vessel = await ReadAccessibleVesselAsync(auth, request.VesselId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Vessel not found or not accessible.");
            await EnsureLinkedRecordsAccessibleAsync(auth, request.MissionId, request.VoyageId, request.DeploymentId, request.RegressionObjectiveId, token).ConfigureAwait(false);

            if (request.Type == CheckRunTypeEnum.Slop && String.IsNullOrWhiteSpace(request.CommandOverride))
                return await RunNewSlopAsync(auth, vessel, request, token).ConfigureAwait(false);

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
                Status = CheckRunStatusEnum.Pending,
                EnvironmentName = request.EnvironmentName,
                Command = command,
                WorkingDirectory = vessel.WorkingDirectory,
                BranchName = request.BranchName,
                CommitHash = request.CommitHash,
                RegressionPurpose = RegressionLinkRules.ValidatedCheckPurpose(request.RegressionPurpose, request.RegressionObjectiveId, request.RegressionLandedCommit),
                RegressionObjectiveId = RegressionLinkRules.NormalizeObjectiveId(request.RegressionObjectiveId),
                RegressionLandedCommit = RegressionLinkRules.NormalizeCommit(request.RegressionLandedCommit),
                CreatedUtc = DateTime.UtcNow,
                LastUpdateUtc = DateTime.UtcNow
            };

            SemaphoreSlim runLock = _PendingRunLocks.GetOrAdd(run.Id, _ => new SemaphoreSlim(1, 1));
            await runLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                run = await _Database.CheckRuns.CreateAsync(run, token).ConfigureAwait(false);
                OnCheckRunChanged?.Invoke(run);

                IsolatedCheckout? isolatedCheckout = null;

                long executionDurationMs = 0;
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
                            isolatedCheckout = await TryCreateIsolatedCheckoutAsync(vessel, run.Id, run.CommitHash, run.BranchName, vessel.DefaultBranch, token).ConfigureAwait(false);
                            if (isolatedCheckout != null)
                            {
                                executionDirectory = isolatedCheckout.Path;
                                executionCommand = StripNoRestore(run.Command);
                            }
                            else
                            {
                                return await CompleteExistingRunAsFailureAsync(
                                    run,
                                    "Isolated checkout could not be created for the configured repo source. The check will not execute in the live working directory.",
                                    token).ConfigureAwait(false);
                            }
                        }
                    }

                    // Share the host-wide slot with DoD gates and merge-queue test runs so two full
                    // build+test suites never run on one host at once. See HostWideCommandLock.
                    run.SlotRequestedUtc = DateTime.UtcNow;
                    run.LastUpdateUtc = run.SlotRequestedUtc.Value;
                    run = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
                    using (await HostWideCommandLock.AcquireAsync(token).ConfigureAwait(false))
                    {
                        run.Status = CheckRunStatusEnum.Running;
                        run.StartedUtc = DateTime.UtcNow;
                        run.LastUpdateUtc = run.StartedUtc.Value;
                        run = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
                        OnCheckRunChanged?.Invoke(run);
                        Stopwatch sw = Stopwatch.StartNew();
                        execution = await ExecuteCommandAsync(
                            executionCommand, executionDirectory, _DefaultTimeout, token, profile?.EnvironmentVariables).ConfigureAwait(false);
                        sw.Stop();
                        executionDurationMs = Convert.ToInt64(Math.Round(sw.Elapsed.TotalMilliseconds));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    await CleanupIsolatedCheckoutAsync(isolatedCheckout, CancellationToken.None).ConfigureAwait(false);
                    run.Status = CheckRunStatusEnum.Pending;
                    run.StartedUtc = null;
                    run.SlotRequestedUtc = null;
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

                run.ExitCode = execution.ExitCode;
                run.Output = execution.Output;
                run.DurationMs = executionDurationMs;
                run.CompletedUtc = DateTime.UtcNow;
                run.LastUpdateUtc = DateTime.UtcNow;
                run.Status = execution.ExitCode == 0 ? CheckRunStatusEnum.Passed : CheckRunStatusEnum.Failed;

                string artifactDirectory = isolatedCheckout?.Path ?? run.WorkingDirectory!;
                run.Artifacts = CollectArtifacts(artifactDirectory, profile?.ExpectedArtifacts);
                run.TestSummary = CheckRunParsingService.ParseTestSummary(run.Output, artifactDirectory, run.Artifacts);
                run.CoverageSummary = CheckRunParsingService.ParseCoverageSummary(artifactDirectory, run.Artifacts);
                run.Summary = BuildSummary(run, profile);

                await CleanupIsolatedCheckoutAsync(isolatedCheckout, token).ConfigureAwait(false);

                run = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
                OnCheckRunChanged?.Invoke(run);
                return run;
            }
            finally
            {
                runLock.Release();
            }
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

            if (IsNativeSlopRun(run))
                return await ExecuteSlopRunAsync(run, vessel, token).ConfigureAwait(false);

            CheckRun? unstamped = await HandleUnstampedVoyageRecordAsync(run, token).ConfigureAwait(false);
            if (unstamped != null) return unstamped;

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

            return await RunCoreAsync(auth, request, allowDeploymentExecution, token).ConfigureAwait(false);
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
            await EnsureLinkedRecordsAccessibleAsync(auth, request.MissionId, request.VoyageId, request.DeploymentId, request.RegressionObjectiveId, token).ConfigureAwait(false);

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
            await EnsureLinkedRecordsAccessibleAsync(auth, request.MissionId, request.VoyageId, request.DeploymentId, request.RegressionObjectiveId, token).ConfigureAwait(false);

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
            // An imported record carries a command Armada never resolved, and any stored command is the same raw
            // shell text an override is, so only a global administrator re-executes it verbatim. Everyone else
            // retries with the command the workflow profile resolves now.
            if (prior.Source == CheckRunSourceEnum.External && !auth.IsAdmin)
                throw new UnauthorizedAccessException("Only a global administrator can re-execute an imported check; run a new check instead.");
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
                RegressionPurpose = prior.RegressionPurpose,
                RegressionObjectiveId = prior.RegressionObjectiveId,
                RegressionLandedCommit = prior.RegressionLandedCommit,
                CommandOverride = auth.IsAdmin ? prior.Command : null
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
            return CheckRunGateRules.HasUnresolvedCommand(run);
        }

        /// <summary>
        /// True when a record is executed by the native Slop classifier rather than a shell command.
        /// A Slop record carrying an operator-supplied command runs that command instead.
        /// </summary>
        private static bool IsNativeSlopRun(CheckRun run)
        {
            if (run.Type != CheckRunTypeEnum.Slop) return false;
            return CheckRunGateRules.HasUnresolvedCommand(run)
                || String.Equals(run.Command, SlopCheckRunner.CommandLabel, StringComparison.Ordinal);
        }

        private async Task<CheckRun> RunNewSlopAsync(AuthContext auth, Vessel vessel, CheckRunRequest request, CancellationToken token)
        {
            WorkflowProfile? profile = await _WorkflowProfiles.ResolveForVesselAsync(auth, vessel, request.WorkflowProfileId, token).ConfigureAwait(false);

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
                Type = CheckRunTypeEnum.Slop,
                Source = CheckRunSourceEnum.Armada,
                Status = CheckRunStatusEnum.Pending,
                EnvironmentName = request.EnvironmentName,
                Command = SlopCheckRunner.CommandLabel,
                WorkingDirectory = vessel.LocalPath ?? vessel.WorkingDirectory,
                BranchName = request.BranchName,
                CommitHash = request.CommitHash,
                CreatedUtc = DateTime.UtcNow,
                LastUpdateUtc = DateTime.UtcNow
            };

            SemaphoreSlim runLock = _PendingRunLocks.GetOrAdd(run.Id, _ => new SemaphoreSlim(1, 1));
            await runLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                run = await _Database.CheckRuns.CreateAsync(run, token).ConfigureAwait(false);
                OnCheckRunChanged?.Invoke(run);
                return await ExecuteSlopRunAsync(run, vessel, token).ConfigureAwait(false);
            }
            finally
            {
                runLock.Release();
            }
        }

        /// <summary>
        /// Execute a Slop check against the reviewed diff. A voyage-armed record that was never
        /// stamped is pointed at the voyage's work under review first. Every condition that prevents
        /// classification fails the record with its reason; none of them passes it.
        /// </summary>
        private async Task<CheckRun> ExecuteSlopRunAsync(CheckRun run, Vessel vessel, CancellationToken token)
        {
            run.Command = SlopCheckRunner.CommandLabel;

            if (String.IsNullOrWhiteSpace(run.CommitHash)
                && String.IsNullOrWhiteSpace(run.BranchName)
                && !String.IsNullOrWhiteSpace(run.VoyageId))
            {
                List<Mission> missions = await _Database.Missions.EnumerateByVoyageAsync(run.VoyageId!, token).ConfigureAwait(false);
                Mission? work = StaleCheckSupersessionService.SelectWorkUnderReview(missions);
                if (work != null)
                {
                    run.BranchName = work.BranchName;
                    run.CommitHash = work.CommitHash;
                }
            }

            // No reviewable work: the Slop check has nothing to classify. It follows the same rule as
            // the checkout Checks, so a voyage that ended before any stage produced reviewable work
            // cancels the record with that reason instead of failing it and raising an incident
            // about a diff that never existed, and a live voyage leaves it waiting.
            if (String.IsNullOrWhiteSpace(run.CommitHash) && String.IsNullOrWhiteSpace(run.BranchName))
            {
                CheckRun? decided = await DecideUnstampedVoyageRecordAsync(run, token).ConfigureAwait(false);
                if (decided != null) return decided;
            }

            string? repoPath = !String.IsNullOrWhiteSpace(vessel.LocalPath) && Directory.Exists(vessel.LocalPath)
                ? vessel.LocalPath
                : vessel.WorkingDirectory;
            run.WorkingDirectory = repoPath;

            run.Status = CheckRunStatusEnum.Running;
            run.StartedUtc = DateTime.UtcNow;
            run.LastUpdateUtc = run.StartedUtc.Value;
            run = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
            OnCheckRunChanged?.Invoke(run);

            Stopwatch sw = Stopwatch.StartNew();
            SlopCheckOutcome outcome;
            try
            {
                outcome = await new SlopCheckRunner(_Logging, _BannedDiffPatterns)
                    .RunAsync(repoPath, run.CommitHash, run.BranchName, vessel.DefaultBranch, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                run.Status = CheckRunStatusEnum.Pending;
                run.StartedUtc = null;
                run.LastUpdateUtc = DateTime.UtcNow;
                run = await _Database.CheckRuns.UpdateAsync(run, CancellationToken.None).ConfigureAwait(false);
                OnCheckRunChanged?.Invoke(run);
                throw;
            }
            catch (Exception ex)
            {
                outcome = SlopCheckOutcome.Failure("The Slop classifier stopped with an error: " + ex.Message);
            }
            sw.Stop();

            DateTime now = DateTime.UtcNow;
            run.ExitCode = outcome.Passed ? 0 : (outcome.Completed ? 1 : -1);
            run.Status = outcome.Passed ? CheckRunStatusEnum.Passed : CheckRunStatusEnum.Failed;
            run.Output = outcome.Report;
            run.Summary = outcome.Summary;
            run.DurationMs = Convert.ToInt64(Math.Round(sw.Elapsed.TotalMilliseconds));
            run.CompletedUtc = now;
            run.LastUpdateUtc = now;
            if (!String.IsNullOrWhiteSpace(outcome.HeadCommit)) run.CommitHash = outcome.HeadCommit;

            run = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
            OnCheckRunChanged?.Invoke(run);
            return run;
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

            IsolatedCheckout? isolatedCheckout = null;
            string executionDirectory = run.WorkingDirectory!;
            string executionCommand = run.Command;

            if (IsIsolatedCheckoutType(run.Type))
            {
                string? repoSource = ResolveRepoSource(vessel);
                if (repoSource != null)
                {
                    isolatedCheckout = await TryCreateIsolatedCheckoutAsync(vessel, run.Id, run.CommitHash, run.BranchName, vessel.DefaultBranch, token).ConfigureAwait(false);
                    if (isolatedCheckout != null)
                    {
                        executionDirectory = isolatedCheckout.Path;
                        executionCommand = StripNoRestore(run.Command);
                    }
                    else
                    {
                        return await CompleteExistingRunAsFailureAsync(
                            run,
                            "Isolated checkout could not be created for the configured repo source. The check will not execute in the live working directory.",
                            token).ConfigureAwait(false);
                    }
                }
            }

            long executionDurationMs = 0;
            CommandExecutionResult execution;

            try
            {
                run.SlotRequestedUtc = DateTime.UtcNow;
                run.LastUpdateUtc = run.SlotRequestedUtc.Value;
                run = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
                using (await HostWideCommandLock.AcquireAsync(token).ConfigureAwait(false))
                {
                    run.Status = CheckRunStatusEnum.Running;
                    run.StartedUtc = DateTime.UtcNow;
                    run.LastUpdateUtc = run.StartedUtc.Value;
                    run = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
                    OnCheckRunChanged?.Invoke(run);
                    Stopwatch sw = Stopwatch.StartNew();
                    execution = await ExecuteCommandAsync(
                        executionCommand, executionDirectory, _DefaultTimeout, token, profile?.EnvironmentVariables).ConfigureAwait(false);
                    sw.Stop();
                    executionDurationMs = Convert.ToInt64(Math.Round(sw.Elapsed.TotalMilliseconds));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await CleanupIsolatedCheckoutAsync(isolatedCheckout, CancellationToken.None).ConfigureAwait(false);
                run.Status = CheckRunStatusEnum.Pending;
                run.StartedUtc = null;
                run.SlotRequestedUtc = null;
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

            run.ExitCode = execution.ExitCode;
            run.Output = execution.Output;
            run.DurationMs = executionDurationMs;
            run.CompletedUtc = DateTime.UtcNow;
            run.LastUpdateUtc = DateTime.UtcNow;
            run.Status = execution.ExitCode == 0 ? CheckRunStatusEnum.Passed : CheckRunStatusEnum.Failed;

            string artifactDirectory = isolatedCheckout?.Path ?? run.WorkingDirectory!;
            run.Artifacts = CollectArtifacts(artifactDirectory, profile?.ExpectedArtifacts);
            run.TestSummary = CheckRunParsingService.ParseTestSummary(run.Output, artifactDirectory, run.Artifacts);
            run.CoverageSummary = CheckRunParsingService.ParseCoverageSummary(artifactDirectory, run.Artifacts);
            run.Summary = BuildSummary(run, profile);

            await CleanupIsolatedCheckoutAsync(isolatedCheckout, token).ConfigureAwait(false);

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
                RegressionPurpose = RegressionLinkRules.ValidatedCheckPurpose(request.RegressionPurpose, request.RegressionObjectiveId, request.RegressionLandedCommit),
                RegressionObjectiveId = RegressionLinkRules.NormalizeObjectiveId(request.RegressionObjectiveId),
                RegressionLandedCommit = RegressionLinkRules.NormalizeCommit(request.RegressionLandedCommit),
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

        /// <summary>
        /// Refuse a mission, voyage or deployment link outside the caller's tenant. A check record counts in the gates
        /// of whatever it links to, so a link outside the tenant would let one tenant write evidence into another
        /// tenant's Judge and voyage gates. A global administrator may link any record.
        /// </summary>
        private async Task EnsureLinkedRecordsAccessibleAsync(
            AuthContext auth,
            string? missionId,
            string? voyageId,
            string? deploymentId,
            string? regressionObjectiveId,
            CancellationToken token)
        {
            if (auth.IsAdmin) return;
            string tenantId = auth.TenantId ?? String.Empty;

            // The production summary attributes a regression Check to the objective it names, so the link is
            // read in the caller's scope like any other id in the request body.
            if (!String.IsNullOrWhiteSpace(regressionObjectiveId)
                && await CallerScopedRead.ReadObjectiveAsync(_Database, auth, regressionObjectiveId.Trim(), token).ConfigureAwait(false) == null)
                throw new InvalidOperationException("Regression objective not found or not accessible.");

            if (!String.IsNullOrWhiteSpace(missionId)
                && await _Database.Missions.ReadAsync(tenantId, missionId, token).ConfigureAwait(false) == null)
                throw new InvalidOperationException("Mission not found or not accessible.");

            if (!String.IsNullOrWhiteSpace(voyageId)
                && await _Database.Voyages.ReadAsync(tenantId, voyageId, token).ConfigureAwait(false) == null)
                throw new InvalidOperationException("Voyage not found or not accessible.");

            if (!String.IsNullOrWhiteSpace(deploymentId)
                && await _Database.Deployments.ReadAsync(deploymentId, new DeploymentQuery { TenantId = tenantId }, token).ConfigureAwait(false) == null)
                throw new InvalidOperationException("Deployment not found or not accessible.");
        }

        private async Task<CommandExecutionResult> ExecuteCommandAsync(
            string command,
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken token,
            IReadOnlyDictionary<string, string>? environmentVariables = null)
        {
            bool isWindows = OperatingSystem.IsWindows();

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/sh",
                WorkingDirectory = workingDirectory
            };

            // Applied on top of the inherited environment, so a profile adds to the dock's shell
            // rather than replacing it. A check that needs a variable is otherwise unrunnable in
            // every dock, whatever the command says.
            if (environmentVariables != null)
            {
                foreach (KeyValuePair<string, string> variable in environmentVariables)
                {
                    if (String.IsNullOrWhiteSpace(variable.Key)) continue;
                    startInfo.Environment[variable.Key] = variable.Value ?? String.Empty;
                }
            }

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

            // The check owns its process group, so a timeout or cancellation also stops a background child it
            // started, and a build server left holding the output pipe cannot keep the run open after exit.
            BoundedProcessRequest request = new BoundedProcessRequest(startInfo, timeout)
            {
                OutputLimitBytes = CheckCommandOutputLimitBytes,
                OwnProcessGroup = true
            };
            BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request, token).ConfigureAwait(false);
            if (result.KillError != null)
                _Logging.Warn(_Header + "could not kill check command; it may still be running: " + result.KillError);
            if (result.Cancelled)
            {
                token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(token);
            }
            if (result.TimedOut)
                throw new TimeoutException("Check command timed out after " + timeout.TotalMinutes.ToString("0") + " minutes.");

            string anomalies = BoundedProcessRunner.DescribeAnomalies(result);
            if (anomalies.Length > 0)
                _Logging.Warn(_Header + "check command output: " + anomalies);

            string stdout = result.StandardOutput;
            string stderr = result.StandardError;
            string output = CombineOutput(stdout, stderr);
            int exitCode = result.ExitCode ?? -1;

            _Logging.Debug(_Header + "command exited with code " + exitCode + ": " + FirstNonEmptyLine(stderr, stdout));

            return new CommandExecutionResult
            {
                ExitCode = exitCode,
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
                    string? fullPath = PathContainment.TryResolve(root, relativePath);
                    if (fullPath == null) continue;
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

        /// <summary>
        /// Decide what a voyage-armed record with no stamped branch or commit may do at execution
        /// time. Build and UnitTest resolve their checkout from the record, so executing one
        /// unstamped measures the vessel's default branch and reports base-branch failures as
        /// failures of the work under review. A live voyage has simply not committed yet, so the
        /// record waits for its stamp. A voyage that ended before any stage stamped it can never be
        /// measured, so the record is cancelled with that reason. A completed voyage is left to run,
        /// because its work is on the default branch by then. Returns null when the record may
        /// execute, and otherwise the record in its decided state.
        /// </summary>
        private async Task<CheckRun?> HandleUnstampedVoyageRecordAsync(CheckRun run, CancellationToken token)
        {
            if (!IsIsolatedCheckoutType(run.Type)) return null;
            return await DecideUnstampedVoyageRecordAsync(run, token).ConfigureAwait(false);
        }

        /// <summary>
        /// The unstamped-record rule itself, for any Check type whose subject is the voyage's work:
        /// null when the record may execute, otherwise the record left Pending (a live voyage that has
        /// not committed yet) or Canceled (a voyage that ended before any stage stamped it).
        /// </summary>
        private async Task<CheckRun?> DecideUnstampedVoyageRecordAsync(CheckRun run, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(run.VoyageId)) return null;
            if (!String.IsNullOrWhiteSpace(run.BranchName) || !String.IsNullOrWhiteSpace(run.CommitHash)) return null;

            Voyage? voyage = await _Database.Voyages.ReadAsync(run.VoyageId!, token).ConfigureAwait(false);
            if (voyage != null && voyage.Status == VoyageStatusEnum.Complete) return null;

            string? endedReason = voyage == null ? "voyage_missing" : VoyageCheckDiscard.ReasonFor(voyage.Status);
            if (endedReason == null)
            {
                _Logging.Info(_Header + "check " + run.Id + " stays pending: its voyage has not stamped a branch on it yet");
                return run;
            }

            DateTime now = DateTime.UtcNow;
            run.Status = CheckRunStatusEnum.Canceled;
            run.Summary = "unstamped_voyage_check (" + endedReason + "): this Check carries no branch or commit, "
                + "and its voyage ended before a stage stamped one. Running it would measure the default branch, "
                + "not the work under review.";
            run.Output = run.Summary;
            run.CompletedUtc = now;
            run.LastUpdateUtc = now;
            run = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
            OnCheckRunChanged?.Invoke(run);
            _Logging.Warn(_Header + "cancelled unstamped check " + run.Id + " (" + endedReason + ")");
            return run;
        }

        /// <summary>
        /// Build the path of a check's private checkout. The check run id is part of the directory
        /// name so a reclaim sweep can tell a checkout that a live check is executing in from a
        /// leftover one, without a second record of what is in flight.
        /// </summary>
        private static string BuildCheckoutPath(string checkRunId)
        {
            string suffix = String.IsNullOrWhiteSpace(checkRunId) ? String.Empty : checkRunId.Trim() + "-";
            return Path.Combine(Path.GetTempPath(), CheckoutDirectoryPrefix + suffix + Guid.NewGuid().ToString("N"));
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

        /// <summary>
        /// Create the isolated checkout a Build or UnitTest check runs in. A detached worktree cut
        /// from the vessel's local repository is preferred, because that is exactly how a mission dock
        /// checks out: it inherits the repository's own config (core.autocrlf, core.eol, filters) and
        /// .gitattributes, so the checked-out bytes are faithful to what a dock produces. A fresh
        /// clone is used only as a fallback when the local repository is not usable.
        /// </summary>
        private async Task<IsolatedCheckout?> TryCreateIsolatedCheckoutAsync(
            Vessel vessel,
            string checkRunId,
            string? commitHash,
            string? branchName,
            string defaultBranch,
            CancellationToken token)
        {
            string? localPath = (!String.IsNullOrWhiteSpace(vessel.LocalPath) && Directory.Exists(vessel.LocalPath))
                ? vessel.LocalPath
                : null;

            if (localPath != null)
            {
                IsolatedCheckout? worktree = await TryCreateDetachedWorktreeAsync(localPath, checkRunId, commitHash, branchName, defaultBranch, token).ConfigureAwait(false);
                if (worktree != null)
                {
                    return worktree;
                }
                _Logging.Warn(_Header + "isolated checkout: detached worktree creation failed for " + localPath + ", falling back to clone");
            }

            string? repoSource = ResolveRepoSource(vessel);
            if (repoSource == null)
            {
                return null;
            }

            string? clonePath = await TryCloneToTempAsync(repoSource, checkRunId, commitHash, branchName, defaultBranch, token).ConfigureAwait(false);
            if (clonePath == null)
            {
                return null;
            }
            return new IsolatedCheckout(clonePath, null);
        }

        /// <summary>
        /// Cut a detached worktree from <paramref name="repoPath"/> at the best resolvable ref
        /// (commit hash, then branch, then default branch). The worktree shares the repository's own
        /// config, so its checkout EOL and filters match a mission dock created from the same repo.
        /// </summary>
        private async Task<IsolatedCheckout?> TryCreateDetachedWorktreeAsync(
            string repoPath,
            string checkRunId,
            string? commitHash,
            string? branchName,
            string defaultBranch,
            CancellationToken token)
        {
            string tempPath = BuildCheckoutPath(checkRunId);
            SemaphoreSlim repoLock = _CheckoutRepoLocks.GetOrAdd(repoPath, _ => new SemaphoreSlim(1, 1));
            await repoLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Bring the repo up to date so a recently pushed commit or branch resolves, exactly as
                // DockService does before creating a mission worktree. A missing remote is not fatal.
                await RunGitAsync(repoPath, TimeSpan.FromMinutes(5), token, "fetch", "--prune", "origin").ConfigureAwait(false);

                string? sha = await ResolveCheckoutShaAsync(repoPath, commitHash, branchName, defaultBranch, token).ConfigureAwait(false);
                if (String.IsNullOrEmpty(sha))
                {
                    _Logging.Warn(_Header + "isolated checkout: no resolvable ref in " + repoPath);
                    return null;
                }

                int addExit = await RunGitAsync(repoPath, TimeSpan.FromMinutes(5), token,
                    "worktree", "add", "--detach", tempPath, sha).ConfigureAwait(false);
                if (addExit != 0)
                {
                    _Logging.Warn(_Header + "isolated checkout: git worktree add failed for " + repoPath);
                    SafeDeleteDirectory(tempPath);
                    return null;
                }

                // A prune run from a process that cannot see this directory removes an unlocked
                // worktree's admin entry while the check is still executing in it. A lock survives
                // every prune, whoever runs it.
                int lockExit = await RunGitAsync(repoPath, TimeSpan.FromMinutes(1), token,
                    "worktree", "lock", "--reason", _WorktreeLockReason, tempPath).ConfigureAwait(false);
                if (lockExit != 0)
                    _Logging.Warn(_Header + "isolated checkout: could not lock worktree " + tempPath + "; a prune can remove it mid-run");

                _Logging.Debug(_Header + "isolated checkout created at " + tempPath);
                return new IsolatedCheckout(tempPath, repoPath);
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
            finally
            {
                repoLock.Release();
            }
        }

        /// <summary>
        /// Resolve the requested checkout ref to a commit in the repository. The commit hash wins,
        /// then the branch name, then the default branch. Branch names resolve through local, origin
        /// remote, and bare-name spellings so the checkout works for both bare and normal repos.
        /// </summary>
        private async Task<string?> ResolveCheckoutShaAsync(
            string repoPath,
            string? commitHash,
            string? branchName,
            string defaultBranch,
            CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(commitHash))
            {
                string? sha = await ResolveRefCandidateAsync(repoPath, commitHash!, token).ConfigureAwait(false);
                if (!String.IsNullOrEmpty(sha))
                {
                    return sha;
                }
            }

            if (!String.IsNullOrWhiteSpace(branchName))
            {
                string? sha = await ResolveBranchCandidateAsync(repoPath, branchName!, token).ConfigureAwait(false);
                if (!String.IsNullOrEmpty(sha))
                {
                    return sha;
                }
            }

            return await ResolveBranchCandidateAsync(repoPath, defaultBranch, token).ConfigureAwait(false);
        }

        private async Task<string?> ResolveBranchCandidateAsync(string repoPath, string branchName, CancellationToken token)
        {
            string? sha = await ResolveRefCandidateAsync(repoPath, "refs/heads/" + branchName, token).ConfigureAwait(false);
            if (!String.IsNullOrEmpty(sha))
            {
                return sha;
            }
            sha = await ResolveRefCandidateAsync(repoPath, "refs/remotes/origin/" + branchName, token).ConfigureAwait(false);
            if (!String.IsNullOrEmpty(sha))
            {
                return sha;
            }
            return await ResolveRefCandidateAsync(repoPath, branchName, token).ConfigureAwait(false);
        }

        private async Task<string?> ResolveRefCandidateAsync(string repoPath, string reference, CancellationToken token)
        {
            try
            {
                GitCommandResult result = await RunGitCaptureAsync(repoPath, TimeSpan.FromMinutes(1), token,
                    "rev-parse", "--verify", reference + "^{commit}").ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    return null;
                }
                string sha = result.StdOut.Trim();
                return sha.Length == 40 ? sha : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Remove an isolated checkout and, for a worktree-backed checkout, its git worktree
        /// registration so the shared repository does not accumulate stale entries.
        /// </summary>
        private async Task CleanupIsolatedCheckoutAsync(IsolatedCheckout? checkout, CancellationToken token)
        {
            if (checkout == null)
            {
                return;
            }

            if (!String.IsNullOrWhiteSpace(checkout.RepoPath))
            {
                try
                {
                    // Removal is refused while the worktree is locked, so the lock that protected the
                    // run is released first.
                    await RunGitAsync(checkout.RepoPath, TimeSpan.FromMinutes(1), token,
                        "worktree", "unlock", checkout.Path).ConfigureAwait(false);
                    await RunGitAsync(checkout.RepoPath, TimeSpan.FromMinutes(2), token,
                        "worktree", "remove", "--force", checkout.Path).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "isolated checkout: could not remove worktree registration for " + checkout.Path + ": " + ex.Message);
                }
            }

            SafeDeleteDirectory(checkout.Path);

            if (!String.IsNullOrWhiteSpace(checkout.RepoPath))
            {
                try
                {
                    await RunGitAsync(checkout.RepoPath, TimeSpan.FromMinutes(1), token, "worktree", "prune").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "isolated checkout: worktree prune failed for " + checkout.RepoPath + ": " + ex.Message);
                }
            }
        }

        private async Task<string?> TryCloneToTempAsync(
            string repoSource,
            string checkRunId,
            string? commitHash,
            string? branchName,
            string defaultBranch,
            CancellationToken token)
        {
            string tempPath = BuildCheckoutPath(checkRunId);
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
            GitCommandResult result = await RunGitCaptureAsync(workingDirectory, timeout, token, args).ConfigureAwait(false);
            return result.ExitCode;
        }

        private static async Task<GitCommandResult> RunGitCaptureAsync(
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken token,
            params string[] args)
        {
            BoundedProcessRequest request = new BoundedProcessRequest(GitProcessStartInfo.Create(workingDirectory, args), timeout)
            {
                OutputLimitBytes = 1024 * 1024
            };
            BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request, token).ConfigureAwait(false);
            if (result.Cancelled)
            {
                token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(token);
            }
            if (result.TimedOut)
                throw new TimeoutException("git " + args[0] + " timed out after " + timeout.TotalSeconds.ToString("F0") + " seconds");

            return new GitCommandResult { ExitCode = result.ExitCode ?? -1, StdOut = result.StandardOutput };
        }

        private sealed class CommandExecutionResult
        {
            public int ExitCode { get; set; } = -1;
            public string Output { get; set; } = String.Empty;
        }

        /// <summary>
        /// A temporary checkout a check run executes in, with the repository it was cut from so the
        /// worktree registration can be removed at cleanup. <see cref="RepoPath"/> is null when the
        /// checkout came from a plain clone.
        /// </summary>
        private sealed class IsolatedCheckout
        {
            public IsolatedCheckout(string path, string? repoPath)
            {
                Path = path;
                RepoPath = repoPath;
            }

            public string Path { get; }

            public string? RepoPath { get; }
        }

        private sealed class GitCommandResult
        {
            public int ExitCode { get; set; } = -1;

            public string StdOut { get; set; } = String.Empty;
        }
    }
}
