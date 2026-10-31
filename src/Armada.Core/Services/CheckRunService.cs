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
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Executes structured check runs using workflow profiles and persists the results.
    /// </summary>
    public partial class CheckRunService
    {
        /// <summary>Settings used to resolve explicit Harbor-owned vessel routes for check runs.</summary>
        public HarborSettings? HarborSettings { get; set; }

        /// <summary>Owner-bound provider for checks that execute on a configured Harbor host.</summary>
        public HarborCheckoutService? HarborCheckouts { get; set; }

        /// <summary>
        /// Optional callback invoked whenever a check run is created or updated.
        /// </summary>
        public Action<CheckRun>? OnCheckRunChanged { get; set; }

        /// <summary>
        /// Whether a failed test Check re-runs its few failing tests once in isolation under the shared
        /// <see cref="IsolatedRerunPlan"/> rule. The server reads the definition-of-done setting
        /// <c>RerunFailingClassesOnce</c>, so the gate and the Checks follow one switch. Defaults to on.
        /// </summary>
        public Func<bool> IsolatedRerunEnabled { get; set; } = () => true;

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
        private const int _RerunOutputLimitChars = 64 * 1024;

        private readonly string _Header = "[CheckRunService] ";
        private readonly DatabaseDriver _Database;
        private readonly WorkflowProfileService _WorkflowProfiles;
        private readonly VesselReadinessService _Readiness;
        private readonly LoggingModule _Logging;
        private readonly Func<IReadOnlyList<BannedDiffPatternRule>>? _BannedDiffPatterns;
        /// <summary>
        /// How long one check command may run by default. The time starts when the process starts, which is
        /// after the check holds its host command slot, so time spent waiting for the slot never counts.
        /// </summary>
        public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(30);

        private readonly TimeSpan _CommandTimeout = DefaultCommandTimeout;
        private readonly bool _HasCommandTimeoutOverride;
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _PendingRunLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _RunStateLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        internal static SemaphoreSlim GetRunStateLock(string id)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return _RunStateLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        }

        private static readonly ConcurrentDictionary<string, byte> _OperatorRuns =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        /// <summary>
        /// True while an operator's run_check or retry executes this record in this process. An operator
        /// re-runs a Check of an ended voyage to prove a commit before landing it by hand, so the voyage's
        /// end does not discard that record and the automatic sweep does not execute it a second time.
        /// The mark lives only as long as the execution: after a restart the record is an ordinary
        /// Pending Check again.
        /// </summary>
        /// <param name="id">Check run ID.</param>
        /// <returns>True when an operator's execution owns the record.</returns>
        public static bool IsOperatorRun(string id)
        {
            return !String.IsNullOrWhiteSpace(id) && _OperatorRuns.ContainsKey(id);
        }

        /// <summary>
        /// Stamp an armed source only while the durable check is still eligible and before source
        /// preparation starts. Callers may pass a row from an earlier enumeration; this method
        /// re-reads and rechecks the caller's eligibility predicate under the shared state lock.
        /// </summary>
        public static Task<CheckRun?> StampPendingSourceAsync(
            DatabaseDriver database,
            string id,
            string branchName,
            string commitHash,
            CancellationToken token = default)
        {
            return StampPendingSourceAsync(database, id, branchName, commitHash, _ => true, token);
        }

        /// <summary>
        /// Stamp an armed source after rechecking the caller's eligibility predicate against the fresh row.
        /// </summary>
        public static async Task<CheckRun?> StampPendingSourceAsync(
            DatabaseDriver database,
            string id,
            string branchName,
            string commitHash,
            Func<CheckRun, bool> eligibility,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            if (String.IsNullOrWhiteSpace(branchName)) throw new ArgumentNullException(nameof(branchName));
            if (String.IsNullOrWhiteSpace(commitHash)) throw new ArgumentNullException(nameof(commitHash));
            if (eligibility == null) throw new ArgumentNullException(nameof(eligibility));

            SemaphoreSlim stateLock = GetRunStateLock(id);
            await stateLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                CheckRun? current = await database.CheckRuns.ReadAsync(id, token: token).ConfigureAwait(false);
                if (current == null || current.Status != CheckRunStatusEnum.Pending
                    || current.SlotRequestedUtc.HasValue
                    || !String.IsNullOrWhiteSpace(current.BranchName)
                    || !eligibility(current)) return current;

                current.BranchName = branchName;
                current.CommitHash = commitHash;
                current.LastUpdateUtc = DateTime.UtcNow;
                return await database.CheckRuns.UpdateAsync(current, token).ConfigureAwait(false);
            }
            finally
            {
                stateLock.Release();
            }
        }

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
            Func<IReadOnlyList<BannedDiffPatternRule>>? bannedDiffPatterns = null,
            TimeSpan? commandTimeout = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            if (commandTimeout.HasValue && commandTimeout.Value > TimeSpan.Zero)
            {
                _CommandTimeout = commandTimeout.Value;
                _HasCommandTimeoutOverride = true;
            }
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
            foreach (CheckRun observed in interrupted)
            {
                SemaphoreSlim stateLock = GetRunStateLock(observed.Id);
                await stateLock.WaitAsync(token).ConfigureAwait(false);
                CheckRun? updated = null;
                try
                {
                    CheckRun? current = await _Database.CheckRuns.ReadAsync(observed.Id, token: token).ConfigureAwait(false);
                    if (current == null || current.Status != CheckRunStatusEnum.Running
                        || (current.StartedUtc.HasValue && current.StartedUtc.Value >= processStartUtc))
                        continue;

                    DateTime now = DateTime.UtcNow;
                    current.Status = CheckRunStatusEnum.Canceled;
                    current.Summary = "admiral restart: this Check was Running when the admiral process stopped, so its "
                        + "command never finished and the record carries no verdict.";
                    current.Output = String.IsNullOrWhiteSpace(current.Output) ? current.Summary : current.Output;
                    current.CompletedUtc = now;
                    current.SettleRunDuration();
                    current.LastUpdateUtc = now;
                    updated = await _Database.CheckRuns.UpdateAsync(current, token).ConfigureAwait(false);
                    cancelled++;
                }
                finally
                {
                    stateLock.Release();
                }

                if (updated != null) OnCheckRunChanged?.Invoke(updated);
            }

            if (cancelled > 0)
                _Logging.Warn(_Header + "cancelled " + cancelled + " check run" + (cancelled == 1 ? "" : "s") + " left Running by a stopped admiral");

            return cancelled;
        }

        /// <summary>
        /// How a check run names itself in the host-wide slot queue.
        /// </summary>
        /// <param name="run">The check run.</param>
        /// <returns>A short description naming the check, its type and its vessel.</returns>
        internal static string DescribeSlotOwner(CheckRun run)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            return "check " + run.Id + " (" + run.Type + (String.IsNullOrWhiteSpace(run.Label) ? String.Empty : ", " + run.Label)
                + ") for vessel " + run.VesselId;
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
            return RunCoreAsync(auth, request, false, token, operatorRun: true);
        }

        /// <summary>
        /// Start a check run and wait for it for at most <paramref name="wait"/>. A check that has not
        /// finished by then keeps running in the background and finishes its own record; the caller gets
        /// the record as it stands, so a long wait for the host-wide slot never surfaces as a timeout.
        /// </summary>
        /// <param name="auth">Caller authorization context.</param>
        /// <param name="request">Check run request.</param>
        /// <param name="wait">Longest time to wait for the result.</param>
        /// <param name="token">Cancellation token for the wait. The started check is not cancelled by it.</param>
        /// <returns>The finished check, or the unfinished record when the wait ends first.</returns>
        public Task<CheckRun> RunWithinAsync(AuthContext auth, CheckRunRequest request, TimeSpan wait, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return WaitWithinAsync(
                onRecorded => RunCoreAsync(auth, request, false, CancellationToken.None, onRecorded, operatorRun: true),
                wait,
                token);
        }

        private async Task<CheckRun> WaitWithinAsync(Func<Action<CheckRun>, Task<CheckRun>> start, TimeSpan wait, CancellationToken token)
        {
            TaskCompletionSource<CheckRun> recorded = new TaskCompletionSource<CheckRun>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<CheckRun> execution = start(created => recorded.TrySetResult(created));
            Task first = await Task.WhenAny(execution, Task.Delay(wait, token)).ConfigureAwait(false);
            if (ReferenceEquals(first, execution)) return await execution.ConfigureAwait(false);

            // Past the wait, return as soon as the record exists; only a run that ends before it records
            // anything (a refused request) is awaited to its end.
            first = await Task.WhenAny(execution, recorded.Task).ConfigureAwait(false);
            if (!recorded.Task.IsCompleted) return await execution.ConfigureAwait(false);

            string runId = recorded.Task.Result.Id;
            _ = execution.ContinueWith(
                finished => _Logging.Warn(_Header + "background check " + runId + " failed: " + finished.Exception?.GetBaseException().Message),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            CheckRun? current = await _Database.CheckRuns.ReadAsync(runId).ConfigureAwait(false);
            return current ?? recorded.Task.Result;
        }

        private async Task<CheckRun> RunCoreAsync(
            AuthContext auth,
            CheckRunRequest request,
            bool allowDeploymentExecution,
            CancellationToken token,
            Action<CheckRun>? onRecorded = null,
            bool operatorRun = false)
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
            bool harborSelected = await IsHarborCheckSelectedAsync(run, vessel, token).ConfigureAwait(false);
            if (!harborSelected && (String.IsNullOrWhiteSpace(vessel.WorkingDirectory) || !Directory.Exists(vessel.WorkingDirectory)))
                throw new InvalidOperationException("This vessel does not have a usable working directory.");

            SemaphoreSlim runLock = _PendingRunLocks.GetOrAdd(run.Id, _ => new SemaphoreSlim(1, 1));
            await runLock.WaitAsync(token).ConfigureAwait(false);
            // Mark before the record exists, so no sweep can see it Pending and unowned.
            if (operatorRun) _OperatorRuns[run.Id] = 0;
            try
            {
                run = await _Database.CheckRuns.CreateAsync(run, token).ConfigureAwait(false);
                OnCheckRunChanged?.Invoke(run);
                onRecorded?.Invoke(run);

                IsolatedCheckout? isolatedCheckout = null;
                HarborCheckExecution? harborExecution = null;
                string executionDirectory = run.WorkingDirectory!;
                string executionCommand = run.Command;
                CheckCommandBudget budget = CreateCommandBudget(profile, run.Type);

                CommandExecutionResult execution;
                CheckExecutionContextReceipt? executionContext = null;

                try
                {
                    // Freeze the source before creating a checkout. A later source stamp must
                    // not retag a checkout after it has been prepared.
                    CheckRun? slotRequested = await MarkSlotRequestedAsync(run, token).ConfigureAwait(false);
                    if (slotRequested == null) throw new InvalidOperationException("The check run no longer exists.");
                    if (slotRequested.Status != CheckRunStatusEnum.Pending) return slotRequested;
                    run = slotRequested;
                    List<ProvisionedSiblingPin> siblingPins = await ResolveRunSiblingPinsAsync(vessel, run, token).ConfigureAwait(false);
                    string? pinRefusal = DescribePinRefusal(siblingPins, harborSelected);
                    if (pinRefusal != null) return await CompleteExistingRunAsFailureAsync(run, pinRefusal, token).ConfigureAwait(false);

                    executionDirectory = run.WorkingDirectory!;
                    executionCommand = run.Command;
                    if (harborSelected && IsIsolatedCheckoutType(run.Type))
                        executionCommand = StripNoRestore(run.Command);

                    if (!harborSelected && IsIsolatedCheckoutType(run.Type))
                    {
                        string? repoSource = ResolveRepoSource(vessel);
                        if (repoSource != null)
                        {
                            isolatedCheckout = await TryCreateIsolatedCheckoutAsync(vessel, run.Id, run.CommitHash, run.BranchName, vessel.DefaultBranch, siblingPins, token).ConfigureAwait(false);
                            if (isolatedCheckout != null)
                            {
                                executionDirectory = isolatedCheckout.Path;
                                executionCommand = StripNoRestore(run.Command);
                            }
                            else
                            {
                                return await CompleteExistingRunAsFailureAsync(
                                    run,
                                    DescribeFailedCheckout(siblingPins),
                                    token).ConfigureAwait(false);
                            }
                        }
                    }

                    // Share the host-wide slots with DoD gates and merge-queue test runs so no more full
                    // build+test suites run on one host at once than its slot count allows. See HostWideCommandLock.
                    using (IDisposable slot = await HostWideCommandLock.AcquireAsync(run.Id, DescribeSlotOwner(run), run.VesselId, token).ConfigureAwait(false))
                    {
                        budget.SlotWait = HostWideCommandLock.ReadWaitRecord(slot);
                        if (isolatedCheckout != null)
                        {
                            string waitedPath = isolatedCheckout.Path;
                            isolatedCheckout = await RecreateRemovedCheckoutAsync(vessel, run, isolatedCheckout, token).ConfigureAwait(false);
                            if (isolatedCheckout == null)
                                return await CompleteExistingRunAsFailureAsync(run, DescribeRemovedCheckout(waitedPath), token).ConfigureAwait(false);
                            executionDirectory = isolatedCheckout.Path;
                        }

                        RunningClaim claim = await TryMarkRunningAsync(run, token).ConfigureAwait(false);
                        if (claim.Run == null) throw new InvalidOperationException("The check run no longer exists.");
                        if (!claim.Started)
                        {
                            await CleanupIsolatedCheckoutAsync(isolatedCheckout, CancellationToken.None).ConfigureAwait(false);
                            return claim.Run;
                        }
                        run = claim.Run;
                        OnCheckRunChanged?.Invoke(run);
                        Dictionary<string, string> commandEnvironment = await BuildCommandEnvironmentAsync(profile, run, token).ConfigureAwait(false);
                        if (!harborSelected) executionContext = CaptureExecutionContext(run, vessel, profile, executionCommand, commandEnvironment);
                        harborExecution = await TryRunHarborCheckAsync(run, vessel, profile, executionCommand,
                            !String.IsNullOrWhiteSpace(request.CommandOverride), budget, token).ConfigureAwait(false);
                        if (harborSelected && harborExecution == null)
                            throw new InvalidOperationException("harbor_owned_check_route_lost");
                        if (harborExecution != null)
                        {
                            run.StartedUtc = harborExecution.Result.StartedUtc ?? run.StartedUtc;
                            execution = new CommandExecutionResult
                            {
                                ExitCode = harborExecution.Result.TimedOut || harborExecution.Result.Cancelled
                                    ? -1 : harborExecution.Result.ExitCode ?? -1,
                                Output = harborExecution.Output,
                                Truncated = harborExecution.Result.Truncated
                            };
                            if (budget.FailureReason != null) execution.ExitCode = -1;
                            executionDirectory = harborExecution.WorkingDirectory;
                            run.WorkingDirectory = executionDirectory;
                        }
                        else
                        {
                            execution = await budget.ExecuteAsync(timeout => ExecuteCommandAsync(
                                executionCommand, executionDirectory, timeout, token, commandEnvironment)).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    await CleanupIsolatedCheckoutAsync(isolatedCheckout, CancellationToken.None).ConfigureAwait(false);
                    run = await RestorePendingAfterCancellationAsync(run.Id, clearSlotRequest: true).ConfigureAwait(false) ?? run;
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

                HarborArtifactStage? harborArtifacts = null;
                if (harborSelected && harborExecution != null)
                {
                    try { harborArtifacts = await StageHarborArtifactsAsync(harborExecution, profile?.ExpectedArtifacts, token).ConfigureAwait(false); }
                    catch (Exception artifactException) when (!(artifactException is OperationCanceledException && token.IsCancellationRequested))
                    {
                        execution.ExitCode = -1;
                        execution.Output += Environment.NewLine + "Harbor artifact retrieval failed: " + artifactException.Message;
                    }
                }
                run.ExitCode = execution.ExitCode;
                run.Output = execution.Output;
                run.Status = execution.ExitCode == 0 ? CheckRunStatusEnum.Passed : CheckRunStatusEnum.Failed;

                string artifactDirectory = !harborSelected
                    ? isolatedCheckout?.Path ?? run.WorkingDirectory ?? String.Empty
                    : harborArtifacts?.Directory ?? String.Empty;
                run.Artifacts = !harborSelected
                    ? CollectArtifacts(artifactDirectory, profile?.ExpectedArtifacts)
                    : harborArtifacts?.Artifacts ?? new List<CheckRunArtifact>();
                ParseExecutionSummaries(run, artifactDirectory);
                run.Summary = BuildSummary(run, profile);
                if (!harborSelected)
                    await ApplyIsolatedRerunAsync(run, profile, execution, executionCommand, executionDirectory, request.DiffBaseCommit, budget, token).ConfigureAwait(false);
                else if (harborExecution != null)
                    await ApplyHarborIsolatedRerunAsync(run, profile, execution, executionCommand, harborExecution, budget, token).ConfigureAwait(false);

                await ReleaseHarborCheckDockAsync(run, harborExecution, token).ConfigureAwait(false);

                await CleanupIsolatedCheckoutAsync(isolatedCheckout, token).ConfigureAwait(false);
                FinishExecutedRun(run, budget);

                run = await CompleteExecutionAsync(run, token).ConfigureAwait(false);
                if (execution.ExitCode == 0 && harborExecution == null)
                    await RecordExecutionContextAsync(run, executionContext, token).ConfigureAwait(false);
                OnCheckRunChanged?.Invoke(run);
                harborArtifacts?.Dispose();
                return run;
            }
            finally
            {
                if (operatorRun) _OperatorRuns.TryRemove(run.Id, out _);
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

            if (CheckRunGateRules.IsNativeSlopRun(run))
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
                SemaphoreSlim stateLock = GetRunStateLock(pending.Id);
                await stateLock.WaitAsync(token).ConfigureAwait(false);
                CheckRun? updated = null;
                try
                {
                    CheckRun? current = await _Database.CheckRuns.ReadAsync(pending.Id, token: token).ConfigureAwait(false);
                    if (current != null && current.Status == CheckRunStatusEnum.Pending)
                    {
                        CopyCompletedRecord(run, current);
                        current.LastUpdateUtc = DateTime.UtcNow;
                        updated = await _Database.CheckRuns.UpdateAsync(current, token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    stateLock.Release();
                }

                if (updated != null)
                {
                    OnCheckRunChanged?.Invoke(updated);
                    return updated;
                }
            }

            // The matching record changed after lookup (for example, discard won). Preserve that
            // terminal record and save this independently completed result as its own record.
            CheckRun created = await _Database.CheckRuns.CreateAsync(run, token).ConfigureAwait(false);
            OnCheckRunChanged?.Invoke(created);
            return created;
        }

        /// <summary>
        /// Apply the explicit operator/MCP status resolution to the current row without replacing
        /// unrelated fields from a stale pre-resolution snapshot.
        /// </summary>
        public async Task<CheckRun?> ResolveStatusManuallyAsync(
            string id,
            CheckRunStatusEnum status,
            string? output,
            string? summary,
            int? exitCode,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            SemaphoreSlim stateLock = GetRunStateLock(id);
            await stateLock.WaitAsync(token).ConfigureAwait(false);
            CheckRun? current;
            try
            {
                current = await _Database.CheckRuns.ReadAsync(id, token: token).ConfigureAwait(false);
                if (current == null) return null;

                current.Status = status;
                if (output != null) current.Output = output;
                if (summary != null) current.Summary = summary;
                if (exitCode.HasValue) current.ExitCode = exitCode.Value;
                if (status == CheckRunStatusEnum.Running && current.StartedUtc == null) current.StartedUtc = DateTime.UtcNow;
                if (status == CheckRunStatusEnum.Passed || status == CheckRunStatusEnum.Failed || status == CheckRunStatusEnum.Canceled)
                    current.CompletedUtc ??= DateTime.UtcNow;
                current.LastUpdateUtc = DateTime.UtcNow;
                current = await _Database.CheckRuns.UpdateAsync(current, token).ConfigureAwait(false);
            }
            finally
            {
                stateLock.Release();
            }

            OnCheckRunChanged?.Invoke(current);
            return current;
        }

        /// <summary>
        /// Retry a check run: a finished record as a new record, a Pending record in place as an operator run.
        /// </summary>
        /// <remarks>
        /// A retry is an operator run: it executes to its end even when the Check's voyage has ended,
        /// because an operator retries a failed voyage's Check to prove its commit before landing it by hand.
        /// </remarks>
        public Task<CheckRun> RetryAsync(AuthContext auth, string id, CancellationToken token = default)
        {
            return RetryCoreAsync(auth, id, token, null);
        }

        /// <summary>
        /// Retry a check run and wait for it for at most <paramref name="wait"/>, with the same contract as
        /// <see cref="RunWithinAsync"/>: a retry still waiting for the host-wide slot is returned unfinished and
        /// finishes its own record in the background.
        /// </summary>
        /// <param name="auth">Caller authorization context.</param>
        /// <param name="id">Check run ID to retry.</param>
        /// <param name="wait">Longest time to wait for the result.</param>
        /// <param name="token">Cancellation token for the wait. The started retry is not cancelled by it.</param>
        /// <returns>The finished retry, or the unfinished record when the wait ends first.</returns>
        public Task<CheckRun> RetryWithinAsync(AuthContext auth, string id, TimeSpan wait, CancellationToken token = default)
        {
            return WaitWithinAsync(
                onRecorded => RetryCoreAsync(auth, id, CancellationToken.None, onRecorded),
                wait,
                token);
        }

        private async Task<CheckRun> RetryCoreAsync(AuthContext auth, string id, CancellationToken token, Action<CheckRun>? onRecorded)
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
            {
                _OperatorRuns[prior.Id] = 0;
                try
                {
                    onRecorded?.Invoke(prior);
                    return await RunPendingAsync(auth, prior.Id, allowDeploymentExecution: false, token).ConfigureAwait(false);
                }
                finally
                {
                    _OperatorRuns.TryRemove(prior.Id, out _);
                }
            }

            return await RunCoreAsync(auth, new CheckRunRequest
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
            }, false, token, onRecorded, operatorRun: true).ConfigureAwait(false);
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
                    || (String.IsNullOrWhiteSpace(run.CommitHash) && !IsDeploymentExecutionType(request.Type))
                    || String.Equals(run.CommitHash, request.CommitHash, StringComparison.OrdinalIgnoreCase))
                .OrderBy(run => run.CreatedUtc)
                .FirstOrDefault();
        }

        private static bool ShouldResolvePendingCommand(CheckRun run)
        {
            return CheckRunGateRules.HasUnresolvedCommand(run);
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
                Mission? work = StaleCheckSupersessionService.SelectWorkUnderReviewFor(run, missions);
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

            RunningClaim claim = await TryMarkRunningAsync(run, token).ConfigureAwait(false);
            if (claim.Run == null) throw new InvalidOperationException("The check run no longer exists.");
            if (!claim.Started) return claim.Run;
            run = claim.Run;
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
                run = await RestorePendingAfterCancellationAsync(run.Id).ConfigureAwait(false) ?? run;
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

            run = await CompleteExecutionAsync(run, token).ConfigureAwait(false);
            OnCheckRunChanged?.Invoke(run);
            return run;
        }

        /// <summary>
        /// The environment a check command runs with: the workflow profile's variables, plus the deployment candidate
        /// for a record that belongs to a deployment. The candidate variables are set by Armada and win over a profile
        /// variable of the same name, so a deploy script reads the commit the deployment pinned rather than a branch tip.
        /// A record whose objective provisions a sibling pin also receives each resolved pin through a
        /// <see cref="SiblingPinOverride.EnvironmentVariablePrefix"/> variable, the same variable the gate's commands
        /// receive, so a profile that verifies its sibling checks the pinned commit.
        /// </summary>
        private async Task<Dictionary<string, string>> BuildCommandEnvironmentAsync(
            WorkflowProfile? profile,
            CheckRun run,
            CancellationToken token)
        {
            Dictionary<string, string> environment = new Dictionary<string, string>(StringComparer.Ordinal);
            if (profile?.EnvironmentVariables != null)
            {
                foreach (KeyValuePair<string, string> pair in profile.EnvironmentVariables)
                {
                    if (!String.IsNullOrWhiteSpace(pair.Key)) environment[pair.Key] = pair.Value ?? String.Empty;
                }
            }

            Vessel? vessel = String.IsNullOrWhiteSpace(run.VesselId) ? null : await _Database.Vessels.ReadAsync(run.VesselId!, token).ConfigureAwait(false);
            foreach (KeyValuePair<string, string> pin in SiblingPinOverride.ToEnvironment(await ResolveRunSiblingPinsAsync(vessel, run, token).ConfigureAwait(false)))
                environment[pin.Key] = pin.Value;

            if (String.IsNullOrWhiteSpace(run.DeploymentId)) return environment;

            Deployment? deployment = await _Database.Deployments.ReadAsync(run.DeploymentId, null, token).ConfigureAwait(false);
            if (deployment == null) return environment;

            environment["ARMADA_DEPLOYMENT_ID"] = deployment.Id;
            environment["ARMADA_DEPLOYMENT_SOURCE_REF"] = deployment.SourceRef ?? String.Empty;
            environment["ARMADA_DEPLOYMENT_SOURCE_COMMIT"] = deployment.SourceCommit ?? String.Empty;
            environment["ARMADA_DEPLOYMENT_ARTIFACT"] = deployment.ArtifactIdentity ?? String.Empty;
            return environment;
        }

        private static bool IsDeploymentExecutionType(CheckRunTypeEnum type)
        {
            return type == CheckRunTypeEnum.Deploy || type == CheckRunTypeEnum.Rollback;
        }

        private static void CopyCompletedRecord(CheckRun source, CheckRun target)
        {
            // Keep the matched Pending record's id and creation time, but preserve the complete
            // finished record's source and execution provenance.
            target.TenantId = source.TenantId;
            target.UserId = source.UserId;
            target.WorkflowProfileId = source.WorkflowProfileId;
            target.VesselId = source.VesselId;
            target.MissionId = source.MissionId;
            target.VoyageId = source.VoyageId;
            target.DeploymentId = source.DeploymentId;
            target.Label = source.Label;
            target.Type = source.Type;
            target.Source = source.Source;
            target.ProviderName = source.ProviderName;
            target.ExternalId = source.ExternalId;
            target.ExternalUrl = source.ExternalUrl;
            target.EnvironmentName = source.EnvironmentName;
            target.Command = source.Command;
            target.WorkingDirectory = source.WorkingDirectory;
            target.BranchName = source.BranchName;
            target.CommitHash = source.CommitHash;
            target.RegressionPurpose = source.RegressionPurpose;
            target.RegressionObjectiveId = source.RegressionObjectiveId;
            target.RegressionLandedCommit = source.RegressionLandedCommit;
            target.Status = source.Status;
            target.ExitCode = source.ExitCode;
            target.Output = source.Output;
            target.Summary = source.Summary;
            target.TestSummary = source.TestSummary;
            target.CoverageSummary = source.CoverageSummary;
            target.Artifacts = source.Artifacts;
            target.SatisfiedBy = source.SatisfiedBy;
            target.DurationMs = source.DurationMs;
            target.SlotRequestedUtc = source.SlotRequestedUtc;
            target.StartedUtc = source.StartedUtc;
            target.CompletedUtc = source.CompletedUtc;
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
            bool harborSelected = await IsHarborCheckSelectedAsync(run, vessel, token).ConfigureAwait(false);
            if (!harborSelected && (String.IsNullOrWhiteSpace(vessel.WorkingDirectory) || !Directory.Exists(vessel.WorkingDirectory)))
                return await CompleteExistingRunAsFailureAsync(run, "This vessel does not have a usable working directory.", token).ConfigureAwait(false);

            if (String.IsNullOrWhiteSpace(run.Command))
                return await CompleteExistingRunAsFailureAsync(run, "No command is configured for " + run.Type + ".", token).ConfigureAwait(false);

            run.WorkingDirectory = vessel.WorkingDirectory;

            IsolatedCheckout? isolatedCheckout = null;
            HarborCheckExecution? harborExecution = null;
            string executionDirectory = run.WorkingDirectory!;
            string executionCommand = run.Command;
            CheckCommandBudget budget = CreateCommandBudget(profile, run.Type);

            CommandExecutionResult execution;
            CheckExecutionContextReceipt? executionContext = null;

            try
            {
                // Freeze the pending row's source before preparing its isolated checkout.
                CheckRun? slotRequested = await MarkSlotRequestedAsync(run, token).ConfigureAwait(false);
                if (slotRequested == null) throw new InvalidOperationException("The check run no longer exists.");
                if (slotRequested.Status != CheckRunStatusEnum.Pending) return slotRequested;
                run = slotRequested;
                List<ProvisionedSiblingPin> siblingPins = await ResolveRunSiblingPinsAsync(vessel, run, token).ConfigureAwait(false);
                string? pinRefusal = DescribePinRefusal(siblingPins, harborSelected);
                if (pinRefusal != null) return await CompleteExistingRunAsFailureAsync(run, pinRefusal, token).ConfigureAwait(false);
                executionDirectory = run.WorkingDirectory!;
                executionCommand = run.Command;
                if (harborSelected && IsIsolatedCheckoutType(run.Type))
                    executionCommand = StripNoRestore(run.Command);

                CheckRun? satisfied = await TryCompleteFromGatePassAsync(run, profile, token).ConfigureAwait(false);
                if (satisfied != null) return satisfied;

                if (!harborSelected && IsIsolatedCheckoutType(run.Type))
                {
                    string? repoSource = ResolveRepoSource(vessel);
                    if (repoSource != null)
                    {
                        isolatedCheckout = await TryCreateIsolatedCheckoutAsync(vessel, run.Id, run.CommitHash, run.BranchName, vessel.DefaultBranch, siblingPins, token).ConfigureAwait(false);
                        if (isolatedCheckout != null)
                        {
                            executionDirectory = isolatedCheckout.Path;
                            executionCommand = StripNoRestore(run.Command);
                        }
                        else
                        {
                            return await CompleteExistingRunAsFailureAsync(
                                run,
                                DescribeFailedCheckout(siblingPins),
                                token).ConfigureAwait(false);
                        }
                    }
                }

                using (IDisposable slot = await HostWideCommandLock.AcquireAsync(run.Id, DescribeSlotOwner(run), run.VesselId, token).ConfigureAwait(false))
                {
                    budget.SlotWait = HostWideCommandLock.ReadWaitRecord(slot);

                    // A gate ahead of this check can publish its pass while the check waits. Read the
                    // evidence again before starting the command so that wait does not cause a duplicate run.
                    satisfied = await TryCompleteFromGatePassAsync(run, profile, token).ConfigureAwait(false);
                    if (satisfied != null)
                    {
                        await CleanupIsolatedCheckoutAsync(isolatedCheckout, CancellationToken.None).ConfigureAwait(false);
                        return satisfied;
                    }

                    if (isolatedCheckout != null)
                    {
                        string waitedPath = isolatedCheckout.Path;
                        isolatedCheckout = await RecreateRemovedCheckoutAsync(vessel, run, isolatedCheckout, token).ConfigureAwait(false);
                        if (isolatedCheckout == null)
                            return await CompleteExistingRunAsFailureAsync(run, DescribeRemovedCheckout(waitedPath), token).ConfigureAwait(false);
                        executionDirectory = isolatedCheckout.Path;
                    }

                    RunningClaim claim = await TryMarkRunningAsync(run, token).ConfigureAwait(false);
                    if (claim.Run == null) throw new InvalidOperationException("The check run no longer exists.");
                    if (!claim.Started)
                    {
                        await CleanupIsolatedCheckoutAsync(isolatedCheckout, CancellationToken.None).ConfigureAwait(false);
                        return claim.Run;
                    }
                    run = claim.Run;
                    OnCheckRunChanged?.Invoke(run);
                    Dictionary<string, string> commandEnvironment = await BuildCommandEnvironmentAsync(profile, run, token).ConfigureAwait(false);
                    if (!harborSelected) executionContext = CaptureExecutionContext(run, vessel, profile, executionCommand, commandEnvironment);
                    harborExecution = await TryRunHarborCheckAsync(run, vessel, profile, executionCommand, false, budget, token).ConfigureAwait(false);
                    if (harborSelected && harborExecution == null)
                        throw new InvalidOperationException("harbor_owned_check_route_lost");
                    if (harborExecution != null)
                    {
                        run.StartedUtc = harborExecution.Result.StartedUtc ?? run.StartedUtc;
                        execution = new CommandExecutionResult
                        {
                            ExitCode = harborExecution.Result.TimedOut || harborExecution.Result.Cancelled
                                ? -1 : harborExecution.Result.ExitCode ?? -1,
                            Output = harborExecution.Output,
                            Truncated = harborExecution.Result.Truncated
                        };
                        if (budget.FailureReason != null) execution.ExitCode = -1;
                        executionDirectory = harborExecution.WorkingDirectory;
                        run.WorkingDirectory = executionDirectory;
                    }
                    else
                    {
                        execution = await budget.ExecuteAsync(timeout => ExecuteCommandAsync(
                            executionCommand, executionDirectory, timeout, token, commandEnvironment)).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await CleanupIsolatedCheckoutAsync(isolatedCheckout, CancellationToken.None).ConfigureAwait(false);
                run = await RestorePendingAfterCancellationAsync(run.Id, clearSlotRequest: true).ConfigureAwait(false) ?? run;
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
            run.Status = execution.ExitCode == 0 ? CheckRunStatusEnum.Passed : CheckRunStatusEnum.Failed;

            string artifactDirectory = harborSelected
                ? String.Empty : isolatedCheckout?.Path ?? run.WorkingDirectory ?? String.Empty;
            run.Artifacts = !harborSelected
                ? CollectArtifacts(artifactDirectory, profile?.ExpectedArtifacts)
                : new List<CheckRunArtifact>();
            ParseExecutionSummaries(run, artifactDirectory);
            run.Summary = BuildSummary(run, profile);
            if (!harborSelected)
                await ApplyIsolatedRerunAsync(run, profile, execution, executionCommand, executionDirectory, null, budget, token).ConfigureAwait(false);
            else if (harborExecution != null)
                await ApplyHarborIsolatedRerunAsync(run, profile, execution, executionCommand, harborExecution, budget, token).ConfigureAwait(false);

            await ReleaseHarborCheckDockAsync(run, harborExecution, token).ConfigureAwait(false);

            await CleanupIsolatedCheckoutAsync(isolatedCheckout, token).ConfigureAwait(false);
            FinishExecutedRun(run, budget);

            run = await CompleteExecutionAsync(run, token).ConfigureAwait(false);
            if (execution.ExitCode == 0 && harborExecution == null)
                await RecordExecutionContextAsync(run, executionContext, token).ConfigureAwait(false);
            OnCheckRunChanged?.Invoke(run);
            return run;
        }

        private async Task<CheckRun?> MarkSlotRequestedAsync(CheckRun run, CancellationToken token)
        {
            SemaphoreSlim stateLock = GetRunStateLock(run.Id);
            await stateLock.WaitAsync(token).ConfigureAwait(false);
            CheckRun? current;
            try
            {
                current = await _Database.CheckRuns.ReadAsync(run.Id, token: token).ConfigureAwait(false);
                if (current == null || current.Status != CheckRunStatusEnum.Pending) return current;
                current.Source = run.Source;
                current.Command = run.Command;
                current.WorkingDirectory = run.WorkingDirectory;
                current.SlotRequestedUtc = DateTime.UtcNow;
                current.LastUpdateUtc = current.SlotRequestedUtc.Value;
                current = await _Database.CheckRuns.UpdateAsync(current, token).ConfigureAwait(false);
            }
            finally
            {
                stateLock.Release();
            }

            return current;
        }

        private sealed class RunningClaim
        {
            public CheckRun? Run { get; }
            public bool Started { get; }

            public RunningClaim(CheckRun? run, bool started)
            {
                Run = run;
                Started = started;
            }
        }

        private async Task<RunningClaim> TryMarkRunningAsync(CheckRun run, CancellationToken token)
        {
            SemaphoreSlim stateLock = GetRunStateLock(run.Id);
            await stateLock.WaitAsync(token).ConfigureAwait(false);
            CheckRun? current;
            try
            {
                current = await _Database.CheckRuns.ReadAsync(run.Id, token: token).ConfigureAwait(false);
                if (current == null || current.Status != CheckRunStatusEnum.Pending) return new RunningClaim(current, false);
                current.Source = run.Source;
                current.Command = run.Command;
                current.WorkingDirectory = run.WorkingDirectory;
                current.BranchName = run.BranchName;
                current.CommitHash = run.CommitHash;
                current.Status = CheckRunStatusEnum.Running;
                current.StartedUtc = DateTime.UtcNow;
                current.LastUpdateUtc = current.StartedUtc.Value;
                current = await _Database.CheckRuns.UpdateAsync(current, token).ConfigureAwait(false);
            }
            finally
            {
                stateLock.Release();
            }

            return new RunningClaim(current, true);
        }

        private async Task<CheckRun?> RestorePendingAfterCancellationAsync(string id, bool clearSlotRequest = false)
        {
            SemaphoreSlim stateLock = GetRunStateLock(id);
            await stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            CheckRun? current;
            try
            {
                current = await _Database.CheckRuns.ReadAsync(id, token: CancellationToken.None).ConfigureAwait(false);
                if (current == null || (current.Status != CheckRunStatusEnum.Pending && current.Status != CheckRunStatusEnum.Running)) return current;
                current.Status = CheckRunStatusEnum.Pending;
                current.StartedUtc = null;
                if (clearSlotRequest) current.SlotRequestedUtc = null;
                current.LastUpdateUtc = DateTime.UtcNow;
                current = await _Database.CheckRuns.UpdateAsync(current, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                stateLock.Release();
            }

            return current;
        }

        private async Task<CheckRun> CompleteExecutionAsync(CheckRun run, CancellationToken token)
        {
            SemaphoreSlim stateLock = GetRunStateLock(run.Id);
            await stateLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                CheckRun? current = await _Database.CheckRuns.ReadAsync(run.Id, token: token).ConfigureAwait(false);
                if (current == null) throw new InvalidOperationException("The check run no longer exists.");
                // Preserve the existing failure result when a host-slot request fails before
                // Running is claimed. A canceled row is terminal and must not be overwritten.
                if (current.Status != CheckRunStatusEnum.Pending && current.Status != CheckRunStatusEnum.Running) return current;
                // The execution snapshot owns source identity. In particular, Slop resolves its
                // measured HEAD after Running; do not lose that commit by writing only to the stale
                // local object or by taking a later unrelated row value.
                current.Source = run.Source;
                current.Command = run.Command;
                current.WorkingDirectory = run.WorkingDirectory;
                current.BranchName = run.BranchName;
                current.CommitHash = run.CommitHash;
                current.EnvironmentName = run.EnvironmentName;
                current.RegressionPurpose = run.RegressionPurpose;
                current.RegressionObjectiveId = run.RegressionObjectiveId;
                current.RegressionLandedCommit = run.RegressionLandedCommit;
                current.Status = run.Status;
                current.ExitCode = run.ExitCode;
                current.Output = run.Output;
                current.Summary = run.Summary;
                current.TestSummary = run.TestSummary;
                current.CoverageSummary = run.CoverageSummary;
                current.Artifacts = run.Artifacts;
                current.DurationMs = run.DurationMs;
                current.StartedUtc = run.StartedUtc;
                current.CompletedUtc = run.CompletedUtc;
                current.LastUpdateUtc = run.LastUpdateUtc;
                return await _Database.CheckRuns.UpdateAsync(current, token).ConfigureAwait(false);
            }
            finally
            {
                stateLock.Release();
            }
        }

        /// <summary>
        /// Record an automatic Build or UnitTest check of a voyage or mission as satisfied when a passing
        /// definition-of-done gate already ran the same command in full on the same commit, with the same
        /// environment. The check does not run the suite again. It stores the gate it took its result from in
        /// <see cref="CheckRun.SatisfiedBy"/> (mission, command label, commit, pass time) and has no run duration,
        /// so no reader can take it for a run of its own. An operator's run and a check of a deployment always run
        /// their command. See <see cref="DefinitionOfDoneCommandPasses"/>.
        /// </summary>
        /// <returns>The completed record, or null when no gate pass applies and the command must run.</returns>
        private async Task<CheckRun?> TryCompleteFromGatePassAsync(CheckRun run, WorkflowProfile? profile, CancellationToken token)
        {
            if (run.Source != CheckRunSourceEnum.Armada || IsOperatorRun(run.Id)) return null;
            if (!IsIsolatedCheckoutType(run.Type)) return null;
            if (!String.IsNullOrWhiteSpace(run.DeploymentId)) return null;
            if (String.IsNullOrWhiteSpace(run.VoyageId) && String.IsNullOrWhiteSpace(run.MissionId)) return null;
            if (String.IsNullOrWhiteSpace(run.CommitHash)) return null;

            Dictionary<string, string> environment = await BuildCommandEnvironmentAsync(profile, run, token).ConfigureAwait(false);
            CheckCommandBudget budget = CreateCommandBudget(profile, run.Type);
            long? executionBudgetTicks = budget.Shared ? budget.Limit.Ticks : null;
            DefinitionOfDoneCommandPass? pass = DefinitionOfDoneCommandPasses.Find(
                run.VesselId, run.CommitHash, run.Command, environment, executionBudgetTicks);
            if (pass == null) return null;

            string text = "Satisfied by the definition-of-done gate of mission " + pass.MissionId + ": it passed the same "
                + pass.Label + " command in full on commit " + pass.CommitHash + " at " + pass.CompletedUtc.ToString("u")
                + ", so this check did not run the command again.";

            SemaphoreSlim stateLock = GetRunStateLock(run.Id);
            await stateLock.WaitAsync(token).ConfigureAwait(false);
            CheckRun? current;
            try
            {
                current = await _Database.CheckRuns.ReadAsync(run.Id, token: token).ConfigureAwait(false);
                if (current == null) throw new InvalidOperationException("The check run no longer exists.");
                if (current.Status != CheckRunStatusEnum.Pending) return current;

                DateTime now = DateTime.UtcNow;
                current.Status = CheckRunStatusEnum.Passed;
                current.ExitCode = 0;
                current.Command = run.Command;
                current.Output = text;
                current.Summary = text;
                current.SatisfiedBy = new CheckRunSatisfiedBy
                {
                    Kind = CheckRunSatisfiedBy.DefinitionOfDoneGate,
                    MissionId = pass.MissionId,
                    Label = pass.Label,
                    CommitHash = pass.CommitHash,
                    PassedUtc = pass.CompletedUtc
                };
                current.StartedUtc = now;
                current.CompletedUtc = now;
                current.DurationMs = null;
                current.LastUpdateUtc = now;
                current = await _Database.CheckRuns.UpdateAsync(current, token).ConfigureAwait(false);
            }
            finally
            {
                stateLock.Release();
            }

            _Logging.Info(_Header + "check " + run.Id + " satisfied by the definition-of-done gate of mission " + pass.MissionId
                + " (" + pass.Label + " at " + pass.CommitHash + ")");
            OnCheckRunChanged?.Invoke(current);
            return current;
        }

        private async Task<CheckRun> CompleteExistingRunAsFailureAsync(CheckRun run, string output, CancellationToken token)
        {
            SemaphoreSlim stateLock = GetRunStateLock(run.Id);
            await stateLock.WaitAsync(token).ConfigureAwait(false);
            CheckRun? current;
            try
            {
                current = await _Database.CheckRuns.ReadAsync(run.Id, token: token).ConfigureAwait(false);
                if (current == null) throw new InvalidOperationException("The check run no longer exists.");
                if (current.Status != CheckRunStatusEnum.Pending) return current;

                DateTime now = DateTime.UtcNow;
                current.Status = CheckRunStatusEnum.Failed;
                current.ExitCode = -1;
                current.Output = output;
                current.StartedUtc ??= now;
                current.CompletedUtc = now;
                current.DurationMs ??= 0;
                current.LastUpdateUtc = now;
                current.Summary = BuildSummary(current, null);
                current = await _Database.CheckRuns.UpdateAsync(current, token).ConfigureAwait(false);
            }
            finally
            {
                stateLock.Release();
            }

            OnCheckRunChanged?.Invoke(current);
            return current;
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

        /// <summary>
        /// An explicit profile budget bounds Build and UnitTest execution. Other check types and
        /// profiles without that declaration keep the default per-command timeout. A caller's
        /// positive constructor override remains authoritative, including subsecond test bounds.
        /// </summary>
        internal TimeSpan ResolveCheckCommandTimeout(WorkflowProfile? profile, CheckRunTypeEnum type)
        {
            if (_HasCommandTimeoutOverride) return _CommandTimeout;
            if (IsProfileBudgetCheck(type) && profile?.GateCommandTimeoutSeconds is int seconds)
            {
                return TimeSpan.FromSeconds(Math.Clamp(seconds,
                    WorkflowProfile.MinimumGateCommandTimeoutSeconds, WorkflowProfile.MaximumGateCommandTimeoutSeconds));
            }
            return DefaultCommandTimeout;
        }

        private static bool IsProfileBudgetCheck(CheckRunTypeEnum type)
        {
            return type == CheckRunTypeEnum.Build || type == CheckRunTypeEnum.UnitTest;
        }

        private CheckCommandBudget CreateCommandBudget(WorkflowProfile? profile, CheckRunTypeEnum type)
        {
            bool shared = IsProfileBudgetCheck(type)
                && (_HasCommandTimeoutOverride || profile?.GateCommandTimeoutSeconds != null);
            return new CheckCommandBudget(ResolveCheckCommandTimeout(profile, type), shared);
        }

        private static void FinishExecutedRun(CheckRun run, CheckCommandBudget budget)
        {
            // Completion belongs after every command and checkout cleanup. Duration keeps its
            // documented StartedUtc-to-CompletedUtc meaning, including an isolated retry's wait.
            // The appended monotonic measurements separate that wait from active command time.
            run.CompletedUtc = DateTime.UtcNow;
            run.SettleRunDuration();
            run.DurationMs ??= 0;
            run.LastUpdateUtc = run.CompletedUtc.Value;
            run.Output = (run.Output ?? String.Empty) + Environment.NewLine + Environment.NewLine
                + "Check execution timing: command_execution_ms=" + budget.ExecutionMillisecondsText
                + "; retry_slot_wait_ms=" + budget.RetrySlotWaitMilliseconds
                + "; " + (budget.Shared ? "shared_execution_budget_ms=" : "per_command_timeout_ms=")
                + Convert.ToInt64(Math.Ceiling(budget.Limit.TotalMilliseconds));

            // Who the run waited behind for its host slot. The slot snapshot shows this only while the run waits,
            // so the finished record keeps it.
            if (budget.SlotWait != null)
                run.Output += Environment.NewLine + "Check slot wait: " + budget.SlotWait.Describe();
        }

        private sealed class CheckCommandBudget
        {
            public CheckCommandBudget(TimeSpan limit, bool shared)
            {
                Limit = limit;
                Shared = shared;
            }

            public TimeSpan Limit { get; }
            public bool Shared { get; }
            public TimeSpan RetrySlotWait { get; set; }
            public HostSlotWaitRecord? SlotWait { get; set; }
            public bool Exhausted => Shared && _Execution >= Limit;
            public string? FailureReason { get; private set; }
            public long ExecutionMilliseconds => Convert.ToInt64(Math.Ceiling(_Execution.TotalMilliseconds));
            public string ExecutionMillisecondsText => _ExecutionKnown
                ? ExecutionMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
            public long RetrySlotWaitMilliseconds => Convert.ToInt64(Math.Ceiling(RetrySlotWait.TotalMilliseconds));
            private TimeSpan _Execution;
            private bool _ExecutionKnown = true;

            public int GetRemoteTimeoutSeconds()
            {
                if (FailureReason != null) throw new InvalidOperationException(FailureReason);
                double seconds = (Shared ? Limit - _Execution : Limit).TotalSeconds;
                // The authenticated runner protocol accepts whole seconds only. Rounding a
                // shared remainder up could let a new command run past the declared budget.
                seconds = Shared ? Math.Floor(seconds) : Math.Ceiling(seconds);
                if (seconds < 1)
                    throw new InvalidOperationException("Check command timed out: less than one whole second remains in the shared execution budget.");
                return (int)Math.Min(1800, seconds);
            }

            public void RecordRemoteExecution(HarborWorkspaceOperationResult result)
            {
                if (result.DurationMilliseconds is long milliseconds && milliseconds >= 0)
                {
                    try
                    {
                        _Execution += TimeSpan.FromMilliseconds(milliseconds);
                        if (Exhausted) FailureReason = "Check command timed out: shared execution budget exhausted.";
                        return;
                    }
                    catch (OverflowException)
                    {
                        // Invalid remote facts cannot become a zero-duration passing command.
                    }
                }

                _ExecutionKnown = false;
                if (Shared && (result.StartedUtc.HasValue || result.ExitCode.HasValue))
                    FailureReason = "Harbor verification has no valid duration for the shared execution budget.";
            }

            public async Task<CommandExecutionResult> ExecuteAsync(Func<TimeSpan, Task<CommandExecutionResult>> execute)
            {
                TimeSpan remaining = Shared ? Limit - _Execution : Limit;
                if (remaining <= TimeSpan.Zero)
                    return new CommandExecutionResult { Output = "Check command timed out: shared execution budget exhausted before the isolated re-run." };

                CommandExecutionResult result;
                long start = Stopwatch.GetTimestamp();
                try
                {
                    result = await execute(remaining).ConfigureAwait(false);
                }
                finally
                {
                    _Execution += Stopwatch.GetElapsedTime(start);
                }

                // Process exit and cancellation can race at the deadline. A late zero exit is
                // not proof that the complete execution fitted inside the declared budget.
                if (Shared && _Execution >= Limit && result.ExitCode == 0)
                {
                    result.ExitCode = -1;
                    result.Output += Environment.NewLine + "Check command timed out: shared execution budget exhausted.";
                }
                return result;
            }
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
            BuildServerEnvironment.Apply(startInfo);

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
            ArmadaMetrics.RecordValidation(ArmadaMetrics.ValidationCheck, result);
            if (result.KillError != null)
                _Logging.Warn(_Header + "could not kill check command; it may still be running: " + result.KillError);
            if (result.Cancelled)
            {
                token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(token);
            }
            string anomalies = BoundedProcessRunner.DescribeAnomalies(result);
            if (anomalies.Length > 0)
                _Logging.Warn(_Header + "check command output: " + anomalies);

            string stdout = result.StandardOutput;
            string stderr = result.StandardError;
            string output = CombineOutput(stdout, stderr);
            if (result.TimedOut)
            {
                string message = "Check command timed out after " + timeout.TotalMinutes.ToString("0") + " minutes.";
                output = String.IsNullOrWhiteSpace(output) ? message : output + Environment.NewLine + message;
                if (anomalies.Length > 0) output += Environment.NewLine + "Check command output: " + anomalies;
            }
            int exitCode = result.TimedOut ? -1 : result.ExitCode ?? -1;

            _Logging.Debug(_Header + "command exited with code " + exitCode + ": " + FirstNonEmptyLine(stderr, stdout));

            return new CommandExecutionResult
            {
                ExitCode = exitCode,
                Output = output,
                Truncated = result.Truncated,
                TimedOut = result.TimedOut
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

        private static void ParseExecutionSummaries(CheckRun run, string artifactDirectory)
        {
            // A file left by an earlier command is visible as an artifact, but cannot certify this run.
            List<CheckRunArtifact> freshArtifacts = run.Artifacts.Where(artifact => run.StartedUtc.HasValue
                && artifact.LastWriteUtc.HasValue && artifact.LastWriteUtc.Value >= run.StartedUtc.Value).ToList();
            run.TestSummary = CheckRunParsingService.ParseTestSummary(run.Output, artifactDirectory, freshArtifacts);
            run.CoverageSummary = CheckRunParsingService.ParseCoverageSummary(artifactDirectory, freshArtifacts);
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

        private static bool IsTestCheckType(CheckRunTypeEnum type)
        {
            return type == CheckRunTypeEnum.UnitTest || type == CheckRunTypeEnum.IntegrationTest;
        }

        /// <summary>
        /// Apply the isolated re-run rule of <see cref="IsolatedRerunPlan"/> to a failed test Check, the same
        /// rule and bound the definition-of-done gate applies. When the run failed on a few named tests that
        /// the profile can re-run alone, they are re-run once in the same checkout under the host command
        /// slot, and the Check passes only when that re-run passes. The summary and output always say that
        /// the full run failed, which tests were re-run and how the re-run ended, or why no re-run applied,
        /// so a pass on re-run is never read as a full-suite pass.
        /// </summary>
        private async Task ApplyIsolatedRerunAsync(
            CheckRun run,
            WorkflowProfile? profile,
            CommandExecutionResult execution,
            string executionCommand,
            string executionDirectory,
            string? diffBaseCommit,
            CheckCommandBudget budget,
            CancellationToken token)
        {
            if (run.Status != CheckRunStatusEnum.Failed || !IsTestCheckType(run.Type)) return;
            // Failure names captured before a timeout do not describe a completed suite.
            if (execution.TimedOut) return;
            if (!IsolatedRerunEnabled()) return;
            if (String.IsNullOrWhiteSpace(executionDirectory) || !Directory.Exists(executionDirectory)) return;

            DefinitionOfDoneFailureClassEnum failureClass = new DefinitionOfDoneFailureClassifier().Classify(
                "unit-test", execution.ExitCode, execution.Output);
            Regex? pattern = null;
            if (!String.IsNullOrWhiteSpace(profile?.FailedTestLinePattern)
                && !FailedTestLinePattern.TryCreate(profile!.FailedTestLinePattern, FailedTestLinePattern.MatchTimeout, out pattern, out string? patternError))
            {
                _Logging.Warn(_Header + "check " + run.Id + " workflow profile " + profile.Id + " FailedTestLinePattern ignored: " + patternError);
                pattern = null;
            }

            FailedTestNameExtractor.FailedTestNameSet failing = failureClass == DefinitionOfDoneFailureClassEnum.TestFail
                ? FailedTestNameExtractor.Extract(execution.Output, pattern)
                : FailedTestNameExtractor.FailedTestNameSet.Empty;
            IsolatedRerunPlan plan = IsolatedRerunPlan.ForFailedRun(
                profile, executionCommand, failureClass, failing.Names, failing.Overflow || failing.PatternTimedOut || execution.Truncated);
            if (plan.NotTestFailure) return;

            string triage = await DescribeFailingTestsInChangeAsync(failing.Names, diffBaseCommit, executionDirectory, token).ConfigureAwait(false);
            string names = String.Join(", ", failing.Names);

            if (plan.Command == null)
            {
                string reason = plan.NotRerunReason ?? IsolatedRerunPlan.NoCommandReason;
                _Logging.Info(_Header + "check " + run.Id + " red not re-run in isolation: " + reason);
                run.Summary = run.Summary + " Not re-run in isolation: " + reason + "." + triage;
                run.Output = run.Output + Environment.NewLine + Environment.NewLine + "Not re-run in isolation: " + reason + ".";
                return;
            }

            if (budget.Exhausted)
            {
                const string exhausted = "Not re-run in isolation: shared execution budget exhausted.";
                run.Summary += " " + exhausted + triage;
                run.Output += Environment.NewLine + Environment.NewLine + exhausted;
                return;
            }

            Dictionary<string, string> environment = await BuildCommandEnvironmentAsync(profile, run, token).ConfigureAwait(false);
            foreach (KeyValuePair<string, string> pair in plan.Environment) environment[pair.Key] = pair.Value;

            _Logging.Info(_Header + "check " + run.Id + " re-running " + plan.Count + " failing " + plan.Unit + " in isolation"
                + (plan.FromProfile ? " through the profile's IsolatedTestRerunCommand" : String.Empty));
            CommandExecutionResult rerun;
            try
            {
                long slotWaitStart = Stopwatch.GetTimestamp();
                using (await HostWideCommandLock.AcquireAsync(run.Id, DescribeSlotOwner(run) + " (isolated re-run)", run.VesselId, token).ConfigureAwait(false))
                {
                    budget.RetrySlotWait += Stopwatch.GetElapsedTime(slotWaitStart);
                    // Re-evaluate the remainder after admission. Waiting for the slot spends no
                    // command budget, and the full run has already spent its own execution time.
                    rerun = await budget.ExecuteAsync(timeout => ExecuteCommandAsync(
                        plan.Command, executionDirectory, timeout, token, environment)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                rerun = new CommandExecutionResult { ExitCode = -1, Output = ex.Message };
            }

            bool passed = rerun.ExitCode == 0;
            _Logging.Info(_Header + "check " + run.Id + " isolated re-run passed=" + passed);
            string fullRunExit = run.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
            string rerunText = "Isolated re-run of " + plan.Count + " failing " + plan.Unit + " (" + names + ") "
                + (passed ? "passed" : "also failed (exit " + rerun.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")") + ".";
            run.Output = run.Output + Environment.NewLine + Environment.NewLine
                + "--- ISOLATED RE-RUN: " + plan.Command + " ---" + Environment.NewLine
                + rerunText + Environment.NewLine + BoundRerunOutput(rerun.Output);

            if (passed)
            {
                string label = !String.IsNullOrWhiteSpace(run.Label) ? run.Label! : run.Type.ToString();
                string? testDetails = BuildTestSummaryText(run.TestSummary);
                run.Status = CheckRunStatusEnum.Passed;
                run.ExitCode = 0;
                run.Summary = label + " passed on isolated re-run. The full run failed (exit " + fullRunExit
                    + (String.IsNullOrWhiteSpace(testDetails) ? String.Empty : "; " + testDetails) + "). "
                    + rerunText + triage;
                return;
            }

            run.Summary = run.Summary + " " + rerunText + triage;
        }

        private static string BoundRerunOutput(string output)
        {
            if (String.IsNullOrEmpty(output) || output.Length <= _RerunOutputLimitChars) return output ?? String.Empty;
            return "...(re-run output truncated)" + Environment.NewLine + output.Substring(output.Length - _RerunOutputLimitChars);
        }

        /// <summary>
        /// Say, for each failing test, whether a file named for its class is in the change since the diff base,
        /// so a failure outside the change is visible at a glance. Empty when no base is known; a diff that
        /// cannot be read is named rather than skipped.
        /// </summary>
        private async Task<string> DescribeFailingTestsInChangeAsync(
            IReadOnlyList<string> failingNames,
            string? diffBaseCommit,
            string executionDirectory,
            CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(diffBaseCommit) || failingNames.Count == 0) return String.Empty;
            string shortBase = diffBaseCommit.Trim().Length > 12 ? diffBaseCommit.Trim().Substring(0, 12) : diffBaseCommit.Trim();

            GitCommandResult diff;
            try
            {
                diff = await RunGitCaptureAsync(executionDirectory, TimeSpan.FromSeconds(60), token, "diff", "--name-only", diffBaseCommit.Trim(), "HEAD").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read the change since " + shortBase + " for failing-test triage: " + ex.Message);
                return " The change since " + shortBase + " could not be read for triage (" + ex.GetType().Name + ").";
            }

            if (diff.ExitCode != 0)
                return " The change since " + shortBase + " could not be read for triage (git exit " + diff.ExitCode + ").";

            HashSet<string> changedStems = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in diff.StdOut.Split('\n'))
            {
                string path = line.Trim();
                if (path.Length > 0) changedStems.Add(Path.GetFileNameWithoutExtension(path));
            }

            List<string> parts = new List<string>();
            foreach (string name in failingNames)
            {
                IReadOnlyList<string> classes = FlakeRerunCommand.DeriveClassNames(new[] { name });
                string verdict = classes.Count == 0
                    ? "class unknown"
                    : (changedStems.Contains(classes[0]) ? "in the change" : "not in the change");
                parts.Add(name + " (" + (classes.Count == 0 ? String.Empty : classes[0] + " file ") + verdict + ")");
            }

            return " Failing tests against the change since " + shortBase + ": " + String.Join("; ", parts) + ".";
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

            SemaphoreSlim stateLock = GetRunStateLock(run.Id);
            await stateLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                CheckRun? current = await _Database.CheckRuns.ReadAsync(run.Id, token: token).ConfigureAwait(false);
                if (current == null) throw new InvalidOperationException("The check run no longer exists.");
                if (current.Status != CheckRunStatusEnum.Pending) return current;

                DateTime now = DateTime.UtcNow;
                current.Status = CheckRunStatusEnum.Canceled;
                current.Summary = "unstamped_voyage_check (" + endedReason + "): this Check carries no branch or commit, "
                    + "and its voyage ended before a stage stamped one. Running it would measure the default branch, "
                    + "not the work under review.";
                current.Output = current.Summary;
                current.CompletedUtc = now;
                current.LastUpdateUtc = now;
                run = await _Database.CheckRuns.UpdateAsync(current, token).ConfigureAwait(false);
            }
            finally
            {
                stateLock.Release();
            }
            OnCheckRunChanged?.Invoke(run);
            await VoyageCheckDiscard.WriteDiscardedEventAsync(_Database, run, "unstamped_voyage_check (" + endedReason + ")", token).ConfigureAwait(false);
            _Logging.Warn(_Header + "cancelled unstamped check " + run.Id + " (" + endedReason + ")");
            return run;
        }

        /// <summary>
        /// The provisioned sibling pins of the objective a check's mission or voyage belongs to.
        /// </summary>
        private async Task<List<ProvisionedSiblingPin>> ResolveRunSiblingPinsAsync(Vessel? vessel, CheckRun run, CancellationToken token)
        {
            string? voyageId = run.VoyageId;
            if (String.IsNullOrWhiteSpace(voyageId) && !String.IsNullOrWhiteSpace(run.MissionId))
                voyageId = (await _Database.Missions.ReadAsync(run.MissionId!, token).ConfigureAwait(false))?.VoyageId;
            return await SiblingPinOverride.ResolvePinsAsync(_Database, vessel, voyageId,
                (repoPath, revision, ct) => ResolveRefCandidateAsync(repoPath, revision, ct), token).ConfigureAwait(false);
        }

        /// <summary>
        /// Why a check under provisioned sibling pins must not run, or null when it may. A pin must resolve, and a
        /// harbor check has no place to provision the pinned sibling beside its checkout.
        /// </summary>
        private static string? DescribePinRefusal(IReadOnlyList<ProvisionedSiblingPin> pins, bool harborSelected)
        {
            if (pins.Count == 0) return null;
            string? unresolved = SiblingPinOverride.DescribeUnresolved(pins);
            if (unresolved != null) return unresolved;
            if (harborSelected)
                return "check_harbor_sibling_pin_override_unsupported: objective " + pins[0].ObjectiveId
                    + " provisions a pinned sibling, and a harbor check cannot provision it. The check did not run.";
            return null;
        }

        private static string DescribeFailedCheckout(IReadOnlyList<ProvisionedSiblingPin> pins)
        {
            if (pins.Count == 0)
                return "Isolated checkout could not be created for the configured repo source. The check will not execute in the live working directory.";
            List<string> siblings = pins.Select(pin => pin.Sibling.RelativePath + " at " + pin.Commit).ToList();
            return "sibling_pin_override_checkout_failed: the isolated checkout with pinned sibling " + String.Join(", ", siblings)
                + " could not be created. The check will not execute without its pinned sibling.";
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
        /// clone is used only as a fallback when the local repository is not usable. Under provisioned sibling pins
        /// the checkout is nested the way a dock is, and each pinned sibling is checked out at its pin beside it.
        /// </summary>
        private async Task<IsolatedCheckout?> TryCreateIsolatedCheckoutAsync(
            Vessel vessel,
            string checkRunId,
            string? commitHash,
            string? branchName,
            string defaultBranch,
            IReadOnlyList<ProvisionedSiblingPin> siblingPins,
            CancellationToken token)
        {
            if (siblingPins.Count > 0)
                return await TryCreatePinnedCheckoutAsync(vessel, checkRunId, commitHash, branchName, defaultBranch, siblingPins, token).ConfigureAwait(false);

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
        /// Create a nested checkout, &lt;root&gt;/&lt;Vessel&gt;, with each pinned sibling as a detached worktree at its
        /// resolved pin at the sibling's relative path. A sibling path that leaves the checkout root is refused, so a
        /// check never reads or moves a sibling checkout another run shares.
        /// </summary>
        private async Task<IsolatedCheckout?> TryCreatePinnedCheckoutAsync(
            Vessel vessel,
            string checkRunId,
            string? commitHash,
            string? branchName,
            string defaultBranch,
            IReadOnlyList<ProvisionedSiblingPin> siblingPins,
            CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(vessel.LocalPath) || !Directory.Exists(vessel.LocalPath) || String.IsNullOrWhiteSpace(vessel.Name))
            {
                _Logging.Warn(_Header + "isolated checkout: a pinned sibling needs the vessel's local repository and name, and vessel " + vessel.Id + " has none");
                return null;
            }

            string root = BuildCheckoutPath(checkRunId);
            string rootPrefix = root + Path.DirectorySeparatorChar;
            string checkoutPath = Path.Combine(root, vessel.Name);
            IsolatedCheckout? main = await TryCreateDetachedWorktreeAsync(vessel.LocalPath!, checkRunId, commitHash, branchName, defaultBranch, token, checkoutPath).ConfigureAwait(false);
            if (main == null)
            {
                SafeDeleteDirectory(root);
                return null;
            }

            IsolatedCheckout checkout = new IsolatedCheckout(checkoutPath, vessel.LocalPath, root, siblingPins);
            foreach (ProvisionedSiblingPin pin in siblingPins)
            {
                string siblingPath = Path.GetFullPath(Path.Combine(checkoutPath, pin.Sibling.RelativePath));
                IsolatedCheckout? sibling = null;
                if (siblingPath.StartsWith(rootPrefix, StringComparison.Ordinal) && pin.RepoPath != null && pin.Commit != null)
                    sibling = await TryCreateDetachedWorktreeAsync(pin.RepoPath, checkRunId, pin.Commit, null, defaultBranch, token, siblingPath).ConfigureAwait(false);
                else
                    _Logging.Warn(_Header + "isolated checkout: pinned sibling " + pin.Sibling.RelativePath + " does not resolve inside " + root);

                if (sibling == null)
                {
                    await CleanupIsolatedCheckoutAsync(checkout, CancellationToken.None).ConfigureAwait(false);
                    return null;
                }
                checkout.Siblings.Add(sibling);
            }

            _Logging.Info(_Header + "isolated checkout created at " + checkoutPath + " with " + checkout.Siblings.Count + " pinned sibling(s)");
            return checkout;
        }

        /// <summary>
        /// Cut a detached worktree from <paramref name="repoPath"/> at the requested commit, or at
        /// the best resolvable branch ref when no commit hash was supplied. The worktree shares the
        /// repository's own
        /// config, so its checkout EOL and filters match a mission dock created from the same repo.
        /// </summary>
        private async Task<IsolatedCheckout?> TryCreateDetachedWorktreeAsync(
            string repoPath,
            string checkRunId,
            string? commitHash,
            string? branchName,
            string defaultBranch,
            CancellationToken token,
            string? checkoutPath = null)
        {
            string tempPath = checkoutPath ?? BuildCheckoutPath(checkRunId);
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

                string? checkedOutSha = await ResolveRefCandidateAsync(tempPath, "HEAD", token).ConfigureAwait(false);
                if (!String.Equals(sha, checkedOutSha, StringComparison.OrdinalIgnoreCase))
                {
                    _Logging.Warn(_Header + "isolated checkout: worktree HEAD did not match requested ref in " + repoPath);
                    await CleanupIsolatedCheckoutAsync(new IsolatedCheckout(tempPath, repoPath), CancellationToken.None).ConfigureAwait(false);
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
        /// Resolve the requested checkout ref to a commit in the repository. An explicit commit hash
        /// is authoritative; branch and default refs are considered only when no hash was supplied.
        /// Branch names resolve through local, origin remote, and bare-name spellings so the checkout
        /// works for both bare and normal repos.
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
                return await ResolveRefCandidateAsync(repoPath, commitHash!, token).ConfigureAwait(false);
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
        /// <summary>
        /// Return the checkout a check runs in once it holds the host command slot. The checkout is made
        /// before the wait for the slot, and that wait can last as long as the suites ahead of it, so it is
        /// checked again here: a checkout that is gone is made once more at the same source.
        /// </summary>
        /// <returns>The checkout to run in, or null when it was gone and could not be made again.</returns>
        private async Task<IsolatedCheckout?> RecreateRemovedCheckoutAsync(Vessel vessel, CheckRun run, IsolatedCheckout checkout, CancellationToken token)
        {
            if (Directory.Exists(checkout.Path) && checkout.Siblings.TrueForAll(sibling => Directory.Exists(sibling.Path))) return checkout;

            _Logging.Warn(_Header + "isolated checkout " + checkout.Path + " for check " + run.Id
                + " was removed while the check waited for the host command slot; creating it again");
            await CleanupIsolatedCheckoutAsync(checkout, CancellationToken.None).ConfigureAwait(false);
            return await TryCreateIsolatedCheckoutAsync(vessel, run.Id, run.CommitHash, run.BranchName, vessel.DefaultBranch, checkout.Pins, token).ConfigureAwait(false);
        }

        private static string DescribeRemovedCheckout(string path)
        {
            return "The isolated checkout " + path + " was removed while the check waited for the host command slot, "
                + "and it could not be created again. The check did not run.";
        }

        private async Task CleanupIsolatedCheckoutAsync(IsolatedCheckout? checkout, CancellationToken token)
        {
            if (checkout == null)
            {
                return;
            }

            foreach (IsolatedCheckout sibling in checkout.Siblings)
                await CleanupIsolatedCheckoutAsync(sibling, token).ConfigureAwait(false);

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

            if (checkout.RootPath != null) SafeDeleteDirectory(checkout.RootPath);
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

                string? targetSha = await ResolveCheckoutShaAsync(tempPath, commitHash, branchName, defaultBranch, token).ConfigureAwait(false);
                if (String.IsNullOrWhiteSpace(targetSha))
                {
                    _Logging.Warn(_Header + "isolated checkout: no resolvable ref in cloned repo " + repoSource);
                    SafeDeleteDirectory(tempPath);
                    return null;
                }

                int checkoutExit;
                if (!String.IsNullOrWhiteSpace(commitHash))
                {
                    checkoutExit = await RunGitAsync(
                        tempPath,
                        TimeSpan.FromMinutes(2),
                        token,
                        "checkout", "--detach", targetSha).ConfigureAwait(false);
                }
                else
                {
                    string checkoutRef = !String.IsNullOrWhiteSpace(branchName) ? branchName! : defaultBranch;
                    checkoutExit = await RunGitAsync(
                        tempPath,
                        TimeSpan.FromMinutes(2),
                        token,
                        "checkout", checkoutRef, "--").ConfigureAwait(false);

                    if (checkoutExit != 0
                        && !String.IsNullOrWhiteSpace(branchName)
                        && !String.Equals(branchName, defaultBranch, StringComparison.Ordinal))
                    {
                        targetSha = await ResolveBranchCandidateAsync(tempPath, defaultBranch, token).ConfigureAwait(false);
                        if (String.IsNullOrWhiteSpace(targetSha))
                        {
                            _Logging.Warn(_Header + "isolated checkout: default branch did not resolve in clone " + repoSource);
                            SafeDeleteDirectory(tempPath);
                            return null;
                        }

                        checkoutExit = await RunGitAsync(
                            tempPath,
                            TimeSpan.FromMinutes(2),
                            token,
                            "checkout", defaultBranch, "--").ConfigureAwait(false);
                    }
                }

                if (checkoutExit != 0)
                {
                    _Logging.Warn(_Header + "isolated checkout: git checkout failed in clone " + repoSource);
                    SafeDeleteDirectory(tempPath);
                    return null;
                }

                string? checkedOutSha = await ResolveRefCandidateAsync(tempPath, "HEAD", token).ConfigureAwait(false);
                if (!String.Equals(targetSha, checkedOutSha, StringComparison.OrdinalIgnoreCase))
                {
                    _Logging.Warn(_Header + "isolated checkout: clone HEAD did not match requested ref " + repoSource);
                    SafeDeleteDirectory(tempPath);
                    return null;
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
            public bool Truncated { get; set; } = false;
            public bool TimedOut { get; set; } = false;
        }

        /// <summary>
        /// A temporary checkout a check run executes in, with the repository it was cut from so the
        /// worktree registration can be removed at cleanup. <see cref="RepoPath"/> is null when the
        /// checkout came from a plain clone. A checkout under provisioned sibling pins sits inside its own
        /// <see cref="RootPath"/> beside its pinned <see cref="Siblings"/>.
        /// </summary>
        private sealed class IsolatedCheckout
        {
            public IsolatedCheckout(string path, string? repoPath, string? rootPath = null, IReadOnlyList<ProvisionedSiblingPin>? pins = null)
            {
                Path = path;
                RepoPath = repoPath;
                RootPath = rootPath;
                Pins = pins ?? new List<ProvisionedSiblingPin>();
            }

            public string Path { get; }

            public string? RepoPath { get; }

            /// <summary>Directory that holds the checkout and its pinned siblings, or null for a flat checkout.</summary>
            public string? RootPath { get; }

            /// <summary>Sibling pins the checkout was made under.</summary>
            public IReadOnlyList<ProvisionedSiblingPin> Pins { get; }

            /// <summary>Pinned sibling worktrees beside the checkout.</summary>
            public List<IsolatedCheckout> Siblings { get; } = new List<IsolatedCheckout>();
        }

        private sealed class GitCommandResult
        {
            public int ExitCode { get; set; } = -1;

            public string StdOut { get; set; } = String.Empty;
        }
    }
}
