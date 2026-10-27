namespace Armada.Core.Services
{
    using System.Text;
    using System.Text.Json;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Stage checks: verifying a stage's work in its dock, committing leftover work, and dock host helpers.
    /// </summary>
    public partial class MissionService
    {
        #region Private-Methods

        /// <inheritdoc />
        public async Task<string?> VerifyStageWorkAsync(Mission mission, Captain captain, CancellationToken token = default)
        {
            StageCheckResult result = await CheckStageWorkAsync(mission, captain, token).ConfigureAwait(false);
            return result.FailureMessage;
        }

        /// <inheritdoc />
        public async Task<StageCheckResult> CheckStageWorkAsync(Mission mission, Captain captain, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            DateTime started = DateTime.UtcNow;
            StageCheckResult result = await RunStageCheckAsync(mission, captain, token).ConfigureAwait(false);
            result.CheckedUtc = DateTime.UtcNow;
            result.DurationMs = (long)(result.CheckedUtc - started).TotalMilliseconds;
            return result;
        }

        private async Task<StageCheckResult> RunStageCheckAsync(Mission mission, Captain captain, CancellationToken token)
        {
            bool checkedRuntime = captain.Runtime == AgentRuntimeEnum.Mux || captain.Runtime == AgentRuntimeEnum.ApiEndpoint;
            if (!checkedRuntime && !_Settings.VerifyAllStageRuntimes) return StageCheckResult.Skip(StageCheckReasonEnum.RuntimeNotChecked);
            if (PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Judge) || PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Architect)
                || PersonaCatalog.Matches(mission.Persona, PersonaCatalog.ProductManager) || PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Recorder))
                return StageCheckResult.Skip(StageCheckReasonEnum.ReviewPersona);
            if (String.IsNullOrEmpty(mission.DockId) || String.IsNullOrEmpty(mission.VesselId)) return StageCheckResult.Skip(StageCheckReasonEnum.NoDock);
            // A verifier that reported a blocking product defect is not checked: a failing check is its evidence, and its
            // outcome goes to the Worker as rework rather than back to the verifier to "fix" (HF-25).
            if (VerifierContract.Applies(mission.Persona) && OnPeekMissionOutput != null && VerifierContract.IsBlocked(OnPeekMissionOutput(mission.Id)))
            {
                _Logging.Info(_Header + mission.Persona + " mission " + mission.Id + " reported a blocking defect; skipping the stage check");
                return StageCheckResult.Skip(StageCheckReasonEnum.VerifierReportedBlocker);
            }

            StageCheckResult result = new StageCheckResult();
            try
            {
                Dock? dock = await _Database.Docks.ReadAsync(mission.DockId!, token).ConfigureAwait(false);
                Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId!, token).ConfigureAwait(false);
                if (dock == null || vessel == null || String.IsNullOrEmpty(dock.WorktreePath)) return StageCheckResult.Skip(StageCheckReasonEnum.NoDock);

                // A Harbor whose link is down for a moment (an Admiral restart, a network blip) is waited for: the check
                // not being able to run is an infrastructure condition, not something to send the captain back over.
                if (!await WaitForDockHarborAsync(dock, token).ConfigureAwait(false))
                {
                    result.Status = StageCheckStatusEnum.CouldNotRun;
                    result.Reason = StageCheckReasonEnum.HarborDisconnected;
                    result.FailureMessage = "The stage check could not run because Harbor " + dock.HarborId + " did not reconnect within "
                        + (int)HarborReconnectWait.TotalSeconds + " seconds. Its success has not been verified; rerun the configured checks once the Harbor is back.";
                    return result;
                }

                AuthContext system = new AuthContext { IsAuthenticated = true, IsAdmin = true, TenantId = vessel.TenantId };
                WorkflowProfile? profile = await new WorkflowProfileService(_Database, _Logging).ResolveForVesselAsync(system, vessel, null, token).ConfigureAwait(false);
                if (profile == null) return StageCheckResult.Skip(StageCheckReasonEnum.NoWorkflowProfile);
                List<string> commands = new List<string>();
                if (!String.IsNullOrWhiteSpace(profile.BuildCommand)) commands.Add(profile.BuildCommand!.Trim());
                if (!String.IsNullOrWhiteSpace(profile.UnitTestCommand)) commands.Add(profile.UnitTestCommand!.Trim());
                if (commands.Count == 0) return StageCheckResult.Skip(StageCheckReasonEnum.NoCommands);
                result.Commands = commands;

                // A stage that committed nothing has nothing new to verify (a frontier stage that only reviewed). Local
                // models are still checked either way, because they often leave edits uncommitted.
                string? startCommit = TryReadDockStartCommit(dock.Id);
                if (!checkedRuntime)
                {
                    string? head = await ReadDockHeadAsync(dock, token).ConfigureAwait(false);
                    if (String.IsNullOrEmpty(startCommit) || String.IsNullOrEmpty(head) || String.Equals(startCommit, head, StringComparison.OrdinalIgnoreCase))
                    {
                        StageCheckResult skipped = StageCheckResult.Skip(StageCheckReasonEnum.NoNewCommit);
                        skipped.Commands = commands;
                        return skipped;
                    }
                }

                DockHost? harborHost = HarborHostFor(dock);
                IHostCommandExecutor executor = harborHost != null ? harborHost.Commands : _LocalCommands;
                bool windows = OperatingSystem.IsWindows();
                if (harborHost != null && !String.IsNullOrEmpty(dock.HarborId))
                {
                    Harbor? harbor = await _Database.Harbors.ReadAsync(dock.HarborId!, token).ConfigureAwait(false);
                    windows = FleetActionShellCommandBuilder.IsWindowsPlatform(harbor?.OsPlatform);
                }

                HashSet<string> dirtyBefore = await ReadModifiedTrackedFilesAsync(executor, windows, dock.WorktreePath!, token).ConfigureAwait(false);
                try
                {
                    foreach (string command in commands)
                    {
                        HostCommandResult commandResult = await RunStageCommandAsync(executor, windows, dock.WorktreePath!, command, token).ConfigureAwait(false);
                        if (commandResult.TimedOut)
                        {
                            _Logging.Warn(_Header + "stage check for mission " + mission.Id + " timed out running " + command);
                            result.Status = StageCheckStatusEnum.CouldNotRun;
                            result.Reason = StageCheckReasonEnum.TimedOut;
                            result.FailedCommand = command;
                            result.FailureMessage = "The stage check timed out after " + _Settings.StageCheckTimeoutMinutes + " minutes: " + command + ". Its success has not been verified. "
                                + "Investigate and rerun the check. If the command is simply slower than that, an operator can raise stageCheckTimeoutMinutes or give the vessel's workflow profile a narrower test command.";
                            return result;
                        }

                        if (commandResult.ExitCode != 0)
                        {
                            // A check that fails on the stage's starting commit as well is a pre-existing failure, not
                            // something this stage broke: report it in the log and do not send the captain back over it.
                            if (!String.IsNullOrEmpty(startCommit)
                                && await FailsOnBaseCommitAsync(executor, windows, vessel.Id, dock.WorktreePath!, startCommit!, command, token).ConfigureAwait(false))
                            {
                                _Logging.Warn(_Header + "stage check for mission " + mission.Id + " fails on its base commit " + startCommit + " too (" + command + "); not blocking the stage");
                                result.PreExistingFailures.Add(command);
                                continue;
                            }

                            string output = ((commandResult.StandardOutput ?? "") + "\n" + (commandResult.StandardError ?? "")).Trim();
                            if (output.Length > 3000) output = "...\n" + output.Substring(output.Length - 3000);
                            _Logging.Warn(_Header + "stage check for mission " + mission.Id + " failed: " + command + " exited " + commandResult.ExitCode);
                            result.Status = StageCheckStatusEnum.Failed;
                            result.FailedCommand = command;
                            result.ExitCode = commandResult.ExitCode;
                            result.FailureMessage = "The stage check failed: `" + command + "` exited with code " + commandResult.ExitCode + " in the working tree. Its output:\n```\n" + output + "\n```\nFix the cause, run the command again until it succeeds, and commit.";
                            return result;
                        }
                    }
                }
                finally
                {
                    // The check's own build can rewrite tracked files (generated XML docs, lock files). Put those back so
                    // the check never adds changes the stage did not make.
                    await RevertCheckArtifactsAsync(executor, windows, dock.WorktreePath!, dirtyBefore, mission.Id).ConfigureAwait(false);
                }

                _Logging.Info(_Header + "stage check for mission " + mission.Id + " passed (" + String.Join("; ", commands) + ")");
                result.Status = StageCheckStatusEnum.Passed;
                return result;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is TimeoutException)
            {
                _Logging.Warn(_Header + "stage check for mission " + mission.Id + " could not run: " + ex.Message);
                result.Status = StageCheckStatusEnum.CouldNotRun;
                result.Reason = StageCheckReasonEnum.Error;
                result.FailureMessage = "The stage verification could not run: " + ex.Message + ". Restore dependencies and rerun the configured checks.";
                return result;
            }
        }

        /// <inheritdoc />
        public Task CommitDockWorkAsync(Mission mission, Captain captain, Dock dock, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (dock == null) throw new ArgumentNullException(nameof(dock));
            return CommitLeftoverWorkAsync(mission, captain, dock, token);
        }

        private async Task CommitLeftoverWorkAsync(Mission mission, Captain captain, Dock dock, CancellationToken token)
        {
            // A judge reports through its verdict and an architect through its plan: neither should change the branch.
            if (PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Judge) || PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Architect)) return;
            if (String.IsNullOrEmpty(dock.WorktreePath)) return;

            DockHost? harborHost = HarborHostFor(dock);
            IGitService? git = harborHost != null ? harborHost.Git : _Git;
            if (git == null) return;

            try
            {
                if (!await DockDirectoryExistsAsync(harborHost, dock.WorktreePath!, token).ConfigureAwait(false)) return;

                // The rescue is for a captain that never commits. A captain that committed its work this attempt has said
                // what belongs on the branch; what it left behind is typically build output (regenerated XML docs, lock
                // files), and committing that breaks the mission's file scope and sends the work back to rework.
                string? startCommit = TryReadDockStartCommit(dock.Id);
                string? headCommit = await git.GetHeadCommitHashAsync(dock.WorktreePath!, token).ConfigureAwait(false);
                if (!String.IsNullOrEmpty(startCommit) && !String.IsNullOrEmpty(headCommit)
                    && !String.Equals(startCommit, headCommit, StringComparison.OrdinalIgnoreCase))
                {
                    _Logging.Info(_Header + "mission " + mission.Id + ": captain " + captain.Id + " committed its work; leaving any uncommitted changes in dock " + dock.Id + " (typically build output) off the branch");
                    return;
                }

                string message = "Armada: commit work left uncommitted by " + captain.Name + "\n\n"
                    + mission.Title + "\n\nMission " + mission.Id + (String.IsNullOrEmpty(mission.Persona) ? "" : " (" + mission.Persona + ")")
                    + ". The captain finished without committing these changes, so Armada committed them.";
                if (await git.CommitAllAsync(dock.WorktreePath!, message, token).ConfigureAwait(false))
                {
                    _LeftoverCommitted[mission.Id] = 0;
                    _Logging.Info(_Header + "mission " + mission.Id + ": captain " + captain.Id + " left uncommitted changes in dock " + dock.Id + "; committed them");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is TimeoutException || ex is IOException)
            {
                _Logging.Warn(_Header + "mission " + mission.Id + ": could not commit leftover changes in dock " + dock.Id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// How long a stage check waits for a dock's disconnected Harbor to come back before giving up.
        /// </summary>
        public TimeSpan HarborReconnectWait { get; set; } = TimeSpan.FromMinutes(3);

        private async Task<bool> WaitForDockHarborAsync(Dock dock, CancellationToken token)
        {
            if (DockHosts?.Harbors == null || !DockHostResolver.IsHarborDock(dock)) return true;
            DateTime deadline = DateTime.UtcNow + HarborReconnectWait;
            bool logged = false;
            while (!DockHosts.Harbors.IsConnected(dock.HarborId!))
            {
                if (DateTime.UtcNow >= deadline) return false;
                if (!logged)
                {
                    _Logging.Info(_Header + "waiting for Harbor " + dock.HarborId + " to reconnect before checking dock " + dock.Id);
                    logged = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
            return true;
        }

        private DockHost? HarborHostFor(Dock? dock)
        {
            if (DockHosts == null || !DockHostResolver.IsHarborDock(dock)) return null;
            return DockHosts.ForDock(dock);
        }

        /// <summary>
        /// Whether a dock's worktree folder exists on its host. A Harbor that is offline or does not answer counts as
        /// "not there", so callers fall back to what the Admiral already has (the diff snapshot).
        /// </summary>
        private async Task<bool> DockDirectoryExistsAsync(DockHost? harborHost, string path, CancellationToken token)
        {
            if (harborHost == null) return Directory.Exists(path);
            try
            {
                return await harborHost.Files.DirectoryExistsAsync(path, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is TimeoutException || ex is IOException)
            {
                _Logging.Debug(_Header + "could not check dock " + path + " on Harbor " + harborHost.HarborId + ": " + ex.Message);
                return false;
            }
        }

        private async Task HideTrackedInstructionsFileAsync(IHostCommandExecutor commands, string worktreePath, string instructionsFileName, CancellationToken token)
        {
            // git ignores exclude rules for files it already tracks. When the repository commits its own
            // instruction file (for example CLAUDE.md), mark it skip-worktree in this dock so the generated
            // mission instructions can never be staged or committed by the captain.
            // ls-files --error-unmatch exits 1 for a file git does not track: an answer, not a failure.
            int tracked = await RunGitExitCodeAsync(commands, worktreePath, new int[] { 1 }, token, "ls-files", "--error-unmatch", "--", instructionsFileName).ConfigureAwait(false);
            if (tracked != 0) return;

            int marked = await RunGitExitCodeAsync(commands, worktreePath, null, token, "update-index", "--skip-worktree", "--", instructionsFileName).ConfigureAwait(false);
            if (marked != 0)
            {
                _Logging.Warn(_Header + "could not mark " + instructionsFileName + " skip-worktree in " + worktreePath + " (git exit code " + marked + ")");
            }
        }

        private static async Task<int> RunGitExitCodeAsync(IHostCommandExecutor commands, string workingDirectory, int[]? expectedExitCodes, CancellationToken token, params string[] args)
        {
            HostCommandResult result = await commands.RunAsync(new HostCommandRequest
            {
                Executable = "git",
                WorkingDirectory = workingDirectory,
                Arguments = new List<string>(args),
                TimeoutMs = 30000,
                ExpectedExitCodes = expectedExitCodes != null ? new List<int>(expectedExitCodes) : new List<int>()
            }, token).ConfigureAwait(false);
            return result.ExitCode;
        }

        #endregion
    }
}
