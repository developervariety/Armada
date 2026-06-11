namespace Armada.Test.Unit
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
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
    /// Tests for the needs-input Wake signal payload contents, the hard-block cap
    /// downgrade path (exercised across separate MissionService instances so the
    /// per-instance in-flight completion dedup does not short-circuit re-completions),
    /// malformed-marker handling, question sanitization, and captain release on park.
    /// </summary>
    public class MissionNeedsInputSignalAndCapTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Needs-Input Signals and Block Cap";

        #region Doubles

        private sealed class RecordedEscalation
        {
            public EscalationTriggerEnum Trigger { get; set; }
            public string EntityId { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
        }

        private sealed class RecordingEscalationService : IEscalationService
        {
            public List<RecordedEscalation> FireCalls { get; } = new List<RecordedEscalation>();

            public Task EvaluateAsync(CancellationToken token = default) => Task.CompletedTask;

            public Task FireAsync(EscalationTriggerEnum trigger, string entityId, string message, CancellationToken token = default)
            {
                FireCalls.Add(new RecordedEscalation { Trigger = trigger, EntityId = entityId, Message = message });
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

        private sealed class NeedsInputWakePayload
        {
            public string? Trigger { get; set; }
            public string? VoyageId { get; set; }
            public string? MissionId { get; set; }
            public string? Mode { get; set; }
            public string? QuestionText { get; set; }
        }

        private sealed class NeedsInputScenario
        {
            public SqliteDatabaseDriver Db { get; set; } = null!;
            public LoggingModule Logging { get; set; } = null!;
            public ArmadaSettings Settings { get; set; } = null!;
            public DirCreatingGitStub Git { get; set; } = null!;
            public IDockService Docks { get; set; } = null!;
            public CaptainService Captains { get; set; } = null!;
            public RecordingEscalationService Escalation { get; set; } = null!;
            public Captain Captain { get; set; } = null!;
            public Mission Mission { get; set; } = null!;
            public Vessel Vessel { get; set; } = null!;

            public MissionService CreateMissionService(string agentOutput)
            {
                MissionService missionService = new MissionService(Logging, Db, Settings, Docks, Captains, git: Git, escalation: Escalation);
                missionService.OnGetMissionOutput = _ => agentOutput;
                return missionService;
            }
        }

        #endregion

        private static readonly JsonSerializerOptions _PayloadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private async Task<NeedsInputScenario> CreateScenarioAsync(int maxBlocks = 3)
        {
            LoggingModule logging = CreateLogging();
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_nisc_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_nisc_repos_" + Guid.NewGuid().ToString("N"));
            settings.MaxMissionInputBlocks = maxBlocks;

            DirCreatingGitStub git = new DirCreatingGitStub();
            TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            SqliteDatabaseDriver db = testDb.Driver;
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            RecordingEscalationService escalation = new RecordingEscalationService();

            Vessel vessel = new Vessel("needs-input-cap-vessel", "https://github.com/test/nicap.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_nisc_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_nisc_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            vessel = await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Captain captain = new Captain("needs-input-cap-worker");
            captain.Runtime = AgentRuntimeEnum.ClaudeCode;
            captain = await db.Captains.CreateAsync(captain).ConfigureAwait(false);

            Voyage voyage = new Voyage("nisc-voyage", "");
            voyage = await db.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Mission mission = new Mission("NISC Worker", "Needs input signal/cap test mission");
            mission.VoyageId = voyage.Id;
            mission.VesselId = vessel.Id;
            mission = await db.Missions.CreateAsync(mission).ConfigureAwait(false);

            NeedsInputScenario scenario = new NeedsInputScenario();
            scenario.Db = db;
            scenario.Logging = logging;
            scenario.Settings = settings;
            scenario.Git = git;
            scenario.Docks = dockService;
            scenario.Captains = captainService;
            scenario.Escalation = escalation;
            scenario.Captain = captain;
            scenario.Mission = mission;
            scenario.Vessel = vessel;
            return scenario;
        }

        private static async Task<List<Signal>> GetNeedsInputWakeSignalsAsync(SqliteDatabaseDriver db, string missionId)
        {
            List<Signal> recent = await db.Signals.EnumerateRecentAsync(200).ConfigureAwait(false);
            List<Signal> matches = new List<Signal>();
            foreach (Signal signal in recent)
            {
                if (signal.Type != SignalTypeEnum.Wake) continue;
                if (String.IsNullOrEmpty(signal.Payload)) continue;
                if (!signal.Payload.Contains("needs_input_", StringComparison.Ordinal)) continue;
                if (!signal.Payload.Contains(missionId, StringComparison.Ordinal)) continue;
                matches.Add(signal);
            }
            return matches;
        }

        private static async Task ResetMissionForReRunAsync(SqliteDatabaseDriver db, Mission mission, Captain captain, string dockId)
        {
            Mission? current = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
            current!.Status = MissionStatusEnum.InProgress;
            current.ProcessId = 4242;
            current.CaptainId = captain.Id;
            current.DockId = dockId;
            await db.Missions.UpdateAsync(current).ConfigureAwait(false);
            await db.Captains.TryClaimAsync(captain.Id, mission.Id, dockId, default).ConfigureAwait(false);
        }

        /// <summary>Run needs-input signal and cap tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("BlockMarker_WakeSignal_HasStructuredSafePayload", async () =>
            {
                NeedsInputScenario scenario = await CreateScenarioAsync().ConfigureAwait(false);

                await scenario.Db.Captains.TryClaimAsync(scenario.Captain.Id, scenario.Mission.Id, "dck_sig1", default).ConfigureAwait(false);
                MissionService missionService = scenario.CreateMissionService("[ARMADA:NEEDS-INPUT block] Which schema version applies?");
                await missionService.HandleCompletionAsync(scenario.Captain, scenario.Mission.Id).ConfigureAwait(false);

                List<Signal> signals = await GetNeedsInputWakeSignalsAsync(scenario.Db, scenario.Mission.Id).ConfigureAwait(false);
                AssertEqual(1, signals.Count, "Exactly one needs-input Wake signal should be written");

                NeedsInputWakePayload? payload = JsonSerializer.Deserialize<NeedsInputWakePayload>(signals[0].Payload!, _PayloadOptions);
                AssertNotNull(payload, "Wake payload should deserialize into the structured shape");
                AssertEqual("needs_input_block", payload!.Trigger, "Trigger should be needs_input_block");
                AssertEqual(scenario.Mission.VoyageId, payload.VoyageId, "Payload voyageId should match the mission's voyage");
                AssertEqual(scenario.Mission.Id, payload.MissionId, "Payload missionId should match the mission");
                AssertEqual("block", payload.Mode, "Payload mode should be block");
                AssertEqual("Which schema version applies?", payload.QuestionText, "Payload questionText should carry the question");

                AssertFalse(signals[0].Payload!.Contains("bearer", StringComparison.OrdinalIgnoreCase), "Wake payload must not include bearer");
                AssertFalse(signals[0].Payload!.Contains("apiKey", StringComparison.OrdinalIgnoreCase), "Wake payload must not include apiKey");
                AssertFalse(signals[0].Payload!.Contains("password", StringComparison.OrdinalIgnoreCase), "Wake payload must not include password");
                AssertFalse(signals[0].Payload!.Contains("environment", StringComparison.OrdinalIgnoreCase), "Wake payload must not include environment");
            });

            await RunTest("BlockCap_NthBlockParks_NPlusFirstDowngradedToSoft", async () =>
            {
                // MaxMissionInputBlocks = 2: blocks 1 and 2 park, block 3 is downgraded to soft.
                // Each completion uses a FRESH MissionService instance because the in-flight
                // completion dedup is per-instance and holds entries for 30 seconds, which
                // would silently skip repeat completions on a shared instance.
                NeedsInputScenario scenario = await CreateScenarioAsync(maxBlocks: 2).ConfigureAwait(false);

                await scenario.Db.Captains.TryClaimAsync(scenario.Captain.Id, scenario.Mission.Id, "dck_cap_a", default).ConfigureAwait(false);
                MissionService first = scenario.CreateMissionService("[ARMADA:NEEDS-INPUT block] First question");
                await first.HandleCompletionAsync(scenario.Captain, scenario.Mission.Id).ConfigureAwait(false);

                Mission? afterFirst = await scenario.Db.Missions.ReadAsync(scenario.Mission.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.WaitingForInput, afterFirst!.Status, "First block should park the mission");

                await ResetMissionForReRunAsync(scenario.Db, scenario.Mission, scenario.Captain, "dck_cap_b").ConfigureAwait(false);
                MissionService second = scenario.CreateMissionService("[ARMADA:NEEDS-INPUT block] Second question");
                await second.HandleCompletionAsync(scenario.Captain, scenario.Mission.Id).ConfigureAwait(false);

                Mission? afterSecond = await scenario.Db.Missions.ReadAsync(scenario.Mission.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.WaitingForInput, afterSecond!.Status, "Second block (Nth, under cap) should still park the mission");

                await ResetMissionForReRunAsync(scenario.Db, scenario.Mission, scenario.Captain, "dck_cap_c").ConfigureAwait(false);
                MissionService third = scenario.CreateMissionService("[ARMADA:NEEDS-INPUT block] Third question");
                await third.HandleCompletionAsync(scenario.Captain, scenario.Mission.Id).ConfigureAwait(false);

                Mission? afterThird = await scenario.Db.Missions.ReadAsync(scenario.Mission.Id).ConfigureAwait(false);
                AssertTrue(afterThird!.Status != MissionStatusEnum.WaitingForInput, "Third block (N+1th, over cap) must not park the mission");

                List<Signal> signals = await GetNeedsInputWakeSignalsAsync(scenario.Db, scenario.Mission.Id).ConfigureAwait(false);
                AssertEqual(3, signals.Count, "All three completions should write a needs-input Wake signal");

                int blockCount = signals.Count(s => s.Payload!.Contains("needs_input_block", StringComparison.Ordinal));
                int softCount = signals.Count(s => s.Payload!.Contains("needs_input_soft", StringComparison.Ordinal));
                AssertEqual(2, blockCount, "Two Wake signals should record block mode");
                AssertEqual(1, softCount, "The over-cap block should be recorded as a downgraded soft Wake signal");

                int escalations = scenario.Escalation.FireCalls.Count(fc => fc.Trigger == EscalationTriggerEnum.MissionAwaitingInput);
                AssertEqual(2, escalations, "Only the two parking blocks should fire MissionAwaitingInput escalations");
            });

            await RunTest("MalformedMarker_DoesNotPark_NoNeedsInputSignal", async () =>
            {
                NeedsInputScenario scenario = await CreateScenarioAsync().ConfigureAwait(false);

                await scenario.Db.Captains.TryClaimAsync(scenario.Captain.Id, scenario.Mission.Id, "dck_mal", default).ConfigureAwait(false);
                MissionService missionService = scenario.CreateMissionService("[ARMADA:NEEDS-INPUT urgently] Please advise");
                await missionService.HandleCompletionAsync(scenario.Captain, scenario.Mission.Id).ConfigureAwait(false);

                Mission? after = await scenario.Db.Missions.ReadAsync(scenario.Mission.Id).ConfigureAwait(false);
                AssertTrue(after!.Status != MissionStatusEnum.WaitingForInput, "Malformed marker must not park the mission");

                List<Signal> signals = await GetNeedsInputWakeSignalsAsync(scenario.Db, scenario.Mission.Id).ConfigureAwait(false);
                AssertEqual(0, signals.Count, "Malformed marker must not write a needs-input Wake signal");
                AssertEqual(0, scenario.Escalation.FireCalls.Count, "Malformed marker must not fire any escalation");
            });

            await RunTest("BlockQuestionText_TruncatedTo2000Chars_InWakePayload", async () =>
            {
                NeedsInputScenario scenario = await CreateScenarioAsync().ConfigureAwait(false);

                string longQuestion = new string('q', 2500);
                await scenario.Db.Captains.TryClaimAsync(scenario.Captain.Id, scenario.Mission.Id, "dck_long", default).ConfigureAwait(false);
                MissionService missionService = scenario.CreateMissionService("[ARMADA:NEEDS-INPUT block] " + longQuestion);
                await missionService.HandleCompletionAsync(scenario.Captain, scenario.Mission.Id).ConfigureAwait(false);

                List<Signal> signals = await GetNeedsInputWakeSignalsAsync(scenario.Db, scenario.Mission.Id).ConfigureAwait(false);
                AssertEqual(1, signals.Count, "Block with long question should still write one Wake signal");

                NeedsInputWakePayload? payload = JsonSerializer.Deserialize<NeedsInputWakePayload>(signals[0].Payload!, _PayloadOptions);
                AssertNotNull(payload, "Wake payload should deserialize");
                AssertEqual(2000, payload!.QuestionText!.Length, "Question text should be truncated to 2000 characters");
            });

            await RunTest("BlockMarker_ReleasesCaptain_AndClearsMissionProcessState", async () =>
            {
                NeedsInputScenario scenario = await CreateScenarioAsync().ConfigureAwait(false);

                await scenario.Db.Captains.TryClaimAsync(scenario.Captain.Id, scenario.Mission.Id, "dck_rel", default).ConfigureAwait(false);
                MissionService missionService = scenario.CreateMissionService("[ARMADA:NEEDS-INPUT block] Need a decision");
                await missionService.HandleCompletionAsync(scenario.Captain, scenario.Mission.Id).ConfigureAwait(false);

                Mission? parked = await scenario.Db.Missions.ReadAsync(scenario.Mission.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.WaitingForInput, parked!.Status, "Mission should be parked");
                AssertNull(parked.ProcessId, "Parked mission should have no process id");
                AssertNull(parked.DockId, "Parked mission should have no dock id");
                AssertNull(parked.CaptainId, "Parked mission should have no captain id");

                Captain? released = await scenario.Db.Captains.ReadAsync(scenario.Captain.Id).ConfigureAwait(false);
                AssertNotNull(released, "Captain row should still exist");
                AssertNull(released!.CurrentMissionId, "Captain should be released from the parked mission");
            });
        }
    }
}
