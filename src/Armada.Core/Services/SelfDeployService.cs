namespace Armada.Core.Services
{
    using System.Diagnostics;
    using System.Text.Json;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Debounced self-deploy pipeline: workdir sync, Release build gate, incident on failure,
    /// and supervised restart on success.
    /// </summary>
    public sealed class SelfDeployService : ISelfDeployService
    {
        private readonly LoggingModule _Logging;
        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;
        private readonly IGitService _Git;
        private readonly ISelfDeployBuildRunner _BuildRunner;
        private readonly ISelfDeploySupervisor _Supervisor;
        private readonly Action? _RequestProcessExit;
        private readonly object _ScheduleGate = new object();
        private readonly SelfDeployScheduleState _ScheduleState = new SelfDeployScheduleState();
        private const string _Header = "[SelfDeployService] ";

        /// <summary>
        /// Instantiate.
        /// </summary>
        public SelfDeployService(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            IGitService git,
            ISelfDeployBuildRunner buildRunner,
            ISelfDeploySupervisor supervisor,
            Action? requestProcessExit = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _BuildRunner = buildRunner ?? throw new ArgumentNullException(nameof(buildRunner));
            _Supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
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

            await EmitEventAsync("self_deploy.build_succeeded", selfVessel.Id, mergeEntryId,
                "Release build succeeded; requesting supervised restart",
                new { vesselId = selfVessel.Id, mergeEntryId }, token).ConfigureAwait(false);

            string serverDllPath = Path.GetFullPath(Path.Combine(workingDirectory, settings.ServerDllRelativePath));
            string supervisorScriptPath = ResolveSupervisorScriptPath(workingDirectory, settings);
            int admiralPid = Process.GetCurrentProcess().Id;
            bool spawned = await _Supervisor.RequestSupervisedRestartAsync(
                workingDirectory,
                admiralPid,
                serverDllPath,
                supervisorScriptPath,
                token).ConfigureAwait(false);

            if (!spawned)
            {
                await EmitSkippedAsync(selfVessel.Id, mergeEntryId, "supervisor_spawn_failed", token).ConfigureAwait(false);
                await OpenBuildIncidentAsync(selfVessel, mergeEntryId,
                    "Self-deploy supervisor failed to spawn",
                    "Supervisor script: " + supervisorScriptPath, token).ConfigureAwait(false);
                return false;
            }

            await EmitEventAsync("self_deploy.restart_requested", selfVessel.Id, mergeEntryId,
                "Supervised restart requested for admiral pid " + admiralPid,
                new { vesselId = selfVessel.Id, mergeEntryId, admiralPid, serverDllPath, supervisorScriptPath }, token).ConfigureAwait(false);

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
                    RecoveryNotes = "Inspect WorkingDirectory sync state and Release build output. The running admiral was left online.",
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

        private static string ResolveSupervisorScriptPath(string workingDirectory, SelfDeploySettings settings)
        {
            string? relative = settings.SupervisorScriptRelativePath;
            if (String.IsNullOrWhiteSpace(relative))
            {
                if (OperatingSystem.IsWindows())
                {
                    relative = "scripts/windows/admiral-watchdog.ps1";
                }
                else if (OperatingSystem.IsMacOS())
                {
                    relative = "scripts/macos/admiral-watchdog.sh";
                }
                else
                {
                    relative = "scripts/linux/admiral-watchdog.sh";
                }
            }

            return Path.GetFullPath(Path.Combine(workingDirectory, relative));
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
