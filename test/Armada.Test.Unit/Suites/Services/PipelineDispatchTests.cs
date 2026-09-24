namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    /// <summary>
    /// Tests for pipeline-aware dispatch including multi-stage pipelines,
    /// dependency checking, and persona-aware captain routing.
    /// </summary>
    public class PipelineDispatchTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Pipeline Dispatch";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("A start ref is stamped on the first stage only, and an unresolvable ref refuses the dispatch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    AdmiralService admiralService = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService, git: git);

                    Vessel vessel = new Vessel("start-ref-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Pipeline pipeline = new Pipeline("StartRefPipeline");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "TestEngineer"),
                        new PipelineStage(3, "Judge")
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    // The accepted tip resolves in the vessel repository; the ref that is gone does not.
                    const string resolvedStartCommit = "abcdef0123456789abcdef0123456789abcdef01";
                    git.RevisionCommitShaResult = resolvedStartCommit;

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription("Continue the slice", "Continue from the accepted tip") { StartFromRef = " recover/accepted-tip-abc1234 " }
                    };

                    Voyage voyage = await admiralService.DispatchVoyageAsync("Start ref voyage", "Test", vessel.Id, missions, pipeline.Id).ConfigureAwait(false);
                    List<Mission> voyageMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(3, voyageMissions.Count, "three stages");

                    Mission worker = voyageMissions.First(m => m.Persona == "Worker");
                    Mission testEngineer = voyageMissions.First(m => m.Persona == "TestEngineer");
                    Mission judge = voyageMissions.First(m => m.Persona == "Judge");
                    AssertEqual(resolvedStartCommit, worker.StartFromRef, "the first stage carries the verified immutable start commit");
                    AssertNull(testEngineer.StartFromRef, "a later stage continues the branch; it carries no start ref");
                    AssertNull(judge.StartFromRef, "a later stage continues the branch; it carries no start ref");

                    Mission? reread = await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false);
                    AssertEqual(resolvedStartCommit, reread!.StartFromRef, "the verified start commit survives a round trip through the database");
                    List<ArmadaEvent> startEvents = await testDb.Driver.Events.EnumerateByTypeAsync("mission.start_ref_resolved", 50).ConfigureAwait(false);
                    AssertEqual(1, startEvents.Count, "only the root stage records its start ref");
                    AssertEqual(worker.Id, startEvents[0].MissionId);
                    AssertContains(resolvedStartCommit, startEvents[0].Message ?? String.Empty);
                    int voyageCountBeforeRefusal = (await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false)).Count;

                    // An unresolvable ref is refused by name before any voyage row exists.
                    List<MissionDescription> bad = new List<MissionDescription>
                    {
                        new MissionDescription("Continue the slice", "Continue from a ref that is gone") { StartFromRef = "recover/gone" }
                    };
                    git.RevisionCommitShaResult = null;
                    StartFromRefMissingException? refused = null;
                    try
                    {
                        await admiralService.DispatchVoyageAsync("Bad start ref voyage", "Test", vessel.Id, bad, pipeline.Id).ConfigureAwait(false);
                    }
                    catch (StartFromRefMissingException ex)
                    {
                        refused = ex;
                    }
                    AssertNotNull(refused, "a ref that does not resolve refuses the dispatch");
                    AssertContains("start_from_ref_missing", refused!.Message, "the refusal is named");
                    AssertContains("recover/gone", refused.Message, "the refusal names the ref");
                    AssertEqual(voyageCountBeforeRefusal,
                        (await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false)).Count,
                        "a rejected pipeline dispatch creates no voyage");

                    refused = null;
                    try
                    {
                        await admiralService.DispatchVoyageAsync("Bad direct start ref voyage", "Test", vessel.Id, bad).ConfigureAwait(false);
                    }
                    catch (StartFromRefMissingException ex)
                    {
                        refused = ex;
                    }
                    AssertNotNull(refused, "the direct dispatch path also refuses an unresolved ref");
                    AssertEqual(voyageCountBeforeRefusal,
                        (await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false)).Count,
                        "a rejected direct dispatch creates no voyage");
                    AssertTrue(git.RevisionCommitShaCalls.Count >= 3, "every dispatch uses strict commit resolution");
                }
            });

            await RunTest("PrepareBranchFromRefAsync cuts the branch at the resolved ref, leaves an existing branch alone, and refuses an unknown ref", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    DockService dockService = new DockService(logging, testDb.Driver, settings, git);

                    Vessel vessel = new Vessel("prepare-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    git.RevisionCommitShas[vessel.LocalPath + "|recover/tip-abc1234"] = "abcdef0123456789abcdef0123456789abcdef01";

                    string? unknown = await dockService.PrepareBranchFromRefAsync(vessel, "armada/captain/msn_1", "recover/missing").ConfigureAwait(false);
                    AssertNull(unknown, "an unknown ref resolves to nothing");
                    AssertEqual(0, git.ForceUpdateBranchRefCalls.Count, "no branch is written for an unknown ref");

                    string? cut = await dockService.PrepareBranchFromRefAsync(vessel, "armada/captain/msn_1", "recover/tip-abc1234").ConfigureAwait(false);
                    AssertEqual("abcdef0123456789abcdef0123456789abcdef01", cut, "the full resolved commit is returned");
                    AssertEqual(1, git.ForceUpdateBranchRefCalls.Count, "the branch is written once");
                    AssertTrue(git.RevisionCommitShaCalls.Contains(vessel.LocalPath + "|recover/tip-abc1234"), "branch preparation uses strict commit resolution");
                    AssertEqual(vessel.LocalPath + ":armada/captain/msn_1:abcdef0123456789abcdef0123456789abcdef01", git.ForceUpdateBranchRefCalls[0], "the branch is cut at the resolved commit in the vessel repository");

                    git.ExistingBranches.Add("armada/captain/msn_1");
                    string? retry = await dockService.PrepareBranchFromRefAsync(vessel, "armada/captain/msn_1", "recover/tip-abc1234").ConfigureAwait(false);
                    AssertEqual("abcdef0123456789abcdef0123456789abcdef01", retry, "a retry still reports the full commit");
                    AssertEqual(1, git.ForceUpdateBranchRefCalls.Count, "an existing branch is left where it is");
                }
            });

            await RunTest("Dispatch with same-order stages creates sibling missions with identical dependency", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                    AdmiralService admiralService = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);

                    Vessel vessel = new Vessel("dual-judge-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    // Pipeline with two Judge stages at the same Order (parallel siblings).
                    Pipeline pipeline = new Pipeline("AnalysisDualJudge");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Analyst") { PreferredModel = "high" },
                        new PipelineStage(2, "Judge") { PreferredModel = "high" },
                        new PipelineStage(2, "Judge") { PreferredModel = "high" }
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription("Analyze module", "Summarize the module")
                    };

                    Voyage voyage = await admiralService.DispatchVoyageAsync(
                        "DualJudge Voyage",
                        "Test same-order sibling dispatch",
                        vessel.Id,
                        missions,
                        pipeline.Id).ConfigureAwait(false);

                    AssertNotNull(voyage, "Voyage should be created");

                    List<Mission> voyageMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(3, voyageMissions.Count, "Should have 3 missions: 1 Analyst + 2 Judge siblings");

                    Mission? analystMission = voyageMissions.FirstOrDefault(m => m.Persona == "Analyst");
                    List<Mission> judgeMissions = voyageMissions.Where(m => m.Persona == "Judge").ToList();

                    AssertNotNull(analystMission, "Analyst mission should exist");
                    AssertEqual(2, judgeMissions.Count, "Should have exactly two Judge missions");

                    // Analyst is the first stage, so it has no dependency.
                    AssertNull(analystMission!.DependsOnMissionId, "Analyst should have no upstream dependency");

                    // Both Judge siblings depend on the Analyst, not on each other.
                    AssertEqual(analystMission.Id, judgeMissions[0].DependsOnMissionId,
                        "First Judge should depend on Analyst");
                    AssertEqual(analystMission.Id, judgeMissions[1].DependsOnMissionId,
                        "Second Judge should depend on Analyst (same as first Judge)");

                    // Verify the two Judge missions have distinct IDs (they are separate missions).
                    AssertFalse(judgeMissions[0].Id == judgeMissions[1].Id, "Two Judge missions should be distinct");
                }
            });

            await RunTest("Architect fan-out clones full downstream chain and lands only terminal stage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    DirCreatingGitStub git = new DirCreatingGitStub();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    captainService.OnLaunchAgent = (Captain c, Mission m, Dock d) => Task.FromResult(1000 + git.WorktreeCalls.Count);

                    int landingCalls = 0;
                    missionService.OnMissionComplete = (Mission mission, Dock dock) =>
                    {
                        landingCalls++;
                        mission.Status = MissionStatusEnum.Complete;
                        mission.CompletedUtc = DateTime.UtcNow;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        return testDb.Driver.Missions.UpdateAsync(mission);
                    };

                    Vessel vessel = new Vessel("fanout-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalAndRemote;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain captain1 = new Captain("fanout-captain-1");
                    captain1.State = CaptainStateEnum.Idle;
                    captain1 = await testDb.Driver.Captains.CreateAsync(captain1).ConfigureAwait(false);

                    Captain captain2 = new Captain("fanout-captain-2");
                    captain2.State = CaptainStateEnum.Idle;
                    captain2 = await testDb.Driver.Captains.CreateAsync(captain2).ConfigureAwait(false);

                    Voyage voyage = new Voyage("fanout-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission architect = new Mission("[Architect] Plan", "Break this down");
                    architect.VesselId = vessel.Id;
                    architect.VoyageId = voyage.Id;
                    architect.CaptainId = captain1.Id;
                    architect.Persona = "Architect";
                    architect.Status = MissionStatusEnum.InProgress;
                    architect.BranchName = "armada/fanout/architect";
                    architect = await testDb.Driver.Missions.CreateAsync(architect).ConfigureAwait(false);

                    Dock architectDock = new Dock(vessel.Id);
                    architectDock.CaptainId = captain1.Id;
                    architectDock.WorktreePath = Path.Combine(settings.DocksDirectory, vessel.Name, architect.Id);
                    architectDock.BranchName = architect.BranchName;
                    architectDock.Active = true;
                    architectDock = await testDb.Driver.Docks.CreateAsync(architectDock).ConfigureAwait(false);
                    architect.DockId = architectDock.Id;
                    await testDb.Driver.Missions.UpdateAsync(architect).ConfigureAwait(false);

                    Mission worker = new Mission("[Worker] Placeholder", "Initial worker");
                    worker.VesselId = vessel.Id;
                    worker.VoyageId = voyage.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.Pending;
                    worker.DependsOnMissionId = architect.Id;
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

                    Mission testEngineer = new Mission("[Test Engineer] Placeholder", "Initial tests");
                    testEngineer.VesselId = vessel.Id;
                    testEngineer.VoyageId = voyage.Id;
                    testEngineer.Persona = "Test Engineer";
                    testEngineer.Status = MissionStatusEnum.Pending;
                    testEngineer.DependsOnMissionId = worker.Id;
                    testEngineer = await testDb.Driver.Missions.CreateAsync(testEngineer).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Placeholder", "Initial review");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.Pending;
                    judge.DependsOnMissionId = testEngineer.Id;
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    captain1.CurrentMissionId = architect.Id;
                    captain1.CurrentDockId = architectDock.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain1).ConfigureAwait(false);

                    missionService.OnGetMissionOutput = _ =>
                        "[ARMADA:MISSION] Add API endpoint\nImplement endpoint\n" +
                        "[ARMADA:MISSION] Update docs\nDocument endpoint";

                    await missionService.HandleCompletionAsync(captain1, architect.Id).ConfigureAwait(false);

                    List<Mission> afterArchitect = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(7, afterArchitect.Count, "Architect fan-out should create full cloned downstream chain");
                    AssertEqual(0, landingCalls, "Architect completion should not land while downstream stages remain");
                    AssertTrue(git.DeletedLocalBranches.Contains(architect.BranchName), "Architect branch should be deleted locally after successful handoff");
                    AssertTrue(git.DeletedRemoteBranches.Contains(architect.BranchName), "Architect branch should be deleted remotely after successful handoff");

                    Mission? apiWorker = afterArchitect.FirstOrDefault(m => m.Title == "Add API endpoint [Worker]");
                    Mission? apiTest = afterArchitect.FirstOrDefault(m => m.Title == "Add API endpoint [Test Engineer]");
                    Mission? apiJudge = afterArchitect.FirstOrDefault(m => m.Title == "Add API endpoint [Judge]");
                    Mission? docsWorker = afterArchitect.FirstOrDefault(m => m.Title == "Update docs [Worker]");
                    Mission? docsTest = afterArchitect.FirstOrDefault(m => m.Title == "Update docs [Test Engineer]");
                    Mission? docsJudge = afterArchitect.FirstOrDefault(m => m.Title == "Update docs [Judge]");

                    AssertNotNull(apiWorker, "Primary worker should exist");
                    AssertNotNull(apiTest, "Primary test stage should exist");
                    AssertNotNull(apiJudge, "Primary judge stage should exist");
                    AssertNotNull(docsWorker, "Secondary worker should exist");
                    AssertNotNull(docsTest, "Secondary test stage should exist");
                    AssertNotNull(docsJudge, "Secondary judge stage should exist");
                    AssertEqual(docsWorker!.Id, docsTest!.DependsOnMissionId, "Cloned test stage should depend on cloned worker");
                    AssertEqual(docsTest.Id, docsJudge!.DependsOnMissionId, "Cloned judge stage should depend on cloned test stage");
                    AssertTrue(String.IsNullOrEmpty(docsWorker.BranchName), "Cloned worker should wait for its own branch assignment");
                    AssertContains("Implement endpoint", apiTest!.Description ?? String.Empty, "Primary test stage should inherit architect-split mission scope");
                    AssertContains("Implement endpoint", apiJudge!.Description ?? String.Empty, "Primary judge stage should inherit architect-split mission scope");
                    AssertContains("Document endpoint", docsTest.Description ?? String.Empty, "Cloned test stage should inherit architect-split mission scope");
                    AssertContains("Document endpoint", docsJudge.Description ?? String.Empty, "Cloned judge stage should inherit architect-split mission scope");
                    AssertContains(MissionService.ArchitectDerivedBriefPreamble, apiWorker!.Description ?? String.Empty, "every Architect-derived Worker brief carries the plan-block label rule");
                    AssertContains(MissionService.ArchitectDerivedBriefPreamble, docsJudge.Description ?? String.Empty, "the cloned Judge brief carries the same rule");

                    apiWorker = await testDb.Driver.Missions.ReadAsync(apiWorker!.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.InProgress, apiWorker!.Status, "Primary worker should auto-dispatch after architect completion");

                    Captain? workerCaptain = await testDb.Driver.Captains.ReadAsync(apiWorker.CaptainId!).ConfigureAwait(false);
                    missionService.OnGetMissionOutput = _ =>
                        "[ARMADA:RESULT] COMPLETE\nImplemented the API endpoint with request validation and added unit tests covering the success and error paths. The endpoint returns 200 with the expected payload and 400 on invalid input. All tests pass locally and the change is committed to the mission branch.";
                    await missionService.HandleCompletionAsync(workerCaptain!, apiWorker.Id).ConfigureAwait(false);

                    apiWorker = await testDb.Driver.Missions.ReadAsync(apiWorker.Id).ConfigureAwait(false);
                    apiTest = await testDb.Driver.Missions.ReadAsync(apiTest!.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.WorkProduced, apiWorker!.Status, "Worker should remain WorkProduced until downstream review completes");
                    AssertEqual(0, landingCalls, "Worker completion should not trigger landing");
                    AssertEqual(MissionStatusEnum.InProgress, apiTest!.Status, "Test stage should start after worker completion");

                    Captain? testCaptain = await testDb.Driver.Captains.ReadAsync(apiTest.CaptainId!).ConfigureAwait(false);
                    missionService.OnGetMissionOutput = _ =>
                        "[ARMADA:RESULT] COMPLETE\nAdded integration tests for the new endpoint covering success, validation failure, and authentication rejection paths. Verified the suite passes against the committed change and documented the negative-path coverage in the test file.";
                    await missionService.HandleCompletionAsync(testCaptain!, apiTest.Id).ConfigureAwait(false);

                    apiTest = await testDb.Driver.Missions.ReadAsync(apiTest.Id).ConfigureAwait(false);
                    apiJudge = await testDb.Driver.Missions.ReadAsync(apiJudge!.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.WorkProduced, apiTest!.Status, "Test Engineer should remain WorkProduced until judge completes");
                    AssertEqual(0, landingCalls, "Test Engineer completion should not trigger landing");
                    AssertEqual(MissionStatusEnum.InProgress, apiJudge!.Status, "Judge should start after Test Engineer completion");

                    Captain? judgeCaptain = await testDb.Driver.Captains.ReadAsync(apiJudge.CaptainId!).ConfigureAwait(false);
                    missionService.OnGetMissionOutput = _ =>
                        "## Completeness\n" +
                        "The staged work covers the assigned requirements and there are no missing deliverables in this chain.\n\n" +
                        "## Correctness\n" +
                        "The implementation and test updates are coherent, and I do not see logic or scope defects in the reviewed diff.\n\n" +
                        "## Tests\n" +
                        "The automated tests added in the prior stage cover the reviewed behavior adequately for this mission.\n\n" +
                        "## Failure Modes\n" +
                        "I reviewed the relevant edge and failure behavior for this scope and did not find any unresolved blockers.\n\n" +
                        "## Verdict\n" +
                        "[ARMADA:VERDICT] PASS\n" +
                        "The work is complete and correctly scoped.\n" +
                        "tokens used\n" +
                        "48,676";
                    await AddGreenVoyageCheckAsync(testDb, voyage.Id).ConfigureAwait(false);
                    await missionService.HandleCompletionAsync(judgeCaptain!, apiJudge.Id).ConfigureAwait(false);

                    apiJudge = await testDb.Driver.Missions.ReadAsync(apiJudge.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Complete, apiJudge!.Status, "Judge completion should trigger terminal landing");
                    AssertEqual(1, landingCalls, "Only the terminal judge stage should land");
                }
            });

            await RunTest("Architect fan-out honors explicit mission dependencies across worker chains", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    DirCreatingGitStub git = new DirCreatingGitStub();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    captainService.OnLaunchAgent = (Captain c, Mission m, Dock d) => Task.FromResult(2000 + git.WorktreeCalls.Count);

                    int landingCalls = 0;
                    missionService.OnMissionComplete = (Mission mission, Dock dock) =>
                    {
                        landingCalls++;
                        mission.Status = MissionStatusEnum.Complete;
                        mission.CompletedUtc = DateTime.UtcNow;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        return testDb.Driver.Missions.UpdateAsync(mission);
                    };

                    Vessel vessel = new Vessel("sequenced-fanout-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.LocalAndRemote;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain architectCaptain = new Captain("sequenced-architect");
                    architectCaptain.State = CaptainStateEnum.Idle;
                    architectCaptain = await testDb.Driver.Captains.CreateAsync(architectCaptain).ConfigureAwait(false);

                    Captain workerCaptain = new Captain("sequenced-worker");
                    workerCaptain.State = CaptainStateEnum.Idle;
                    workerCaptain = await testDb.Driver.Captains.CreateAsync(workerCaptain).ConfigureAwait(false);

                    Captain reviewerCaptain = new Captain("sequenced-reviewer");
                    reviewerCaptain.State = CaptainStateEnum.Idle;
                    reviewerCaptain = await testDb.Driver.Captains.CreateAsync(reviewerCaptain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("sequenced-fanout-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission architect = new Mission("[Architect] Plan", "Break this down");
                    architect.VesselId = vessel.Id;
                    architect.VoyageId = voyage.Id;
                    architect.CaptainId = architectCaptain.Id;
                    architect.Persona = "Architect";
                    architect.Status = MissionStatusEnum.InProgress;
                    architect.BranchName = "armada/sequenced/architect";
                    architect = await testDb.Driver.Missions.CreateAsync(architect).ConfigureAwait(false);

                    Dock architectDock = new Dock(vessel.Id);
                    architectDock.CaptainId = architectCaptain.Id;
                    architectDock.WorktreePath = Path.Combine(settings.DocksDirectory, vessel.Name, architect.Id);
                    architectDock.BranchName = architect.BranchName;
                    architectDock.Active = true;
                    architectDock = await testDb.Driver.Docks.CreateAsync(architectDock).ConfigureAwait(false);
                    architect.DockId = architectDock.Id;
                    await testDb.Driver.Missions.UpdateAsync(architect).ConfigureAwait(false);

                    Mission worker = new Mission("[Worker] Placeholder", "Initial worker");
                    worker.VesselId = vessel.Id;
                    worker.VoyageId = voyage.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.Pending;
                    worker.DependsOnMissionId = architect.Id;
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

                    Mission testEngineer = new Mission("[Test Engineer] Placeholder", "Initial tests");
                    testEngineer.VesselId = vessel.Id;
                    testEngineer.VoyageId = voyage.Id;
                    testEngineer.Persona = "Test Engineer";
                    testEngineer.Status = MissionStatusEnum.Pending;
                    testEngineer.DependsOnMissionId = worker.Id;
                    testEngineer = await testDb.Driver.Missions.CreateAsync(testEngineer).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Placeholder", "Initial review");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.Pending;
                    judge.DependsOnMissionId = testEngineer.Id;
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    architectCaptain.CurrentMissionId = architect.Id;
                    architectCaptain.CurrentDockId = architectDock.Id;
                    await testDb.Driver.Captains.UpdateAsync(architectCaptain).ConfigureAwait(false);

                    missionService.OnGetMissionOutput = _ =>
                        "[ARMADA:MISSION] Add core model properties\n" +
                        "Update Captain.cs and Mission.cs.\n" +
                        "[ARMADA:MISSION] Extend secondary backends\n" +
                        "Depends on: Mission 1 (core model changes must land first)\n" +
                        "Update PostgreSQL, MySQL, and migration scripts.";

                    await missionService.HandleCompletionAsync(architectCaptain, architect.Id).ConfigureAwait(false);

                    List<Mission> afterArchitect = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    Mission? coreWorker = afterArchitect.FirstOrDefault(m => m.Title == "Add core model properties [Worker]");
                    Mission? coreTest = afterArchitect.FirstOrDefault(m => m.Title == "Add core model properties [Test Engineer]");
                    Mission? coreJudge = afterArchitect.FirstOrDefault(m => m.Title == "Add core model properties [Judge]");
                    Mission? backendWorker = afterArchitect.FirstOrDefault(m => m.Title == "Extend secondary backends [Worker]");
                    Mission? backendTest = afterArchitect.FirstOrDefault(m => m.Title == "Extend secondary backends [Test Engineer]");
                    Mission? backendJudge = afterArchitect.FirstOrDefault(m => m.Title == "Extend secondary backends [Judge]");

                    AssertNotNull(coreWorker, "Primary worker should exist");
                    AssertNotNull(coreTest, "Primary test stage should exist");
                    AssertNotNull(coreJudge, "Primary judge stage should exist");
                    AssertNotNull(backendWorker, "Dependent worker should exist");
                    AssertNotNull(backendTest, "Dependent test stage should exist");
                    AssertNotNull(backendJudge, "Dependent judge stage should exist");
                    AssertEqual(coreJudge!.Id, backendWorker!.DependsOnMissionId, "Dependent worker should wait for the upstream chain's terminal stage");
                    AssertFalse((backendWorker.Description ?? String.Empty).Contains("Depends on:", StringComparison.OrdinalIgnoreCase),
                        "Dependency metadata should be removed from the worker description after parsing");
                    AssertEqual(MissionStatusEnum.InProgress, coreWorker!.Status, "Primary worker should auto-dispatch after architect completion");
                    AssertEqual(MissionStatusEnum.Pending, backendWorker.Status, "Dependent worker should remain pending until the upstream chain completes");

                    Captain? activeWorkerCaptain = await testDb.Driver.Captains.ReadAsync(coreWorker.CaptainId!).ConfigureAwait(false);
                    missionService.OnGetMissionOutput = _ =>
                        "[ARMADA:RESULT] COMPLETE\nAdded the core model properties to Captain and Mission including the new status fields, serialization round-trip coverage, and the migration script that applies the columns. The unit suite passes against the committed branch.";
                    await missionService.HandleCompletionAsync(activeWorkerCaptain!, coreWorker.Id).ConfigureAwait(false);

                    coreTest = await testDb.Driver.Missions.ReadAsync(coreTest!.Id).ConfigureAwait(false);
                    backendWorker = await testDb.Driver.Missions.ReadAsync(backendWorker.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.InProgress, coreTest!.Status, "Primary test stage should start after the primary worker");
                    AssertEqual(MissionStatusEnum.Pending, backendWorker!.Status, "Dependent worker should still wait while upstream review is running");

                    Captain? activeTestCaptain = await testDb.Driver.Captains.ReadAsync(coreTest.CaptainId!).ConfigureAwait(false);
                    missionService.OnGetMissionOutput = _ =>
                        "[ARMADA:RESULT] COMPLETE\nExtended the automated test coverage for the core model changes including the new properties, defaults, and serialization edge cases. The full suite passes and the added tests are committed to the mission branch.";
                    await missionService.HandleCompletionAsync(activeTestCaptain!, coreTest.Id).ConfigureAwait(false);

                    coreJudge = await testDb.Driver.Missions.ReadAsync(coreJudge.Id).ConfigureAwait(false);
                    backendWorker = await testDb.Driver.Missions.ReadAsync(backendWorker.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.InProgress, coreJudge!.Status, "Primary judge should start after the primary test stage");
                    AssertEqual(MissionStatusEnum.Pending, backendWorker!.Status, "Dependent worker should still wait while the primary judge is running");

                    Captain? activeJudgeCaptain = await testDb.Driver.Captains.ReadAsync(coreJudge.CaptainId!).ConfigureAwait(false);
                    missionService.OnGetMissionOutput = _ =>
                        "## Completeness\n" +
                        "The upstream worker and test stages completed the requested scope and nothing material is missing.\n\n" +
                        "## Correctness\n" +
                        "I reviewed the diff and prior output and did not find correctness issues in the completed upstream chain.\n\n" +
                        "## Tests\n" +
                        "The upstream tests cover the changed behavior and are sufficient for this dependency chain.\n\n" +
                        "## Failure Modes\n" +
                        "Relevant error and edge paths were reviewed for this scope and I do not see unresolved safety concerns.\n\n" +
                        "## Verdict\n" +
                        "[ARMADA:VERDICT] PASS\n" +
                        "Upstream chain is approved.\n";
                    await AddGreenVoyageCheckAsync(testDb, voyage.Id).ConfigureAwait(false);
                    await missionService.HandleCompletionAsync(activeJudgeCaptain!, coreJudge.Id).ConfigureAwait(false);

                    coreJudge = await testDb.Driver.Missions.ReadAsync(coreJudge.Id).ConfigureAwait(false);
                    backendWorker = await testDb.Driver.Missions.ReadAsync(backendWorker.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.WorkProduced, coreJudge!.Status, "Upstream judge should remain WorkProduced while dependent stages still exist");
                    AssertEqual(0, landingCalls, "Upstream judge should not land while a dependent worker chain remains");
                    AssertEqual(MissionStatusEnum.InProgress, backendWorker!.Status, "Dependent worker should start once the upstream chain completes");
                    AssertEqual(coreJudge.BranchName, backendWorker.BranchName, "Dependent worker should inherit the approved upstream branch");
                }
            });

            // A planner that commits behaviour strands every fan-out worker below it: they are cut
            // without that commit by design and fail stage_base_missing two stages later, read as a
            // captain fault. The handoff fails the planner by name and creates no workers.
            await RunTest("Architect handoff fails a planner that committed code and creates no fan-out", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    DirCreatingGitStub git = new DirCreatingGitStub();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    captainService.OnLaunchAgent = (Captain c, Mission m, Dock d) => Task.FromResult(4000 + git.WorktreeCalls.Count);

                    Vessel vessel = new Vessel("planner-code-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain architectCaptain = new Captain("planner-code-architect");
                    architectCaptain.State = CaptainStateEnum.Working;
                    architectCaptain = await testDb.Driver.Captains.CreateAsync(architectCaptain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("planner-code-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission architect = new Mission("[Architect] Plan", "Break this down");
                    architect.VesselId = vessel.Id;
                    architect.VoyageId = voyage.Id;
                    architect.CaptainId = architectCaptain.Id;
                    architect.Persona = "Architect";
                    architect.Status = MissionStatusEnum.InProgress;
                    architect.BranchName = "armada/planner-code/architect";
                    architect = await testDb.Driver.Missions.CreateAsync(architect).ConfigureAwait(false);

                    Dock architectDock = new Dock(vessel.Id);
                    architectDock.CaptainId = architectCaptain.Id;
                    architectDock.WorktreePath = Path.Combine(settings.DocksDirectory, vessel.Name, architect.Id);
                    architectDock.BranchName = architect.BranchName;
                    architectDock.Active = true;
                    architectDock = await testDb.Driver.Docks.CreateAsync(architectDock).ConfigureAwait(false);
                    architect.DockId = architectDock.Id;
                    await testDb.Driver.Missions.UpdateAsync(architect).ConfigureAwait(false);

                    Mission worker = new Mission("[Worker] Placeholder", "Initial worker");
                    worker.VesselId = vessel.Id;
                    worker.VoyageId = voyage.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.Pending;
                    worker.DependsOnMissionId = architect.Id;
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

                    // The planner's captured diff carries behaviour, not a plan.
                    missionService.OnCaptureDiff = async (Mission m, Dock d) =>
                    {
                        Mission? stored = await testDb.Driver.Missions.ReadAsync(m.Id).ConfigureAwait(false);
                        stored!.DiffSnapshot = "diff --git a/src/Catalog/Reader.cs b/src/Catalog/Reader.cs\n--- a/src/Catalog/Reader.cs\n+++ b/src/Catalog/Reader.cs\n@@ -1 +1,2 @@\n+class Reader { }\ndiff --git a/docs/plan.md b/docs/plan.md\n--- a/docs/plan.md\n+++ b/docs/plan.md\n@@ -1 +1 @@\n+plan\n";
                        await testDb.Driver.Missions.UpdateAsync(stored).ConfigureAwait(false);
                    };
                    missionService.OnGetMissionOutput = _ =>
                        "[ARMADA:MISSION]\n" +
                        "title: Implement the reader\n" +
                        "goal: Read the catalogue\n" +
                        "inputs: the catalogue file\n" +
                        "deliverables: Reader.cs\n" +
                        "dependencies: none\n" +
                        "risks: none\n" +
                        "done_when: tests pass\n" +
                        "[/ARMADA:MISSION]";

                    await missionService.HandleCompletionAsync(architectCaptain, architect.Id).ConfigureAwait(false);

                    Mission? updatedArchitect = await testDb.Driver.Missions.ReadAsync(architect.Id).ConfigureAwait(false);
                    List<Mission> afterArchitect = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Failed, updatedArchitect!.Status, "A planner that committed code fails at its own handoff");
                    AssertContains(MissionService.PlannerCommittedCodeReason, updatedArchitect.FailureReason ?? String.Empty, "The failure names the rule");
                    AssertContains("src/Catalog/Reader.cs", updatedArchitect.FailureReason ?? String.Empty, "The failure names the behaviour-carrying file");
                    AssertEqual(2, afterArchitect.Count, "No fan-out worker is created against a base that lacks the planner's commit");
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("mission.planner_committed_code").ConfigureAwait(false);
                    AssertEqual(1, events.Count, "The overreach is recorded as an event");
                }
            });

            await RunTest("Architect handoff accepts a planner whose diff is documentation only", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    DirCreatingGitStub git = new DirCreatingGitStub();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    captainService.OnLaunchAgent = (Captain c, Mission m, Dock d) => Task.FromResult(4000 + git.WorktreeCalls.Count);

                    Vessel vessel = new Vessel("planner-docs-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain architectCaptain = new Captain("planner-docs-architect");
                    architectCaptain.State = CaptainStateEnum.Working;
                    architectCaptain = await testDb.Driver.Captains.CreateAsync(architectCaptain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("planner-docs-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission architect = new Mission("[Architect] Plan", "Break this down");
                    architect.VesselId = vessel.Id;
                    architect.VoyageId = voyage.Id;
                    architect.CaptainId = architectCaptain.Id;
                    architect.Persona = "Architect";
                    architect.Status = MissionStatusEnum.InProgress;
                    architect.BranchName = "armada/planner-docs/architect";
                    architect = await testDb.Driver.Missions.CreateAsync(architect).ConfigureAwait(false);

                    Dock architectDock = new Dock(vessel.Id);
                    architectDock.CaptainId = architectCaptain.Id;
                    architectDock.WorktreePath = Path.Combine(settings.DocksDirectory, vessel.Name, architect.Id);
                    architectDock.BranchName = architect.BranchName;
                    architectDock.Active = true;
                    architectDock = await testDb.Driver.Docks.CreateAsync(architectDock).ConfigureAwait(false);
                    architect.DockId = architectDock.Id;
                    await testDb.Driver.Missions.UpdateAsync(architect).ConfigureAwait(false);

                    Mission worker = new Mission("[Worker] Placeholder", "Initial worker");
                    worker.VesselId = vessel.Id;
                    worker.VoyageId = voyage.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.Pending;
                    worker.DependsOnMissionId = architect.Id;
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

                    missionService.OnCaptureDiff = async (Mission m, Dock d) =>
                    {
                        Mission? stored = await testDb.Driver.Missions.ReadAsync(m.Id).ConfigureAwait(false);
                        stored!.DiffSnapshot = "diff --git a/docs/plan.md b/docs/plan.md\n--- a/docs/plan.md\n+++ b/docs/plan.md\n@@ -1 +1 @@\n+plan\n";
                        await testDb.Driver.Missions.UpdateAsync(stored).ConfigureAwait(false);
                    };
                    missionService.OnGetMissionOutput = _ =>
                        "[ARMADA:MISSION]\n" +
                        "title: Implement the reader\n" +
                        "goal: Read the catalogue\n" +
                        "inputs: the catalogue file\n" +
                        "deliverables: Reader.cs\n" +
                        "dependencies: none\n" +
                        "risks: none\n" +
                        "done_when: tests pass\n" +
                        "[/ARMADA:MISSION]";

                    await missionService.HandleCompletionAsync(architectCaptain, architect.Id).ConfigureAwait(false);

                    Mission? updatedArchitect = await testDb.Driver.Missions.ReadAsync(architect.Id).ConfigureAwait(false);
                    // The guard must not fire on a documentation-only diff; whether the plan itself parses
                    // is the parser's business and is pinned by its own tests.
                    AssertTrue(String.IsNullOrEmpty(updatedArchitect!.FailureReason) || !updatedArchitect.FailureReason.Contains(MissionService.PlannerCommittedCodeReason),
                        "A documentation-only planner diff is a plan; the code guard must not fire: " + (updatedArchitect.FailureReason ?? "(none)"));
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("mission.planner_committed_code").ConfigureAwait(false);
                    AssertEqual(0, events.Count, "No planner_committed_code event on a docs-only diff");
                }
            });

            await RunTest("Judge parser accepts structured ARMADA verdict signal", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    DirCreatingGitStub git = new DirCreatingGitStub();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    int landingCalls = 0;
                    missionService.OnMissionComplete = (m, d) =>
                    {
                        landingCalls++;
                        m.Status = MissionStatusEnum.Complete;
                        m.CompletedUtc = DateTime.UtcNow;
                        m.LastUpdateUtc = DateTime.UtcNow;
                        return testDb.Driver.Missions.UpdateAsync(m);
                    };

                    Vessel vessel = new Vessel("judge-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain judgeCaptain = new Captain("judge-captain");
                    judgeCaptain.State = CaptainStateEnum.Working;
                    judgeCaptain = await testDb.Driver.Captains.CreateAsync(judgeCaptain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("judge-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Review worker output", "Review changes");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.InProgress;
                    judge.CaptainId = judgeCaptain.Id;
                    judge.BranchName = "armada/judge/review";
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    Dock judgeDock = new Dock(vessel.Id);
                    judgeDock.CaptainId = judgeCaptain.Id;
                    judgeDock.WorktreePath = Path.Combine(settings.DocksDirectory, vessel.Name, judge.Id);
                    judgeDock.BranchName = judge.BranchName;
                    judgeDock.Active = true;
                    judgeDock = await testDb.Driver.Docks.CreateAsync(judgeDock).ConfigureAwait(false);

                    judge.DockId = judgeDock.Id;
                    await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);

                    judgeCaptain.CurrentMissionId = judge.Id;
                    judgeCaptain.CurrentDockId = judgeDock.Id;
                    await testDb.Driver.Captains.UpdateAsync(judgeCaptain).ConfigureAwait(false);

                    missionService.OnGetMissionOutput = _ =>
                        "## Completeness\n" +
                        "The mission requirements are fully implemented with no missing scope items.\n\n" +
                        "## Correctness\n" +
                        "The reviewed changes are logically consistent and I do not see defects in the touched paths.\n\n" +
                        "## Tests\n" +
                        "Automated coverage exists for the new behavior and the affected scenarios are exercised.\n\n" +
                        "## Failure Modes\n" +
                        "I reviewed error and edge behavior for this scope and found no unaddressed safety issues.\n\n" +
                        "## Verdict\n" +
                        "[ARMADA:VERDICT] PASS\n" +
                        "Everything is complete and correctly scoped.";
                    await AddGreenVoyageCheckAsync(testDb, voyage.Id).ConfigureAwait(false);

                    await missionService.HandleCompletionAsync(judgeCaptain, judge.Id).ConfigureAwait(false);

                    Mission? reloadedJudge = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertNotNull(reloadedJudge, "Judge mission should remain readable");
                    AssertEqual(MissionStatusEnum.Complete, reloadedJudge!.Status, "Structured verdict signal should permit landing");
                    AssertEqual(1, landingCalls, "Structured PASS verdict should invoke landing");
                }
            });

            await RunTest("Judge parser accepts markdown verdict heading emitted by Claude", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    DirCreatingGitStub git = new DirCreatingGitStub();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    int landingCalls = 0;
                    missionService.OnMissionComplete = (m, d) =>
                    {
                        landingCalls++;
                        m.Status = MissionStatusEnum.Complete;
                        m.CompletedUtc = DateTime.UtcNow;
                        m.LastUpdateUtc = DateTime.UtcNow;
                        return testDb.Driver.Missions.UpdateAsync(m);
                    };

                    Vessel vessel = new Vessel("judge-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain judgeCaptain = new Captain("judge-captain");
                    judgeCaptain.State = CaptainStateEnum.Working;
                    judgeCaptain = await testDb.Driver.Captains.CreateAsync(judgeCaptain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("judge-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Review worker output", "Review changes");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.InProgress;
                    judge.CaptainId = judgeCaptain.Id;
                    judge.BranchName = "armada/judge/review";
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    Dock judgeDock = new Dock(vessel.Id);
                    judgeDock.CaptainId = judgeCaptain.Id;
                    judgeDock.WorktreePath = Path.Combine(settings.DocksDirectory, vessel.Name, judge.Id);
                    judgeDock.BranchName = judge.BranchName;
                    judgeDock.Active = true;
                    judgeDock = await testDb.Driver.Docks.CreateAsync(judgeDock).ConfigureAwait(false);

                    judge.DockId = judgeDock.Id;
                    await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);

                    judgeCaptain.CurrentMissionId = judge.Id;
                    judgeCaptain.CurrentDockId = judgeDock.Id;
                    await testDb.Driver.Captains.UpdateAsync(judgeCaptain).ConfigureAwait(false);

                    missionService.OnGetMissionOutput = _ =>
                        "## Completeness\n" +
                        "Everything required by the mission is present and stays within the assigned scope.\n\n" +
                        "## Correctness\n" +
                        "The implementation follows the intended behavior and I did not find logic errors in the reviewed diff.\n\n" +
                        "## Tests\n" +
                        "The updated tests cover the changed behavior and are sufficient for this mission.\n\n" +
                        "## Failure Modes\n" +
                        "I explicitly reviewed edge and failure paths relevant to this change and found no remaining blockers.\n\n" +
                        "### Verdict: **PASS**\n" +
                        "The mission is complete and correct.";
                    await AddGreenVoyageCheckAsync(testDb, voyage.Id).ConfigureAwait(false);

                    await missionService.HandleCompletionAsync(judgeCaptain, judge.Id).ConfigureAwait(false);

                    Mission? reloadedJudge = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertNotNull(reloadedJudge, "Judge mission should remain readable");
                    AssertEqual(MissionStatusEnum.Complete, reloadedJudge!.Status, "Markdown verdict heading should permit landing");
                    AssertEqual(1, landingCalls, "PASS verdict emitted as a markdown heading should invoke landing");
                }
            });

            await RunTest("Judge parser accepts inline sentence verdict emitted by Claude", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    DirCreatingGitStub git = new DirCreatingGitStub();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    int landingCalls = 0;
                    missionService.OnMissionComplete = (m, d) =>
                    {
                        landingCalls++;
                        m.Status = MissionStatusEnum.Complete;
                        m.CompletedUtc = DateTime.UtcNow;
                        m.LastUpdateUtc = DateTime.UtcNow;
                        return testDb.Driver.Missions.UpdateAsync(m);
                    };

                    Vessel vessel = new Vessel("judge-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain judgeCaptain = new Captain("judge-captain");
                    judgeCaptain.State = CaptainStateEnum.Working;
                    judgeCaptain = await testDb.Driver.Captains.CreateAsync(judgeCaptain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("judge-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Review worker output", "Review changes");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.InProgress;
                    judge.CaptainId = judgeCaptain.Id;
                    judge.BranchName = "armada/judge/review";
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    Dock judgeDock = new Dock(vessel.Id);
                    judgeDock.CaptainId = judgeCaptain.Id;
                    judgeDock.WorktreePath = Path.Combine(settings.DocksDirectory, vessel.Name, judge.Id);
                    judgeDock.BranchName = judge.BranchName;
                    judgeDock.Active = true;
                    judgeDock = await testDb.Driver.Docks.CreateAsync(judgeDock).ConfigureAwait(false);

                    judge.DockId = judgeDock.Id;
                    await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);

                    judgeCaptain.CurrentMissionId = judge.Id;
                    judgeCaptain.CurrentDockId = judgeDock.Id;
                    await testDb.Driver.Captains.UpdateAsync(judgeCaptain).ConfigureAwait(false);

                    missionService.OnGetMissionOutput = _ =>
                        "## Completeness\n" +
                        "Everything required by the mission is present and stays within the assigned scope.\n\n" +
                        "## Correctness\n" +
                        "The implementation follows the intended behavior and I did not find logic errors in the reviewed diff.\n\n" +
                        "## Tests\n" +
                        "The updated tests cover the changed behavior and are sufficient for this mission.\n\n" +
                        "## Failure Modes\n" +
                        "I explicitly reviewed edge and failure paths relevant to this change and found no remaining blockers.\n\n" +
                        "Judge review complete. Verdict: **PASS**. All 8 release surfaces are checked, negative paths are covered, and the REST_API.md drift fix stays within scope.";
                    await AddGreenVoyageCheckAsync(testDb, voyage.Id).ConfigureAwait(false);

                    await missionService.HandleCompletionAsync(judgeCaptain, judge.Id).ConfigureAwait(false);

                    Mission? reloadedJudge = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertNotNull(reloadedJudge, "Judge mission should remain readable");
                    AssertEqual(MissionStatusEnum.Complete, reloadedJudge!.Status, "Inline sentence verdict should permit landing");
                    AssertEqual(1, landingCalls, "Sentence-style PASS verdict should invoke landing");
                }
            });

            await RunTest("Judge PASS rejected by the real-signal gate keeps the specific failure reason", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    DirCreatingGitStub git = new DirCreatingGitStub();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    int landingCalls = 0;
                    missionService.OnMissionComplete = (m, d) =>
                    {
                        landingCalls++;
                        return Task.CompletedTask;
                    };

                    Vessel vessel = new Vessel("judge-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain judgeCaptain = new Captain("judge-captain");
                    judgeCaptain.State = CaptainStateEnum.Working;
                    judgeCaptain = await testDb.Driver.Captains.CreateAsync(judgeCaptain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("judge-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Review worker output", "Review changes");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.InProgress;
                    judge.CaptainId = judgeCaptain.Id;
                    judge.BranchName = "armada/judge/review";
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    Dock judgeDock = new Dock(vessel.Id);
                    judgeDock.CaptainId = judgeCaptain.Id;
                    judgeDock.WorktreePath = Path.Combine(settings.DocksDirectory, vessel.Name, judge.Id);
                    judgeDock.BranchName = judge.BranchName;
                    judgeDock.Active = true;
                    judgeDock = await testDb.Driver.Docks.CreateAsync(judgeDock).ConfigureAwait(false);

                    judge.DockId = judgeDock.Id;
                    await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);

                    judgeCaptain.CurrentMissionId = judge.Id;
                    judgeCaptain.CurrentDockId = judgeDock.Id;
                    await testDb.Driver.Captains.UpdateAsync(judgeCaptain).ConfigureAwait(false);

                    // A genuine PASS review with all four required sections. No independent
                    // Checks exist, so the real-signal gate must reject it -- and the stored
                    // failure reason must say WHY (no checks), not the generic judge-verdict
                    // line that previously overwrote it (regression for the recovery-judge
                    // misreport where a PASS read as "Judge verdict: FAIL").
                    missionService.OnGetMissionOutput = _ =>
                        "## Completeness\n" +
                        "Every spec item is present and correct on the reviewed branch.\n\n" +
                        "## Correctness\n" +
                        "Verified by execution: the build is clean and the gate passes.\n\n" +
                        "## Tests\n" +
                        "Negative paths are covered and the full suite is green.\n\n" +
                        "## Failure Modes\n" +
                        "No genuine defect found; the blast radius is contained.\n\n" +
                        "[ARMADA:VERDICT] PASS";

                    await missionService.HandleCompletionAsync(judgeCaptain, judge.Id).ConfigureAwait(false);

                    Mission? reloadedJudge = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertNotNull(reloadedJudge, "Judge mission should remain readable");
                    AssertEqual(MissionStatusEnum.Failed, reloadedJudge!.Status, "Judge PASS without Checks must be rejected by the real-signal gate");
                    AssertContains("no green independent Checks", reloadedJudge.FailureReason ?? String.Empty,
                        "The failure reason must name the missing independent Checks, not a judge rejection");
                    AssertFalse((reloadedJudge.FailureReason ?? String.Empty).Contains("Judge verdict: FAIL", StringComparison.Ordinal),
                        "A PASS rejected by the check gate must not be misreported as a judge FAIL verdict");
                    AssertContains("## Completeness", reloadedJudge.ReviewComment ?? String.Empty,
                        "The review comment must preserve the judge's written review");
                    AssertEqual(0, landingCalls, "Judge PASS rejected by the gate must not invoke landing");
                }
            });

            await RunTest("Judge parser accepts review sections wrapped in markdown emphasis", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    DirCreatingGitStub git = new DirCreatingGitStub();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    int landingCalls = 0;
                    missionService.OnMissionComplete = (m, d) =>
                    {
                        landingCalls++;
                        m.Status = MissionStatusEnum.Complete;
                        m.CompletedUtc = DateTime.UtcNow;
                        m.LastUpdateUtc = DateTime.UtcNow;
                        return testDb.Driver.Missions.UpdateAsync(m);
                    };

                    Vessel vessel = new Vessel("judge-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain judgeCaptain = new Captain("judge-captain");
                    judgeCaptain.State = CaptainStateEnum.Working;
                    judgeCaptain = await testDb.Driver.Captains.CreateAsync(judgeCaptain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("judge-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Review worker output", "Review changes");
                    judge.VesselId = vessel.Id;
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.InProgress;
                    judge.CaptainId = judgeCaptain.Id;
                    judge.BranchName = "armada/judge/review";
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    Dock judgeDock = new Dock(vessel.Id);
                    judgeDock.CaptainId = judgeCaptain.Id;
                    judgeDock.WorktreePath = Path.Combine(settings.DocksDirectory, vessel.Name, judge.Id);
                    judgeDock.BranchName = judge.BranchName;
                    judgeDock.Active = true;
                    judgeDock = await testDb.Driver.Docks.CreateAsync(judgeDock).ConfigureAwait(false);

                    judge.DockId = judgeDock.Id;
                    await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);

                    judgeCaptain.CurrentMissionId = judge.Id;
                    judgeCaptain.CurrentDockId = judgeDock.Id;
                    await testDb.Driver.Captains.UpdateAsync(judgeCaptain).ConfigureAwait(false);

                    await AddGreenVoyageCheckAsync(testDb, voyage.Id).ConfigureAwait(false);

                    // Emphasis can wrap the heading markers as well as sit inside them. A judge that
                    // writes `**## Completeness**` has produced every required section; rejecting the
                    // review for its formatting fails work that is sound on substance.
                    missionService.OnGetMissionOutput = _ =>
                        "**## Completeness**\n" +
                        "The mission requirements are fully implemented with no missing scope items.\n\n" +
                        "**## Correctness**\n" +
                        "The reviewed changes are logically consistent and I do not see defects in the touched paths.\n\n" +
                        "__Tests__\n" +
                        "Automated coverage exists for the new behavior and the affected scenarios are exercised.\n\n" +
                        "  *## Failure Modes:*\n" +
                        "I reviewed error and edge behavior for this scope and found no unaddressed safety issues.\n\n" +
                        "## Verdict\n" +
                        "[ARMADA:VERDICT] PASS\n" +
                        "Everything is complete and correctly scoped.";

                    await missionService.HandleCompletionAsync(judgeCaptain, judge.Id).ConfigureAwait(false);

                    Mission? reloadedJudge = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertNotNull(reloadedJudge, "Judge mission should remain readable");
                    AssertEqual(MissionStatusEnum.Complete, reloadedJudge!.Status, "Emphasis-wrapped review sections should permit landing");
                    AssertEqual(1, landingCalls, "PASS review with emphasis-wrapped sections should invoke landing");
                }
            });

            await RunTest("Completion backfills missing branch from dock before handoff", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    DirCreatingGitStub git = new DirCreatingGitStub();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    captainService.OnLaunchAgent = (Captain c, Mission m, Dock d) => Task.FromResult(2000 + git.WorktreeCalls.Count);

                    Vessel vessel = new Vessel("branch-backfill-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Captain workerCaptain = new Captain("branch-backfill-worker");
                    workerCaptain.State = CaptainStateEnum.Working;
                    workerCaptain = await testDb.Driver.Captains.CreateAsync(workerCaptain).ConfigureAwait(false);

                    Captain reviewerCaptain = new Captain("branch-backfill-reviewer");
                    reviewerCaptain.State = CaptainStateEnum.Idle;
                    reviewerCaptain = await testDb.Driver.Captains.CreateAsync(reviewerCaptain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("branch-backfill-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission worker = new Mission("Implement branch-sensitive change", "Change code");
                    worker.VesselId = vessel.Id;
                    worker.VoyageId = voyage.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.InProgress;
                    worker.CaptainId = workerCaptain.Id;
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

                    Dock workerDock = new Dock(vessel.Id);
                    workerDock.CaptainId = workerCaptain.Id;
                    workerDock.WorktreePath = Path.Combine(settings.DocksDirectory, vessel.Name, worker.Id);
                    workerDock.BranchName = "armada/backfill/shared";
                    workerDock.Active = true;
                    workerDock = await testDb.Driver.Docks.CreateAsync(workerDock).ConfigureAwait(false);

                    worker.DockId = workerDock.Id;
                    worker.LastUpdateUtc = DateTime.UtcNow;
                    await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);

                    workerCaptain.CurrentMissionId = worker.Id;
                    workerCaptain.CurrentDockId = workerDock.Id;
                    await testDb.Driver.Captains.UpdateAsync(workerCaptain).ConfigureAwait(false);

                    Mission testEngineer = new Mission("[Test Engineer] Review branch handoff", "Write tests");
                    testEngineer.VesselId = vessel.Id;
                    testEngineer.VoyageId = voyage.Id;
                    testEngineer.Persona = "Test Engineer";
                    testEngineer.Status = MissionStatusEnum.Pending;
                    testEngineer.DependsOnMissionId = worker.Id;
                    testEngineer = await testDb.Driver.Missions.CreateAsync(testEngineer).ConfigureAwait(false);

                    missionService.OnGetMissionOutput = _ =>
                        "[ARMADA:RESULT] COMPLETE\nImplemented the branch-sensitive change with the required configuration guard and added coverage for the default and override branches. The change is committed and the suite passes locally.";

                    await missionService.HandleCompletionAsync(workerCaptain, worker.Id).ConfigureAwait(false);

                    Mission? completedWorker = await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false);
                    Mission? handedOffTest = await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false);
                    AssertNotNull(completedWorker, "Completed worker mission should remain readable");
                    AssertNotNull(handedOffTest, "Dependent test mission should remain readable");
                    AssertEqual(workerDock.BranchName, completedWorker!.BranchName, "Completion should backfill the mission branch from the dock");
                    AssertEqual(workerDock.BranchName, handedOffTest!.BranchName, "Downstream handoff should inherit the backfilled branch");
                    AssertTrue((handedOffTest.Description ?? String.Empty).Contains("Branch: " + workerDock.BranchName), "Handoff context should show the recovered branch");
                    AssertEqual(MissionStatusEnum.InProgress, handedOffTest.Status, "Dependent stage should dispatch once branch context is restored");
                }
            });
        }

        #region Private-Classes

        /// <summary>
        /// Git service stub that creates worktree directories on disk so that
        /// CLAUDE.md generation succeeds during TryAssignAsync integration tests.
        /// </summary>
        private class DirCreatingGitStub : IGitService
        {
            /// <summary>Call tracking for clone operations.</summary>
            public List<string> CloneCalls { get; } = new List<string>();

            /// <summary>Call tracking for worktree operations.</summary>
            public List<string> WorktreeCalls { get; } = new List<string>();

            /// <summary>Branch names used for worktree creation.</summary>
            public List<string> WorktreeBranches { get; } = new List<string>();

            /// <summary>Branch names deleted from the bare repository.</summary>
            public List<string> DeletedLocalBranches { get; } = new List<string>();

            /// <summary>Branch names deleted from the remote repository.</summary>
            public List<string> DeletedRemoteBranches { get; } = new List<string>();

            /// <summary>Optional hook invoked during worktree creation.</summary>
            public Func<Task>? OnCreateWorktreeAsync { get; set; }

            /// <inheritdoc />
            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default)
            {
                CloneCalls.Add(repoUrl + " -> " + localPath);
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", bool detached = false, CancellationToken token = default)
            {
                // Create the directory on disk so CLAUDE.md can be written
                Directory.CreateDirectory(worktreePath);
                WorktreeCalls.Add(worktreePath);
                WorktreeBranches.Add(branchName);
                return OnCreateWorktreeAsync?.Invoke() ?? Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task FetchAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default)
                => Task.FromResult("https://github.com/test/repo/pull/1");

            /// <inheritdoc />
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => Task.FromResult(true);

            /// <inheritdoc />
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
            {
                DeletedLocalBranches.Add(branchName);
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
            {
                DeletedRemoteBranches.Add(branchName);
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default) => Task.FromResult("refs/heads/main");

            /// <inheritdoc />
            public Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task PullAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult<string?>("main");

            /// <inheritdoc />
            public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> HasUncommittedTrackedChangesAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(false);

            /// <inheritdoc />
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default)
                => Task.FromResult("");

            /// <inheritdoc />
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => Task.FromResult(true);

            /// <inheritdoc />
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => Task.FromResult<string?>("abc123def456");

            /// <inheritdoc />
            /// <summary>
            /// Files the simulated captain changed since its dock was provisioned. A producing stage in
            /// these pipelines stands for a captain that committed, so one changed file is the default.
            /// </summary>
            public IReadOnlyList<string> ChangedFilesSinceResult { get; set; } = new string[] { "src/Simulated/Change.cs" };

            /// <inheritdoc />
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default)
                => Task.FromResult(ChangedFilesSinceResult);

            /// <inheritdoc />
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default)
                => Task.FromResult(branchName == "main" || WorktreeBranches.Contains(branchName));

            /// <inheritdoc />
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default)
                => BranchExistsAsync(repoPath, branchName, token);

            /// <inheritdoc />
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => Task.FromResult(false);

            /// <inheritdoc />
            public Task<IReadOnlyList<string>> GetConflictedFilesAsync(string worktreePath, CancellationToken token = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default) => Task.FromResult(0);

            /// <inheritdoc />
            public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default) => Task.CompletedTask;
        }

        #endregion

        private static async Task AddGreenVoyageCheckAsync(TestDatabase testDb, string voyageId)
        {
            CheckRun run = new CheckRun
            {
                VoyageId = voyageId,
                Label = "Build",
                Type = CheckRunTypeEnum.Build,
                Source = CheckRunSourceEnum.Armada,
                Status = CheckRunStatusEnum.Passed,
                Command = "dotnet build",
                WorkingDirectory = "C:/temp",
                ExitCode = 0,
                Output = "Build succeeded.",
                Summary = "check"
            };
            await testDb.Driver.CheckRuns.CreateAsync(run).ConfigureAwait(false);
        }
    }
}
