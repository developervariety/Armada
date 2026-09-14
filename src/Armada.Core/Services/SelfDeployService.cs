namespace Armada.Core.Services
{
    using System.Text.Json;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Debounced self-deploy pipeline: workdir sync, immutable rollback capture, Release build gate,
    /// safety preflight, immutable candidate capture, and a supervised cutover handshake. Every failed
    /// step opens an incident and keeps the running admiral as the owner.
    /// </summary>
    public sealed class SelfDeployService : ISelfDeployService
    {
        private const string ServerEntryAssembly = "Armada.Server.dll";

        private readonly LoggingModule _Logging;
        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;
        private readonly IGitService _Git;
        private readonly ISelfDeployBuildRunner _BuildRunner;
        private readonly SelfDeployCutoverComponents _Cutover;
        private readonly ISelfDeployPreflight _Preflight;
        private readonly Action? _RequestProcessExit;
        private readonly object _ScheduleGate = new object();
        private readonly SelfDeployScheduleState _ScheduleState = new SelfDeployScheduleState();
        private const string _Header = "[SelfDeployService] ";

        /// <summary>
        /// Instantiate with the default native preflight for the configured database.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Armada database driver.</param>
        /// <param name="settings">Armada settings.</param>
        /// <param name="git">Git service.</param>
        /// <param name="buildRunner">Release build runner.</param>
        /// <param name="cutover">Cutover collaborators.</param>
        /// <param name="requestProcessExit">Optional process-exit callback.</param>
        public SelfDeployService(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            IGitService git,
            ISelfDeployBuildRunner buildRunner,
            SelfDeployCutoverComponents cutover,
            Action? requestProcessExit = null)
            : this(
                logging,
                database,
                settings,
                git,
                buildRunner,
                cutover,
                SelfDeployNativePreflight.CreateDefault(settings ?? throw new ArgumentNullException(nameof(settings))),
                requestProcessExit)
        {
        }

        /// <summary>
        /// Instantiate with an explicit preflight provider.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Armada database driver.</param>
        /// <param name="settings">Armada settings.</param>
        /// <param name="git">Git service.</param>
        /// <param name="buildRunner">Release build runner.</param>
        /// <param name="cutover">Cutover collaborators.</param>
        /// <param name="preflight">Provider that proves backup, restore verification, and candidate validation.</param>
        /// <param name="requestProcessExit">Optional process-exit callback.</param>
        public SelfDeployService(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            IGitService git,
            ISelfDeployBuildRunner buildRunner,
            SelfDeployCutoverComponents cutover,
            ISelfDeployPreflight preflight,
            Action? requestProcessExit = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _BuildRunner = buildRunner ?? throw new ArgumentNullException(nameof(buildRunner));
            _Cutover = cutover ?? throw new ArgumentNullException(nameof(cutover));
            _Preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
            _RequestProcessExit = requestProcessExit;
        }

        /// <inheritdoc />
        public void ScheduleAfterLand(string? vesselId, string? mergeEntryId, string reason)
        {
            if (!_Settings.SelfDeploy.Enabled) return;
            if (String.IsNullOrWhiteSpace(vesselId)) return;

            string capturedVesselId = vesselId.Trim();
            string capturedEntryId = mergeEntryId ?? String.Empty;
            string capturedReason = String.IsNullOrWhiteSpace(reason) ? "successful landing" : reason;
            bool startWorker = false;

            lock (_ScheduleGate)
            {
                _ScheduleState.Generation++;
                _ScheduleState.VesselId = capturedVesselId;
                _ScheduleState.MergeEntryId = capturedEntryId;
                _ScheduleState.Reason = capturedReason;
                if (!_ScheduleState.WorkerStarted)
                {
                    _ScheduleState.WorkerStarted = true;
                    startWorker = true;
                }
            }

            if (!startWorker) return;

            _ = Task.Run(async () =>
            {
                await RunScheduledWorkerAsync().ConfigureAwait(false);
            });
        }

        /// <inheritdoc />
        public async Task<bool> ExecuteAsync(
            string? vesselId,
            string? mergeEntryId,
            string reason,
            CancellationToken token = default)
        {
            SelfDeploySettings settings = _Settings.SelfDeploy;
            if (!settings.Enabled)
            {
                await EmitSkippedAsync(vesselId, mergeEntryId, "disabled", token).ConfigureAwait(false);
                return false;
            }

            Vessel? selfVessel = await ResolveSelfVesselAsync(token).ConfigureAwait(false);
            if (selfVessel == null)
            {
                await EmitSkippedAsync(vesselId, mergeEntryId, "self_vessel_not_resolved", token).ConfigureAwait(false);
                return false;
            }

            if (!String.Equals(vesselId, selfVessel.Id, StringComparison.OrdinalIgnoreCase))
            {
                await EmitSkippedAsync(vesselId, mergeEntryId, "not_self_vessel", token).ConfigureAwait(false);
                return false;
            }

            if (_Cutover.Environment.IsContainer)
            {
                await BlockAsync(selfVessel, mergeEntryId, "container_host_requires_external_deploy",
                    "Self-deploy is disabled inside a container; the container runtime owns the admiral process", token).ConfigureAwait(false);
                return false;
            }

            bool queueIdle = await WaitForMergeQueueDrainAsync(selfVessel.Id, settings, token).ConfigureAwait(false);
            if (!queueIdle)
            {
                await EmitSkippedAsync(vesselId, mergeEntryId, "merge_queue_busy", token).ConfigureAwait(false);
                return false;
            }

            string? workingDirectory = selfVessel.WorkingDirectory;
            if (String.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                await EmitSkippedAsync(vesselId, mergeEntryId, "working_directory_missing", token).ConfigureAwait(false);
                return false;
            }

            string defaultBranch = !String.IsNullOrEmpty(selfVessel.DefaultBranch) ? selfVessel.DefaultBranch : "main";
            string? syncReason = await SyncWorkingDirectoryAsync(selfVessel, workingDirectory, defaultBranch, mergeEntryId, token).ConfigureAwait(false);
            if (!String.IsNullOrEmpty(syncReason))
            {
                await OpenBuildIncidentAsync(selfVessel, mergeEntryId, "WorkingDirectory sync blocked: " + syncReason, syncReason, token).ConfigureAwait(false);
                return false;
            }

            // The running server may execute from the build output the Release build overwrites, so the
            // rollback artifact is captured before building.
            SelfDeployReleaseArtifact rollback;
            try
            {
                rollback = await _Cutover.Artifacts.CaptureAsync(_Cutover.Environment.CurrentServerDirectory, ServerEntryAssembly, token).ConfigureAwait(false);
            }
            catch (SelfDeployCutoverException ex)
            {
                await BlockAsync(selfVessel, mergeEntryId, "rollback_" + ex.FailureReason,
                    "Rollback artifact capture failed; admiral restart aborted", token).ConfigureAwait(false);
                return false;
            }

            await EmitEventAsync("self_deploy.build_started", selfVessel.Id, mergeEntryId,
                "Release build started for self-deploy after " + reason, new { vesselId = selfVessel.Id, mergeEntryId, reason }, token).ConfigureAwait(false);

            SelfDeployBuildResult buildResult = await _BuildRunner.BuildAsync(workingDirectory, settings, token).ConfigureAwait(false);
            if (!buildResult.Succeeded)
            {
                await EmitEventAsync("self_deploy.build_failed", selfVessel.Id, mergeEntryId,
                    "Release build failed; admiral restart aborted",
                    new { vesselId = selfVessel.Id, mergeEntryId, buildResult.ExitCode, buildResult.OutputTail }, token).ConfigureAwait(false);
                await OpenBuildIncidentAsync(selfVessel, mergeEntryId,
                    "Self-deploy Release build failed with exit code " + buildResult.ExitCode,
                    buildResult.OutputTail, token).ConfigureAwait(false);
                return false;
            }

            string serverDllPath = Path.GetFullPath(Path.Combine(workingDirectory, settings.ServerDllRelativePath));

            await EmitEventAsync("self_deploy.preflight_started", selfVessel.Id, mergeEntryId,
                "Self-deploy safety preflight started",
                new { vesselId = selfVessel.Id, mergeEntryId, serverDllPath }, token).ConfigureAwait(false);

            SelfDeployPreflightResult? preflightResult;
            try
            {
                preflightResult = await _Preflight.ValidateAsync(new SelfDeployPreflightRequest
                {
                    VesselId = selfVessel.Id,
                    WorkingDirectory = workingDirectory,
                    CandidateServerDllPath = serverDllPath,
                    Settings = settings,
                    BuildResult = buildResult
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                preflightResult = new SelfDeployPreflightResult
                {
                    FailureReason = "preflight_provider_failed"
                };
            }

            if (preflightResult == null || !preflightResult.IsSafeToCutover)
            {
                string preflightFailure = DescribePreflightFailure(preflightResult);
                await EmitEventAsync("self_deploy.preflight_failed", selfVessel.Id, mergeEntryId,
                    "Self-deploy preflight failed; admiral restart aborted",
                    new
                    {
                        vesselId = selfVessel.Id,
                        mergeEntryId,
                        reason = preflightFailure,
                        backupValidated = preflightResult?.BackupValidated ?? false,
                        restoreVerified = preflightResult?.RestoreVerified ?? false,
                        candidateValidated = preflightResult?.CandidateValidated ?? false,
                        outputTail = preflightResult?.OutputTail ?? String.Empty
                    }, token).ConfigureAwait(false);
                await OpenBuildIncidentAsync(selfVessel, mergeEntryId,
                    "Self-deploy preflight failed; admiral restart aborted",
                    preflightFailure + (String.IsNullOrWhiteSpace(preflightResult?.OutputTail)
                        ? String.Empty
                        : ": " + preflightResult.OutputTail), token).ConfigureAwait(false);
                return false;
            }

            await EmitEventAsync("self_deploy.preflight_succeeded", selfVessel.Id, mergeEntryId,
                "Self-deploy preflight passed; preparing supervised cutover",
                new { vesselId = selfVessel.Id, mergeEntryId }, token).ConfigureAwait(false);

            return await PrepareCutoverAsync(selfVessel, mergeEntryId, rollback, serverDllPath, token).ConfigureAwait(false);
        }

        private async Task<bool> PrepareCutoverAsync(
            Vessel selfVessel,
            string? mergeEntryId,
            SelfDeployReleaseArtifact rollback,
            string serverDllPath,
            CancellationToken token)
        {
            SelfDeployReleaseArtifact candidate;
            try
            {
                string candidateDirectory = Path.GetDirectoryName(serverDllPath) ?? String.Empty;
                candidate = await _Cutover.Artifacts.CaptureAsync(candidateDirectory, Path.GetFileName(serverDllPath), token).ConfigureAwait(false);
            }
            catch (SelfDeployCutoverException ex)
            {
                await BlockAsync(selfVessel, mergeEntryId, "candidate_" + ex.FailureReason,
                    "Candidate artifact capture failed; admiral restart aborted", token).ConfigureAwait(false);
                return false;
            }

            SelfDeployReleasePruneResult pruned = await SelfDeployReleaseRetention.PruneAsync(
                _Cutover.Artifacts,
                _Cutover.Records,
                new[] { rollback.Digest, candidate.Digest },
                _Settings.SelfDeploy.RetainedPreviousReleases,
                token).ConfigureAwait(false);
            if (!String.IsNullOrWhiteSpace(pruned.FailureReason))
            {
                // Retention bounds disk use; it is not a safety precondition, so a failure is reported, not blocking.
                await EmitEventAsync("self_deploy.release_prune_failed", selfVessel.Id, mergeEntryId,
                    "Self-deploy release store could not be fully pruned",
                    new { vesselId = selfVessel.Id, mergeEntryId, reason = pruned.FailureReason, removed = pruned.Removed }, token).ConfigureAwait(false);
            }
            else if (pruned.Removed.Count > 0)
            {
                await EmitEventAsync("self_deploy.releases_pruned", selfVessel.Id, mergeEntryId,
                    "Removed " + pruned.Removed.Count + " previous self-deploy release(s)",
                    new { vesselId = selfVessel.Id, mergeEntryId, removed = pruned.Removed, retained = pruned.RetainedPrevious }, token).ConfigureAwait(false);
            }

            int schemaVersion;
            try
            {
                schemaVersion = await _Database.GetSchemaVersionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                await BlockAsync(selfVessel, mergeEntryId, "schema_version_unreadable",
                    "Schema version could not be read before cutover; admiral restart aborted", token).ConfigureAwait(false);
                return false;
            }

            SelfDeployProcessIdentity? admiral = _Cutover.ProcessHost.Capture(_Cutover.Environment.CurrentProcessId);
            if (admiral == null)
            {
                await BlockAsync(selfVessel, mergeEntryId, "admiral_identity_unverified",
                    "Running admiral identity could not be verified; admiral restart aborted", token).ConfigureAwait(false);
                return false;
            }

            string operationId = "sdo_" + Guid.NewGuid().ToString("N");
            SelfDeployRestartRecord record = new SelfDeployRestartRecord
            {
                OperationId = operationId,
                CreatedUtc = DateTime.UtcNow,
                OldProcess = admiral,
                Candidate = candidate,
                Rollback = rollback,
                SchemaVersionBefore = schemaVersion,
                HealthUrl = SelfDeployHttpHealthProbe.LoopbackHealthUrl(_Settings.AdmiralPort)
            };
            record.MoveTo(SelfDeployRestartStateEnum.Prepared, "admiral_prepared");

            SelfDeployRestartTransitionResult created;
            try
            {
                created = await _Cutover.Records.CreateAsync(record, token).ConfigureAwait(false);
            }
            catch (SelfDeployCutoverException ex)
            {
                await BlockAsync(selfVessel, mergeEntryId, ex.FailureReason,
                    "Restart record could not be written; admiral restart aborted", token).ConfigureAwait(false);
                return false;
            }
            if (!created.Applied)
            {
                await BlockAsync(selfVessel, mergeEntryId, created.FailureReason,
                    "Restart record is not available for a new cutover; admiral restart aborted", token).ConfigureAwait(false);
                return false;
            }

            SelfDeployProcessIdentity supervisor;
            try
            {
                supervisor = _Cutover.ProcessHost.Start(_Cutover.LaunchPlanner.ForSupervisor(rollback, operationId));
            }
            catch (SelfDeployCutoverException ex)
            {
                await AbortPreparedAsync(operationId, "supervisor_" + ex.FailureReason, token).ConfigureAwait(false);
                await BlockAsync(selfVessel, mergeEntryId, "supervisor_" + ex.FailureReason,
                    "Self-deploy supervisor failed to start; admiral restart aborted", token).ConfigureAwait(false);
                return false;
            }

            string? armFailure = await WaitForSupervisorArmAsync(operationId, supervisor, token).ConfigureAwait(false);
            if (armFailure != null)
            {
                await BlockAsync(selfVessel, mergeEntryId, armFailure,
                    "Self-deploy supervisor did not arm; admiral restart aborted", token).ConfigureAwait(false);
                return false;
            }

            SelfDeployRestartTransitionResult exitRequested = await _Cutover.Records.TryTransitionAsync(operationId,
                new[] { SelfDeployRestartStateEnum.Armed },
                r => r.MoveTo(SelfDeployRestartStateEnum.ExitRequested, "admiral_exit_requested"), token).ConfigureAwait(false);
            if (!exitRequested.Applied)
            {
                await BlockAsync(selfVessel, mergeEntryId, "exit_request_rejected_" + exitRequested.FailureReason,
                    "Self-deploy supervisor ended the handshake; admiral restart aborted", token).ConfigureAwait(false);
                return false;
            }

            await EmitEventAsync("self_deploy.restart_requested", selfVessel.Id, mergeEntryId,
                "Supervised cutover armed; admiral " + admiral.ProcessId + " is exiting",
                new
                {
                    vesselId = selfVessel.Id,
                    mergeEntryId,
                    operationId,
                    admiralPid = admiral.ProcessId,
                    supervisorPid = supervisor.ProcessId,
                    candidateDigest = candidate.Digest,
                    rollbackDigest = rollback.Digest,
                    schemaVersion
                }, token).ConfigureAwait(false);

            if (_RequestProcessExit != null)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    try
                    {
                        _RequestProcessExit();
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "process exit hook failed: " + ex.Message);
                    }
                });
            }

            return true;
        }

        private async Task<string?> WaitForSupervisorArmAsync(
            string operationId,
            SelfDeployProcessIdentity supervisor,
            CancellationToken token)
        {
            SelfDeployCutoverOptions options = _Cutover.Options;
            DateTime deadline = DateTime.UtcNow + options.HandshakeTimeout;
            while (DateTime.UtcNow < deadline)
            {
                SelfDeployRestartRecordReadResult read = await _Cutover.Records.ReadAsync(token).ConfigureAwait(false);
                if (!read.IsReadable) return "restart_record_" + (read.Exists ? read.FailureReason : "missing");
                SelfDeployRestartRecord current = read.Record!;
                if (current.State == SelfDeployRestartStateEnum.Armed) return null;
                if (current.State != SelfDeployRestartStateEnum.Prepared) return "supervisor_" + current.Reason;
                await Task.Delay(options.PollInterval, token).ConfigureAwait(false);
            }

            SelfDeployRestartTransitionResult aborted = await AbortPreparedAsync(operationId, "supervisor_arm_timeout", token).ConfigureAwait(false);
            if (!aborted.Applied)
            {
                if (aborted.Record != null && aborted.Record.State == SelfDeployRestartStateEnum.Armed) return null;
                return "supervisor_arm_timeout_" + aborted.FailureReason;
            }

            bool stopped = await _Cutover.ProcessHost.TerminateAsync(supervisor, true, options.TerminationTimeout, token).ConfigureAwait(false);
            return stopped ? "supervisor_arm_timeout" : "supervisor_arm_timeout_supervisor_exit_unconfirmed";
        }

        private async Task<SelfDeployRestartTransitionResult> AbortPreparedAsync(string operationId, string reason, CancellationToken token)
        {
            return await _Cutover.Records.TryTransitionAsync(operationId,
                new[] { SelfDeployRestartStateEnum.Prepared },
                r => r.MoveTo(SelfDeployRestartStateEnum.Aborted, reason), token).ConfigureAwait(false);
        }

        private async Task BlockAsync(Vessel vessel, string? mergeEntryId, string reason, string summary, CancellationToken token)
        {
            await EmitEventAsync("self_deploy.cutover_blocked", vessel.Id, mergeEntryId, summary,
                new { vesselId = vessel.Id, mergeEntryId, reason }, token).ConfigureAwait(false);
            await OpenBuildIncidentAsync(vessel, mergeEntryId, summary, reason, token).ConfigureAwait(false);
        }

        private async Task RunScheduledWorkerAsync()
        {
            while (true)
            {
                int generation;
                string vesselId;
                string mergeEntryId;
                string reason;
                lock (_ScheduleGate)
                {
                    generation = _ScheduleState.Generation;
                    vesselId = _ScheduleState.VesselId;
                    mergeEntryId = _ScheduleState.MergeEntryId;
                    reason = _ScheduleState.Reason;
                }

                int delayMs = _Settings.SelfDeploy.DebounceSeconds * 1000;
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }

                lock (_ScheduleGate)
                {
                    if (generation != _ScheduleState.Generation)
                    {
                        continue;
                    }
                }

                try
                {
                    await ExecuteAsync(vesselId, mergeEntryId, reason).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "scheduled self-deploy failed: " + ex.Message);
                }

                lock (_ScheduleGate)
                {
                    if (generation == _ScheduleState.Generation)
                    {
                        _ScheduleState.WorkerStarted = false;
                        return;
                    }
                }
            }
        }

        private async Task<Vessel?> ResolveSelfVesselAsync(CancellationToken token)
        {
            SelfDeploySettings settings = _Settings.SelfDeploy;
            if (!String.IsNullOrWhiteSpace(settings.SelfVesselId))
            {
                try
                {
                    return await _Database.Vessels.ReadAsync(settings.SelfVesselId.Trim(), token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not read configured self vessel " + settings.SelfVesselId + ": " + ex.Message);
                    return null;
                }
            }

            string baseDir = AppContext.BaseDirectory;
            if (String.IsNullOrEmpty(baseDir)) return null;

            List<Vessel> vessels = await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false);
            foreach (Vessel vessel in vessels)
            {
                if (String.IsNullOrEmpty(vessel.WorkingDirectory)) continue;

                string normalizedVesselPath = vessel.WorkingDirectory.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (baseDir.StartsWith(
                    normalizedVesselPath + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                    || String.Equals(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        normalizedVesselPath, StringComparison.OrdinalIgnoreCase))
                {
                    return vessel;
                }
            }

            return null;
        }

        private async Task<bool> WaitForMergeQueueDrainAsync(string vesselId, SelfDeploySettings settings, CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(settings.MergeQueueDrainTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (!await HasActiveLandingWorkAsync(vesselId, token).ConfigureAwait(false))
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }

            return !await HasActiveLandingWorkAsync(vesselId, token).ConfigureAwait(false);
        }

        private async Task<bool> HasActiveLandingWorkAsync(string vesselId, CancellationToken token)
        {
            List<MergeEntry> entries = await _Database.MergeEntries.EnumerateAsync(token).ConfigureAwait(false);
            foreach (MergeEntry entry in entries)
            {
                if (!String.Equals(entry.VesselId, vesselId, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsLandingState(entry.Status)) return true;
            }

            return false;
        }

        private static bool IsLandingState(MergeStatusEnum status)
        {
            return status == MergeStatusEnum.Queued
                || status == MergeStatusEnum.Rebasing
                || status == MergeStatusEnum.Merging
                || status == MergeStatusEnum.Testing
                || status == MergeStatusEnum.Passed
                || status == MergeStatusEnum.Pushing
                || status == MergeStatusEnum.CreatingPR;
        }

        private async Task<string?> SyncWorkingDirectoryAsync(
            Vessel vessel,
            string workingDirectory,
            string defaultBranch,
            string? mergeEntryId,
            CancellationToken token)
        {
            if (!await _Git.IsRepositoryAsync(workingDirectory, token).ConfigureAwait(false))
            {
                return "not_a_repository";
            }

            string? currentBranch = await _Git.GetCurrentBranchAsync(workingDirectory, token).ConfigureAwait(false);
            if (!String.Equals(currentBranch, defaultBranch, StringComparison.OrdinalIgnoreCase))
            {
                return "on_non_default_branch";
            }

            if (!await _Git.IsWorkingDirectoryCleanAsync(workingDirectory, token).ConfigureAwait(false))
            {
                return "dirty_working_directory";
            }

            string upstreamRef = "origin/" + defaultBranch;
            int aheadCount = 0;
            int behindCount = 0;
            try
            {
                await _Git.FetchAsync(workingDirectory, token).ConfigureAwait(false);
                aheadCount = await _Git.GetCommitCountBetweenAsync(workingDirectory, upstreamRef, "HEAD", token).ConfigureAwait(false);
                behindCount = await _Git.GetCommitCountBetweenAsync(workingDirectory, "HEAD", upstreamRef, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not evaluate upstream divergence: " + ex.Message);
                return "upstream_check_failed";
            }

            if (aheadCount > 0)
            {
                await EmitEventAsync("self_deploy.workdir_diverged", vessel.Id, mergeEntryId,
                    "WorkingDirectory has unpushed local commits; preserving state",
                    new { vesselId = vessel.Id, mergeEntryId, aheadCount, behindCount }, token).ConfigureAwait(false);
                return "unpushed_local_commits";
            }

            if (behindCount > 0)
            {
                try
                {
                    await _Git.PullFastForwardOnlyAsync(workingDirectory, token).ConfigureAwait(false);
                    await EmitEventAsync("self_deploy.workdir_synced", vessel.Id, mergeEntryId,
                        "WorkingDirectory fast-forwarded for self-deploy",
                        new { vesselId = vessel.Id, mergeEntryId, behindCount }, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await EmitEventAsync("self_deploy.workdir_diverged", vessel.Id, mergeEntryId,
                        "WorkingDirectory fast-forward failed; preserving state",
                        new { vesselId = vessel.Id, mergeEntryId, behindCount, error = ex.Message }, token).ConfigureAwait(false);
                    return "fast_forward_failed";
                }
            }

            return null;
        }

        private async Task OpenBuildIncidentAsync(
            Vessel vessel,
            string? mergeEntryId,
            string summary,
            string detail,
            CancellationToken token)
        {
            try
            {
                IncidentService incidents = new IncidentService(_Database);
                AuthContext auth = AuthContext.Authenticated(
                    Constants.DefaultTenantId,
                    Constants.DefaultUserId,
                    true,
                    true,
                    "SelfDeploy");

                Incident created = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Self-deploy blocked",
                    Summary = summary,
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.High,
                    VesselId = vessel.Id,
                    Impact = "Admiral self-deploy did not restart the running server.",
                    RootCause = detail,
                    RecoveryNotes = "Inspect WorkingDirectory sync state, Release build output, preflight result and the self-deploy restart record. The running admiral was left online.",
                    DetectedUtc = DateTime.UtcNow
                }, token).ConfigureAwait(false);

                await EmitEventAsync("self_deploy.incident_opened", vessel.Id, mergeEntryId,
                    "Opened incident " + created.Id + " for self-deploy failure",
                    new { vesselId = vessel.Id, mergeEntryId, incidentId = created.Id, summary }, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to open self-deploy incident: " + ex.Message);
            }
        }

        private async Task EmitSkippedAsync(string? vesselId, string? mergeEntryId, string reason, CancellationToken token)
        {
            await EmitEventAsync("self_deploy.skipped", vesselId, mergeEntryId,
                "Self-deploy skipped: " + reason,
                new { vesselId, mergeEntryId, reason }, token).ConfigureAwait(false);
        }

        private async Task EmitEventAsync(
            string eventType,
            string? vesselId,
            string? mergeEntryId,
            string message,
            object payload,
            CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent();
                evt.TenantId = Constants.DefaultTenantId;
                evt.EventType = eventType;
                evt.EntityType = "vessel";
                evt.EntityId = vesselId;
                evt.VesselId = vesselId;
                evt.Message = message;
                evt.Payload = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["vesselId"] = vesselId,
                    ["mergeEntryId"] = mergeEntryId,
                    ["detail"] = payload
                });

                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to emit " + eventType + ": " + ex.Message);
            }
        }

        private static string DescribePreflightFailure(SelfDeployPreflightResult? result)
        {
            if (result == null) return "preflight_provider_returned_no_result";
            if (!String.IsNullOrWhiteSpace(result.FailureReason)) return result.FailureReason;
            if (!result.BackupValidated) return "backup_not_validated";
            if (!result.RestoreVerified) return "restore_verification_failed";
            if (!result.CandidateValidated) return "candidate_validation_failed";
            return "preflight_failed";
        }

        private sealed class SelfDeployScheduleState
        {
            public int Generation { get; set; }
            public bool WorkerStarted { get; set; }
            public string VesselId { get; set; } = String.Empty;
            public string MergeEntryId { get; set; } = String.Empty;
            public string Reason { get; set; } = "successful landing";
        }
    }
}
