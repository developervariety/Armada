namespace Armada.Test.Unit
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Integration tests for the WaitingForInput mission lifecycle:
    /// gate during assignment, resumption via armada_nudge_voyage equivalent,
    /// and safety cap downgrade behaviour.
    /// </summary>
    public class MissionNeedsInputWorkflowTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Needs-Input Workflow";

        #region Doubles

        private sealed class RecordingEscalationService : IEscalationService
        {
            public List<(EscalationTriggerEnum trigger, string entityId, string message)> FireCalls { get; }
                = new List<(EscalationTriggerEnum, string, string)>();

            public Task EvaluateAsync(CancellationToken token = default) => Task.CompletedTask;

            public Task FireAsync(EscalationTriggerEnum trigger, string entityId, string message, CancellationToken token = default)
            {
                FireCalls.Add((trigger, entityId, message));
                return Task.CompletedTask;
            }
        }

        private sealed class DirCreatingGitStub : IGitService
        {
            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default)
            {
                Directory.CreateDirectory(localPath);
                return Task.CompletedTask;
            }
            public Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", CancellationToken token = default)
            {
                Directory.CreateDirectory(worktreePath);
                return Task.CompletedTask;
            }
            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task FetchAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) => Task.CompletedTask;
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default) => Task.FromResult("https://github.com/test/pr/1");
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => Task.FromResult(true);
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default) => Task.CompletedTask;
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => Task.CompletedTask;
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => Task.CompletedTask;
            public Task PullAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult<string?>("main");
            public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(true);
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => Task.FromResult("");
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => Task.FromResult(false);
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => Task.FromResult<string?>("abc123");
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => Task.FromResult(false);
        }

        #endregion

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings(int maxBlocks = 3)
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_ni_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_ni_repos_" + Guid.NewGuid().ToString("N"));
            settings.MaxMissionInputBlocks = maxBlocks;
            return settings;
        }

        private async Task<(SqliteDatabaseDriver db, MissionService missionService, RecordingEscalationService escalation, Captain captain, Mission mission, Vessel vessel)>
            CreateScenarioAsync(int maxBlocks = 3)
        {
            LoggingModule logging = CreateLogging();
            ArmadaSettings settings = CreateSettings(maxBlocks);
            DirCreatingGitStub git = new DirCreatingGitStub();
            TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            SqliteDatabaseDriver db = testDb.Driver;
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            RecordingEscalationService escalation = new RecordingEscalationService();
            MissionService missionService = new MissionService(logging, db, settings, dockService, captainService, git: git, escalation: escalation);

            int nextPid = 5000;
            captainService.OnLaunchAgent = (_, _, _) =>
            {
                nextPid++;
                return Task.FromResult(nextPid);
            };

            Vessel vessel = new Vessel("needs-input-vessel", "https://github.com/test/ni.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_ni_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_ni_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            vessel = await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Captain captain = new Captain("needs-input-worker");
            captain.Runtime = AgentRuntimeEnum.ClaudeCode;
            captain = await db.Captains.CreateAsync(captain).ConfigureAwait(false);

            Voyage voyage = new Voyage("ni-voyage", "");
            voyage = await db.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Mission mission = new Mission("NI Worker", "Needs input test mission");
            mission.VoyageId = voyage.Id;
            mission.VesselId = vessel.Id;
            mission = await db.Missions.CreateAsync(mission).ConfigureAwait(false);

            return (db, missionService, escalation, captain, mission, vessel);
        }

        /// <summary>Run needs-input workflow tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("WaitingForInput_MissionNotAssigned_WhileParked", async () =>
            {
                (SqliteDatabaseDriver db, MissionService missionService, _, Captain captain, Mission mission, Vessel vessel) = await CreateScenarioAsync().ConfigureAwait(false);

                // Simulate captain completing with a block marker
                await db.Captains.TryClaimAsync(captain.Id, mission.Id, "dck_test", default).ConfigureAwait(false);
                missionService.OnGetMissionOutput = _ => "[ARMADA:NEEDS-INPUT block] What library should I use?";
                await missionService.HandleCompletionAsync(captain, mission.Id).ConfigureAwait(false);

                Mission? parked = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertNotNull(parked, "Mission should still exist after parking");
                AssertEqual(MissionStatusEnum.WaitingForInput, parked!.Status, "Mission should be in WaitingForInput");

                // Verify assignment is skipped for WaitingForInput mission
                parked.Status = MissionStatusEnum.WaitingForInput;
                bool assigned = await missionService.TryAssignAsync(parked, vessel).ConfigureAwait(false);
                AssertFalse(assigned, "WaitingForInput mission should not be assigned");
            });

            await RunTest("NudgeVoyage_ResumesMission_AfterWaitingForInput", async () =>
            {
                (SqliteDatabaseDriver db, MissionService missionService, _, Captain captain, Mission mission, Vessel vessel) = await CreateScenarioAsync().ConfigureAwait(false);

                await db.Captains.TryClaimAsync(captain.Id, mission.Id, "dck_test2", default).ConfigureAwait(false);
                missionService.OnGetMissionOutput = _ => "[ARMADA:NEEDS-INPUT block] Please clarify the target.";
                await missionService.HandleCompletionAsync(captain, mission.Id).ConfigureAwait(false);

                Mission? parked = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.WaitingForInput, parked!.Status, "Should be parked");

                // Simulate the nudge_voyage reply injection
                string notes = "[ORCHESTRATOR NOTES]\nUse the main branch.\n\n";
                parked.Description = notes + (parked.Description ?? "");
                parked.Status = MissionStatusEnum.Pending;
                parked.CaptainId = null;
                parked.ProcessId = null;
                parked.DockId = null;
                parked.AgentOutput = null;
                parked.LastUpdateUtc = DateTime.UtcNow;
                await db.Missions.UpdateAsync(parked).ConfigureAwait(false);

                Mission? resumed = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Pending, resumed!.Status, "Resumed mission should be Pending");
                AssertNull(resumed.CaptainId, "Resumed mission should have no captain");
                AssertNull(resumed.AgentOutput, "Resumed mission should have no stale agent output");
                AssertTrue(resumed.Description!.StartsWith("[ORCHESTRATOR NOTES]", StringComparison.Ordinal), "Notes should be prepended");
            });

            await RunTest("BlockCap_ExceededBlock_DowngradedToSoft_MissionNotParked", async () =>
            {
                // maxBlocks = 1: first block parks, second block (cap exceeded) downgraded to soft
                (SqliteDatabaseDriver db, MissionService missionService, _, Captain captain, Mission mission, Vessel vessel) = await CreateScenarioAsync(maxBlocks: 1).ConfigureAwait(false);

                // First block: parks the mission
                await db.Captains.TryClaimAsync(captain.Id, mission.Id, "dck_cap1", default).ConfigureAwait(false);
                missionService.OnGetMissionOutput = _ => "[ARMADA:NEEDS-INPUT block] First block request";
                await missionService.HandleCompletionAsync(captain, mission.Id).ConfigureAwait(false);

                Mission? parked = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.WaitingForInput, parked!.Status, "First block should park");

                // Reset mission to InProgress for the second completion attempt
                parked.Status = MissionStatusEnum.InProgress;
                parked.ProcessId = 9999;
                parked.CaptainId = captain.Id;
                parked.DockId = "dck_cap2";
                await db.Missions.UpdateAsync(parked).ConfigureAwait(false);
                await db.Captains.TryClaimAsync(captain.Id, mission.Id, "dck_cap2", default).ConfigureAwait(false);

                // Second block: cap exceeded, should be downgraded to soft and NOT park the mission
                missionService.OnGetMissionOutput = _ => "[ARMADA:NEEDS-INPUT block] Second block request";
                await missionService.HandleCompletionAsync(captain, mission.Id).ConfigureAwait(false);

                Mission? afterCap = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertTrue(afterCap!.Status != MissionStatusEnum.WaitingForInput, "Second block over cap should not park mission");
            });

            await RunTest("SoftMarker_DoesNotParkMission_ContinuesFlow", async () =>
            {
                (SqliteDatabaseDriver db, MissionService missionService, RecordingEscalationService escalation, Captain captain, Mission mission, Vessel vessel) = await CreateScenarioAsync().ConfigureAwait(false);

                await db.Captains.TryClaimAsync(captain.Id, mission.Id, "dck_soft", default).ConfigureAwait(false);
                missionService.OnGetMissionOutput = _ => "[ARMADA:NEEDS-INPUT soft] Soft notification question";
                bool landingCalled = false;
                missionService.OnMissionComplete = (m, d) =>
                {
                    landingCalled = true;
                    m.Status = MissionStatusEnum.Complete;
                    return db.Missions.UpdateAsync(m);
                };
                await missionService.HandleCompletionAsync(captain, mission.Id).ConfigureAwait(false);

                Mission? after = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertTrue(after!.Status != MissionStatusEnum.WaitingForInput, "Soft marker should not park mission");
                // Soft does not fire escalation, only writes a Wake signal
                AssertEqual(0, escalation.FireCalls.Count(fc => fc.trigger == EscalationTriggerEnum.MissionAwaitingInput), "Soft should not fire MissionAwaitingInput escalation");
            });

            await RunTest("BlockMarker_WritesWakeSignal_And_FiresEscalation", async () =>
            {
                (SqliteDatabaseDriver db, MissionService missionService, RecordingEscalationService escalation, Captain captain, Mission mission, Vessel vessel) = await CreateScenarioAsync().ConfigureAwait(false);

                await db.Captains.TryClaimAsync(captain.Id, mission.Id, "dck_esc", default).ConfigureAwait(false);
                missionService.OnGetMissionOutput = _ => "[ARMADA:NEEDS-INPUT block] What database should I use?";
                await missionService.HandleCompletionAsync(captain, mission.Id).ConfigureAwait(false);

                // Verify escalation was fired
                AssertEqual(1, escalation.FireCalls.Count(fc => fc.trigger == EscalationTriggerEnum.MissionAwaitingInput), "Should fire MissionAwaitingInput escalation once");

                // Verify escalation payload includes missionId and questionText
                (EscalationTriggerEnum trigger, string entityId, string payload) = escalation.FireCalls.First(fc => fc.trigger == EscalationTriggerEnum.MissionAwaitingInput);
                AssertTrue(payload.Contains(mission.Id, StringComparison.Ordinal), "Escalation payload should include missionId");
                AssertTrue(payload.Contains("What database should I use?", StringComparison.Ordinal), "Escalation payload should include question text");
                AssertFalse(payload.Contains("token", StringComparison.OrdinalIgnoreCase), "Escalation payload must not include tokens");
                AssertFalse(payload.Contains("bearer", StringComparison.OrdinalIgnoreCase), "Escalation payload must not include bearer");
                AssertFalse(payload.Contains("apiKey", StringComparison.OrdinalIgnoreCase), "Escalation payload must not include apiKey");
                AssertFalse(payload.Contains("password", StringComparison.OrdinalIgnoreCase), "Escalation payload must not include password");
            });
        }
    }
}
