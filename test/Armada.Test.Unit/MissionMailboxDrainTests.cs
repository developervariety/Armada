namespace Armada.Test.Unit
{
    using System.Text.Json;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for mailbox signal drain at TryHandoffToNextStageAsync: voyage-level and
    /// mission-level signals are injected at the top of the next brief and then marked read.
    /// </summary>
    public sealed class MissionMailboxDrainTests : TestSuite
    {
        private static readonly JsonSerializerOptions _PayloadOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Suite name.</summary>
        public override string Name => "MissionMailboxDrain";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_drain_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_drain_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private string BuildVoyageSignalPayload(string voyageId, string message) =>
            JsonSerializer.Serialize(new VoyageMailboxSignalPayload { VoyageId = voyageId, Message = message }, _PayloadOpts);

        private string BuildMissionSignalPayload(string missionId, string voyageId, string message) =>
            JsonSerializer.Serialize(new VoyageMailboxSignalPayload { MissionId = missionId, VoyageId = voyageId, Message = message }, _PayloadOpts);

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = haystack.IndexOf(needle, StringComparison.Ordinal);
            while (index >= 0)
            {
                count++;
                index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
            }
            return count;
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("VoyageLevel_UnreadNudge_PrependedToNextBrief", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git);

                    Vessel vessel = new Vessel("drain-voyage-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_drain_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_drain_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("drain-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("drain-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission worker = new Mission("[Worker] Implement", "worker description");
                    worker.VesselId = vessel.Id;
                    worker.VoyageId = voyage.Id;
                    worker.CaptainId = captain.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.InProgress;
                    worker.BranchName = "armada/drain-captain/msn_worker";
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Review", "judge description");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.Pending;
                    judge.DependsOnMissionId = worker.Id;
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    captain.CurrentMissionId = worker.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    // Create unread voyage-level Nudge signal
                    Signal nudge = new Signal(SignalTypeEnum.Nudge, BuildVoyageSignalPayload(voyage.Id, "please check edge cases"));
                    nudge.TenantId = Armada.Core.Constants.DefaultTenantId;
                    nudge.Read = false;
                    nudge = await testDb.Driver.Signals.CreateAsync(nudge).ConfigureAwait(false);

                    await missionService.HandleCompletionAsync(captain, worker.Id).ConfigureAwait(false);

                    Mission? updatedJudge = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertNotNull(updatedJudge, "Judge mission should still exist");
                    AssertNotNull(updatedJudge!.Description, "Judge description should be set after handoff");
                    AssertTrue(updatedJudge.Description!.StartsWith("[ORCHESTRATOR NOTES]"),
                        "Notes block should be at the absolute top of the judge brief");
                    AssertContains("please check edge cases", updatedJudge.Description);
                    AssertContains("[/ORCHESTRATOR NOTES]", updatedJudge.Description);

                    // Signal must be marked read after drain
                    Signal? readSignal = await testDb.Driver.Signals.ReadAsync(nudge.Id).ConfigureAwait(false);
                    AssertNotNull(readSignal);
                    AssertTrue(readSignal!.Read, "Nudge signal should be marked read after drain");
                }
            });

            await RunTest("MissionLevel_UnreadMail_PrependedOnlyToTargetMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git);

                    Vessel vessel = new Vessel("drain-mission-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_drain_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_drain_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("drain-msn-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("drain-msn-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission worker = new Mission("[Worker] Implement", "worker description");
                    worker.VesselId = vessel.Id;
                    worker.VoyageId = voyage.Id;
                    worker.CaptainId = captain.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.InProgress;
                    worker.BranchName = "armada/drain-msn-captain/msn_worker";
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Review", "judge description");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.Pending;
                    judge.DependsOnMissionId = worker.Id;
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    Mission testEngineer = new Mission("[Test Engineer] Tests", "te description");
                    testEngineer.VesselId = vessel.Id;
                    testEngineer.VoyageId = voyage.Id;
                    testEngineer.Persona = "TestEngineer";
                    testEngineer.Status = MissionStatusEnum.Pending;
                    testEngineer.DependsOnMissionId = worker.Id;
                    testEngineer = await testDb.Driver.Missions.CreateAsync(testEngineer).ConfigureAwait(false);

                    captain.CurrentMissionId = worker.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    // Mail signal targeting judge specifically (not testEngineer)
                    Signal mail = new Signal(SignalTypeEnum.Mail, BuildMissionSignalPayload(judge.Id, voyage.Id, "note for judge only"));
                    mail.TenantId = Armada.Core.Constants.DefaultTenantId;
                    mail.Read = false;
                    mail = await testDb.Driver.Signals.CreateAsync(mail).ConfigureAwait(false);

                    await missionService.HandleCompletionAsync(captain, worker.Id).ConfigureAwait(false);

                    Mission? updatedJudge = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    Mission? updatedTe = await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false);

                    AssertNotNull(updatedJudge);
                    AssertTrue(updatedJudge!.Description!.Contains("[ORCHESTRATOR NOTES]"),
                        "Judge should have notes block");
                    AssertContains("note for judge only", updatedJudge.Description);

                    AssertNotNull(updatedTe);
                    AssertFalse(updatedTe!.Description!.Contains("[ORCHESTRATOR NOTES]"),
                        "TestEngineer should NOT receive notes meant only for judge");

                    // Signal must be marked read
                    Signal? readSignal = await testDb.Driver.Signals.ReadAsync(mail.Id).ConfigureAwait(false);
                    AssertTrue(readSignal!.Read, "Mail signal should be marked read after drain");
                }
            });

            await RunTest("AlreadyReadSignal_NotReinjectedOnSecondHandoff", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git);

                    Vessel vessel = new Vessel("drain-reread-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_drain_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_drain_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain1 = new Captain("drain-reread-captain1");
                    captain1.State = CaptainStateEnum.Working;
                    captain1 = await testDb.Driver.Captains.CreateAsync(captain1).ConfigureAwait(false);

                    Captain captain2 = new Captain("drain-reread-captain2");
                    captain2.State = CaptainStateEnum.Working;
                    captain2 = await testDb.Driver.Captains.CreateAsync(captain2).ConfigureAwait(false);

                    Voyage voyage = new Voyage("drain-reread-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    // Worker -> TestEngineer -> Judge chain
                    Mission worker = new Mission("[Worker] Implement", "worker desc");
                    worker.VesselId = vessel.Id;
                    worker.VoyageId = voyage.Id;
                    worker.CaptainId = captain1.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.InProgress;
                    worker.BranchName = "armada/drain-reread/msn_worker";
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

                    Mission testEngineer = new Mission("[TestEngineer] Tests", "te desc");
                    testEngineer.VesselId = vessel.Id;
                    testEngineer.VoyageId = voyage.Id;
                    testEngineer.Persona = "TestEngineer";
                    testEngineer.Status = MissionStatusEnum.Pending;
                    testEngineer.DependsOnMissionId = worker.Id;
                    testEngineer = await testDb.Driver.Missions.CreateAsync(testEngineer).ConfigureAwait(false);

                    captain1.CurrentMissionId = worker.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain1).ConfigureAwait(false);

                    // Unread voyage-level signal
                    Signal signal = new Signal(SignalTypeEnum.Nudge, BuildVoyageSignalPayload(voyage.Id, "original nudge"));
                    signal.TenantId = Armada.Core.Constants.DefaultTenantId;
                    signal.Read = false;
                    signal = await testDb.Driver.Signals.CreateAsync(signal).ConfigureAwait(false);

                    // First handoff: Worker -> TestEngineer
                    await missionService.HandleCompletionAsync(captain1, worker.Id).ConfigureAwait(false);

                    Mission? updatedTe = await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false);
                    AssertNotNull(updatedTe);
                    AssertContains("[ORCHESTRATOR NOTES]", updatedTe!.Description!,
                        "TestEngineer brief should contain notes from first handoff");
                    AssertContains("original nudge", updatedTe.Description);

                    // Signal should now be marked read
                    Signal? readSignal = await testDb.Driver.Signals.ReadAsync(signal.Id).ConfigureAwait(false);
                    AssertTrue(readSignal!.Read, "Signal should be marked read after first handoff");

                    // Add a fresh captain to simulate TestEngineer completing
                    Captain captain3 = new Captain("drain-reread-captain3");
                    captain3.State = CaptainStateEnum.Working;
                    captain3 = await testDb.Driver.Captains.CreateAsync(captain3).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Review", "judge desc");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.Pending;
                    judge.DependsOnMissionId = testEngineer.Id;
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    // Set testEngineer to InProgress for second handoff
                    updatedTe!.Status = MissionStatusEnum.InProgress;
                    updatedTe.CaptainId = captain3.Id;
                    await testDb.Driver.Missions.UpdateAsync(updatedTe).ConfigureAwait(false);
                    captain3.CurrentMissionId = testEngineer.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain3).ConfigureAwait(false);

                    // Second handoff: TestEngineer -> Judge
                    await missionService.HandleCompletionAsync(captain3, testEngineer.Id).ConfigureAwait(false);

                    Mission? updatedJudge = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertNotNull(updatedJudge);
                    // The already-read signal must NOT be reinjected into the Judge brief
                    AssertFalse(updatedJudge!.Description!.Contains("original nudge"),
                        "Already-read signal must not be reinjected in second handoff");
                }
            });

            await RunTest("VoyageLevel_Signal_InjectedIntoAllSiblingDependents", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git);

                    Vessel vessel = new Vessel("drain-sibling-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_drain_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_drain_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("drain-sibling-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("drain-sibling-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission worker = new Mission("[Worker] Implement", "worker desc");
                    worker.VesselId = vessel.Id;
                    worker.VoyageId = voyage.Id;
                    worker.CaptainId = captain.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.InProgress;
                    worker.BranchName = "armada/drain-sibling/msn_worker";
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

                    // Two sibling dependents on the same completed worker
                    Mission judge = new Mission("[Judge] Review", "judge desc");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.Pending;
                    judge.DependsOnMissionId = worker.Id;
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    Mission testEngineer = new Mission("[TestEngineer] Tests", "te desc");
                    testEngineer.VesselId = vessel.Id;
                    testEngineer.VoyageId = voyage.Id;
                    testEngineer.Persona = "TestEngineer";
                    testEngineer.Status = MissionStatusEnum.Pending;
                    testEngineer.DependsOnMissionId = worker.Id;
                    testEngineer = await testDb.Driver.Missions.CreateAsync(testEngineer).ConfigureAwait(false);

                    captain.CurrentMissionId = worker.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    // Voyage-level signal should reach BOTH siblings
                    Signal nudge = new Signal(SignalTypeEnum.Nudge, BuildVoyageSignalPayload(voyage.Id, "note for all"));
                    nudge.TenantId = Armada.Core.Constants.DefaultTenantId;
                    nudge.Read = false;
                    nudge = await testDb.Driver.Signals.CreateAsync(nudge).ConfigureAwait(false);

                    await missionService.HandleCompletionAsync(captain, worker.Id).ConfigureAwait(false);

                    Mission? updatedJudge = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    Mission? updatedTe = await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false);

                    AssertNotNull(updatedJudge);
                    AssertNotNull(updatedTe);
                    AssertContains("[ORCHESTRATOR NOTES]", updatedJudge!.Description!,
                        "Judge should receive voyage-level note");
                    AssertContains("note for all", updatedJudge.Description);
                    AssertContains("[ORCHESTRATOR NOTES]", updatedTe!.Description!,
                        "TestEngineer should also receive voyage-level note");
                    AssertContains("note for all", updatedTe.Description);

                    // Signal marked read exactly once (both received it but it's the same signal)
                    Signal? readSignal = await testDb.Driver.Signals.ReadAsync(nudge.Id).ConfigureAwait(false);
                    AssertTrue(readSignal!.Read, "Signal should be marked read after all siblings processed");
                }
            });

            await RunTest("ArchitectHandoff_VoyageNudge_InjectedIntoFirstAndAdditionalWorkers", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git);
                    captainService.OnLaunchAgent = (Captain c, Mission m, Dock d) => Task.FromResult(12345);

                    Vessel vessel = new Vessel("drain-architect-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_drain_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_drain_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("drain-architect-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("drain-architect-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission architect = new Mission("[Architect] Plan", "Break this down");
                    architect.VesselId = vessel.Id;
                    architect.VoyageId = voyage.Id;
                    architect.CaptainId = captain.Id;
                    architect.Persona = "Architect";
                    architect.Status = MissionStatusEnum.InProgress;
                    architect.BranchName = "armada/drain-architect/msn_architect";
                    architect = await testDb.Driver.Missions.CreateAsync(architect).ConfigureAwait(false);

                    Mission workerPlaceholder = new Mission("[Worker] Placeholder", "Original dispatch description");
                    workerPlaceholder.VesselId = vessel.Id;
                    workerPlaceholder.VoyageId = voyage.Id;
                    workerPlaceholder.Persona = "Worker";
                    workerPlaceholder.Status = MissionStatusEnum.Pending;
                    workerPlaceholder.DependsOnMissionId = architect.Id;
                    workerPlaceholder = await testDb.Driver.Missions.CreateAsync(workerPlaceholder).ConfigureAwait(false);

                    captain.CurrentMissionId = architect.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    Signal nudge = new Signal(SignalTypeEnum.Nudge, BuildVoyageSignalPayload(voyage.Id, "architect-level note"));
                    nudge.TenantId = Armada.Core.Constants.DefaultTenantId;
                    nudge.Read = false;
                    nudge = await testDb.Driver.Signals.CreateAsync(nudge).ConfigureAwait(false);

                    missionService.OnGetMissionOutput = _ =>
                        "[ARMADA:MISSION] Add nudge endpoint\nImplement the endpoint\n" +
                        "[ARMADA:MISSION] Document nudge endpoint\nWrite the docs";

                    await missionService.HandleCompletionAsync(captain, architect.Id).ConfigureAwait(false);

                    // First parsed mission lands in the existing placeholder worker slot
                    Mission? updatedFirstWorker = await testDb.Driver.Missions.ReadAsync(workerPlaceholder.Id).ConfigureAwait(false);
                    AssertNotNull(updatedFirstWorker, "First worker mission should still exist");
                    AssertNotNull(updatedFirstWorker!.Description, "First worker description should be set");
                    AssertTrue(updatedFirstWorker.Description!.StartsWith("[ORCHESTRATOR NOTES]"),
                        "Notes block should be at the absolute top of the architect-prepared first worker brief");
                    AssertContains("architect-level note", updatedFirstWorker.Description);
                    AssertContains("<!-- ARMADA:ARCHITECT-HANDOFF -->", updatedFirstWorker.Description,
                        "Architect handoff marker must survive notes injection so assignment gating still works");
                    AssertContains("Implement the endpoint", updatedFirstWorker.Description);

                    // Second parsed mission becomes an additional worker; voyage-level note applies there too
                    List<Mission> voyageMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    Mission? additionalWorker = voyageMissions.FirstOrDefault(m =>
                        String.Equals(m.Persona, "Worker", StringComparison.OrdinalIgnoreCase) &&
                        m.Id != workerPlaceholder.Id);
                    AssertNotNull(additionalWorker, "Architect fan-out should create an additional worker mission");
                    AssertNotNull(additionalWorker!.Description, "Additional worker description should be set");
                    AssertTrue(additionalWorker.Description!.StartsWith("[ORCHESTRATOR NOTES]"),
                        "Voyage-level note should also top the additional worker brief");
                    AssertContains("architect-level note", additionalWorker.Description);
                    AssertContains("<!-- ARMADA:ARCHITECT-HANDOFF -->", additionalWorker.Description);
                    AssertContains("Write the docs", additionalWorker.Description);

                    Signal? readSignal = await testDb.Driver.Signals.ReadAsync(nudge.Id).ConfigureAwait(false);
                    AssertNotNull(readSignal);
                    AssertTrue(readSignal!.Read, "Signal should be marked read after architect handoff drain");
                }
            });

            await RunTest("MalformedPayloadSignal_SkippedAndLeftUnread", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git);

                    Vessel vessel = new Vessel("drain-malformed-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_drain_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_drain_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("drain-malformed-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("drain-malformed-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission worker = new Mission("[Worker] Implement", "worker desc");
                    worker.VesselId = vessel.Id;
                    worker.VoyageId = voyage.Id;
                    worker.CaptainId = captain.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.InProgress;
                    worker.BranchName = "armada/drain-malformed/msn_worker";
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Review", "judge desc");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.Pending;
                    judge.DependsOnMissionId = worker.Id;
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    captain.CurrentMissionId = worker.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    // Unread Nudge whose payload is not valid JSON
                    Signal malformed = new Signal(SignalTypeEnum.Nudge, "{not valid json!!");
                    malformed.TenantId = Armada.Core.Constants.DefaultTenantId;
                    malformed.Read = false;
                    malformed = await testDb.Driver.Signals.CreateAsync(malformed).ConfigureAwait(false);

                    await missionService.HandleCompletionAsync(captain, worker.Id).ConfigureAwait(false);

                    Mission? updatedJudge = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertNotNull(updatedJudge, "Judge mission should still exist");
                    AssertNotNull(updatedJudge!.Description, "Handoff should still prepare the judge brief");
                    AssertFalse(updatedJudge.Description!.Contains("[ORCHESTRATOR NOTES]"),
                        "Malformed payload signal must not produce a notes block");
                    AssertContains("judge desc", updatedJudge.Description,
                        "Handoff must complete normally despite the malformed signal");

                    Signal? unreadSignal = await testDb.Driver.Signals.ReadAsync(malformed.Id).ConfigureAwait(false);
                    AssertNotNull(unreadSignal);
                    AssertFalse(unreadSignal!.Read, "Malformed signal was never applied so it must stay unread");
                }
            });

            await RunTest("NonApplicableSignals_OtherVoyageAndCompletedMissionTarget_NotInjectedAndLeftUnread", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git);

                    Vessel vessel = new Vessel("drain-nonapp-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_drain_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_drain_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("drain-nonapp-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("drain-nonapp-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Voyage otherVoyage = new Voyage("drain-nonapp-other-voyage");
                    otherVoyage = await testDb.Driver.Voyages.CreateAsync(otherVoyage).ConfigureAwait(false);

                    Mission worker = new Mission("[Worker] Implement", "worker desc");
                    worker.VesselId = vessel.Id;
                    worker.VoyageId = voyage.Id;
                    worker.CaptainId = captain.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.InProgress;
                    worker.BranchName = "armada/drain-nonapp/msn_worker";
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Review", "judge desc");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.Pending;
                    judge.DependsOnMissionId = worker.Id;
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    captain.CurrentMissionId = worker.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    // Voyage-level signal addressed to a DIFFERENT voyage
                    Signal otherVoyageSignal = new Signal(SignalTypeEnum.Nudge, BuildVoyageSignalPayload(otherVoyage.Id, "note for the other voyage"));
                    otherVoyageSignal.TenantId = Armada.Core.Constants.DefaultTenantId;
                    otherVoyageSignal.Read = false;
                    otherVoyageSignal = await testDb.Driver.Signals.CreateAsync(otherVoyageSignal).ConfigureAwait(false);

                    // Mission-level signal targeting the COMPLETED worker, not a downstream dependent;
                    // it must not leak into the judge brief via the shared voyage id in its payload
                    Signal completedTargetSignal = new Signal(SignalTypeEnum.Mail, BuildMissionSignalPayload(worker.Id, voyage.Id, "note for the completed worker"));
                    completedTargetSignal.TenantId = Armada.Core.Constants.DefaultTenantId;
                    completedTargetSignal.Read = false;
                    completedTargetSignal = await testDb.Driver.Signals.CreateAsync(completedTargetSignal).ConfigureAwait(false);

                    await missionService.HandleCompletionAsync(captain, worker.Id).ConfigureAwait(false);

                    Mission? updatedJudge = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertNotNull(updatedJudge);
                    AssertNotNull(updatedJudge!.Description);
                    AssertFalse(updatedJudge.Description!.Contains("[ORCHESTRATOR NOTES]"),
                        "Non-applicable signals must not produce a notes block");
                    AssertFalse(updatedJudge.Description.Contains("note for the other voyage"),
                        "Other-voyage note must not be injected");
                    AssertFalse(updatedJudge.Description.Contains("note for the completed worker"),
                        "Mission-targeted note for the completed mission must not leak to dependents");

                    Signal? otherVoyageReadBack = await testDb.Driver.Signals.ReadAsync(otherVoyageSignal.Id).ConfigureAwait(false);
                    Signal? completedTargetReadBack = await testDb.Driver.Signals.ReadAsync(completedTargetSignal.Id).ConfigureAwait(false);
                    AssertFalse(otherVoyageReadBack!.Read, "Other-voyage signal must stay unread for its own voyage's handoff");
                    AssertFalse(completedTargetReadBack!.Read, "Unapplied mission-targeted signal must stay unread");
                }
            });

            await RunTest("MultipleApplicableSignals_CombinedIntoSingleNotesBlock", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git);

                    Vessel vessel = new Vessel("drain-multi-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_drain_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_drain_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain = new Captain("drain-multi-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("drain-multi-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission worker = new Mission("[Worker] Implement", "worker desc");
                    worker.VesselId = vessel.Id;
                    worker.VoyageId = voyage.Id;
                    worker.CaptainId = captain.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.InProgress;
                    worker.BranchName = "armada/drain-multi/msn_worker";
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Review", "judge desc");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.Pending;
                    judge.DependsOnMissionId = worker.Id;
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    captain.CurrentMissionId = worker.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    Signal voyageNudge = new Signal(SignalTypeEnum.Nudge, BuildVoyageSignalPayload(voyage.Id, "first note for everyone"));
                    voyageNudge.TenantId = Armada.Core.Constants.DefaultTenantId;
                    voyageNudge.Read = false;
                    voyageNudge = await testDb.Driver.Signals.CreateAsync(voyageNudge).ConfigureAwait(false);

                    Signal missionMail = new Signal(SignalTypeEnum.Mail, BuildMissionSignalPayload(judge.Id, voyage.Id, "second note for the judge"));
                    missionMail.TenantId = Armada.Core.Constants.DefaultTenantId;
                    missionMail.Read = false;
                    missionMail = await testDb.Driver.Signals.CreateAsync(missionMail).ConfigureAwait(false);

                    await missionService.HandleCompletionAsync(captain, worker.Id).ConfigureAwait(false);

                    Mission? updatedJudge = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertNotNull(updatedJudge);
                    AssertNotNull(updatedJudge!.Description);
                    AssertEqual(1, CountOccurrences(updatedJudge.Description!, "[ORCHESTRATOR NOTES]"),
                        "Both messages must share a single opening delimiter");
                    AssertEqual(1, CountOccurrences(updatedJudge.Description!, "[/ORCHESTRATOR NOTES]"),
                        "Both messages must share a single closing delimiter");
                    AssertContains("first note for everyone", updatedJudge.Description!);
                    AssertContains("second note for the judge", updatedJudge.Description!);

                    int openIndex = updatedJudge.Description!.IndexOf("[ORCHESTRATOR NOTES]", StringComparison.Ordinal);
                    int closeIndex = updatedJudge.Description.IndexOf("[/ORCHESTRATOR NOTES]", StringComparison.Ordinal);
                    int firstNoteIndex = updatedJudge.Description.IndexOf("first note for everyone", StringComparison.Ordinal);
                    int secondNoteIndex = updatedJudge.Description.IndexOf("second note for the judge", StringComparison.Ordinal);
                    AssertTrue(firstNoteIndex > openIndex && firstNoteIndex < closeIndex,
                        "First note must sit inside the delimited block");
                    AssertTrue(secondNoteIndex > openIndex && secondNoteIndex < closeIndex,
                        "Second note must sit inside the delimited block");

                    Signal? voyageNudgeReadBack = await testDb.Driver.Signals.ReadAsync(voyageNudge.Id).ConfigureAwait(false);
                    Signal? missionMailReadBack = await testDb.Driver.Signals.ReadAsync(missionMail.Id).ConfigureAwait(false);
                    AssertTrue(voyageNudgeReadBack!.Read, "Voyage-level signal should be marked read");
                    AssertTrue(missionMailReadBack!.Read, "Mission-level signal should be marked read");
                }
            });
        }
    }
}
