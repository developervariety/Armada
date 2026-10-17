namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using FixedResourcePressureProbe = global::Test.Shared.Infrastructure.FixedResourcePressureProbe;
    using SyslogLogging;

    /// <summary>
    /// Covers the lazy pipeline-handoff self-heal in <see cref="MissionService.TryAssignAsync"/>:
    /// a same-vessel dependent whose handoff-eligible upstream never propagated its branch (the
    /// rescue creation-order race) is repaired in-pass rather than parked at WaitingForDependency
    /// forever. Mirrors the direct TryAssignAsync harness used by the preferred-model routing tests.
    /// </summary>
    public sealed class MissionServiceSelfHealHandoffTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "MissionService SelfHeal Handoff";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            string id = Guid.NewGuid().ToString("N");
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_selfheal_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_selfheal_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_selfheal_logs_" + id);
            return settings;
        }

        private MissionService CreateMissionService(SqliteDatabaseDriver db, ArmadaSettings settings)
        {
            return CreateMissionService(db, settings, TestResourcePressure.Unconstrained(settings));
        }

        private MissionService CreateMissionService(
            SqliteDatabaseDriver db,
            ArmadaSettings settings,
            IResourcePressureAdmission resourcePressureAdmission)
        {
            LoggingModule logging = CreateLogging();
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64010);
            return new MissionService(logging, db, settings, dockService, captainService, resourcePressureAdmission: resourcePressureAdmission);
        }

        private async Task<Vessel> CreateVesselAsync(SqliteDatabaseDriver db, ArmadaSettings settings)
        {
            Vessel vessel = new Vessel("selfheal-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/selfheal.git");
            vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
            vessel.DefaultBranch = "main";
            return await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private async Task<Captain> CreateIdleCaptainAsync(SqliteDatabaseDriver db, string name, string model, string allowedPersonas)
        {
            Captain captain = new Captain(name);
            captain.Model = model;
            captain.AllowedPersonas = allowedPersonas;
            captain.State = CaptainStateEnum.Idle;
            return await db.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        private async Task<Mission> CreateUpstreamAsync(
            SqliteDatabaseDriver db,
            Vessel vessel,
            string persona,
            string branchName,
            MissionStatusEnum status = MissionStatusEnum.WorkProduced)
        {
            Mission mission = new Mission("Upstream " + persona, "Upstream work.");
            mission.VesselId = vessel.Id;
            mission.Persona = persona;
            mission.BranchName = branchName;
            mission.Status = status;
            mission.AgentOutput = persona + " implemented the fix.";
            return await db.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private async Task<Mission> CreateDependentAsync(
            SqliteDatabaseDriver db,
            Vessel vessel,
            string persona,
            string dependsOnMissionId,
            string description,
            string preferredModel = "high")
        {
            Mission mission = new Mission(persona + " stage", description);
            mission.VesselId = vessel.Id;
            mission.Persona = persona;
            mission.Status = MissionStatusEnum.Pending;
            mission.DependsOnMissionId = dependsOnMissionId;
            mission.PreferredModel = preferredModel;
            mission.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
            return await db.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        /// <summary>
        /// Wire an enabled definition-of-done gate, as the server does when the gate is on. The self-heal never runs
        /// the gate; it only reads the results the gate recorded.
        /// </summary>
        private void WireDefinitionOfDoneGate(MissionService missions, SqliteDatabaseDriver db)
        {
            missions.DefinitionOfDone = new DefinitionOfDoneGate(
                new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                db,
                CreateLogging());
        }

        /// <summary>
        /// A Worker whose agent finished and whose completion wrote WorkProduced, launched ten minutes ago. Whether its
        /// gate recorded a result is up to the caller.
        /// </summary>
        private async Task<Mission> CreateProducedWorkerAsync(SqliteDatabaseDriver db, Vessel vessel, string branchName)
        {
            Mission worker = await CreateUpstreamAsync(db, vessel, "Worker", branchName).ConfigureAwait(false);
            worker.StartedUtc = DateTime.UtcNow.AddMinutes(-10);
            return await db.Missions.UpdateAsync(worker).ConfigureAwait(false);
        }

        private async Task RecordGateResultAsync(SqliteDatabaseDriver db, Mission mission, DefinitionOfDoneResult result, DateTime recordedUtc)
        {
            DefinitionOfDoneEvaluationRecord record = DefinitionOfDoneEvaluationRecord.FromResult(result, recordedUtc.AddSeconds(-30));
            ArmadaEvent evt = new ArmadaEvent(DefinitionOfDoneEvaluationRecord.EventType, "Definition-of-done evaluation: " + record.Outcome);
            evt.EntityType = "mission";
            evt.EntityId = mission.Id;
            evt.MissionId = mission.Id;
            evt.VesselId = mission.VesselId;
            evt.Payload = System.Text.Json.JsonSerializer.Serialize(record);
            evt.CreatedUtc = recordedUtc;
            await db.Events.CreateAsync(evt).ConfigureAwait(false);
        }

        /// <summary>
        /// Builds a report-only chain in one voyage: a root Worker, a PlanReviewer and a TestEngineer that run in
        /// parallel after it, and a pending Judge that names the TestEngineer. A second root Worker of an unrelated
        /// chain shares the voyage. Every report is longer than the default preview, with a sentinel in its middle.
        /// </summary>
        private async Task<ReportOnlyChain> CreateReportOnlyChainAsync(SqliteDatabaseDriver db, Vessel vessel, MissionModeEnum judgeMode)
        {
            Voyage voyage = await db.Voyages.CreateAsync(new Voyage("report-only chain") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
            ReportOnlyChain chain = new ReportOnlyChain();

            chain.Worker = await CreateStageAsync(db, vessel, voyage, "Worker", 1, null, MissionStatusEnum.Complete,
                new string('w', 7000) + "\n| Family | Row |\n| --- | --- |\n| ROOT-MATRIX-MIDDLE-ROW | cited |\n" + new string('x', 7000)).ConfigureAwait(false);
            chain.OtherRoot = await CreateStageAsync(db, vessel, voyage, "Worker", 1, null, MissionStatusEnum.Complete,
                "OTHER-CHAIN-SENTINEL report.").ConfigureAwait(false);
            chain.PlanReviewer = await CreateStageAsync(db, vessel, voyage, "PlanReviewer", 2, chain.Worker.Id, MissionStatusEnum.Complete,
                "## Completeness\n" + new string('p', 9000) + "\n## Correctness\n" +
                "1. **Blocking. The matrix omits the PLAN-REVIEWER-FINDING row.** `Matrix.md:12` lists no source for it.\n" +
                "## Verdict\nNEEDS_REVISION\n").ConfigureAwait(false);
            chain.TestEngineer = await CreateStageAsync(db, vessel, voyage, "TestEngineer", 2, chain.Worker.Id, MissionStatusEnum.WorkProduced,
                new string('t', 6000) + "\nTEST-ENGINEER-MIDDLE-SENTINEL\n" + new string('u', 6000)).ConfigureAwait(false);

            Mission judge = new Mission("[Judge] report", "Review the report.");
            judge.VesselId = vessel.Id;
            judge.VoyageId = voyage.Id;
            judge.Persona = "Judge";
            judge.Mode = judgeMode;
            judge.StageOrder = 3;
            judge.Status = MissionStatusEnum.Pending;
            judge.DependsOnMissionId = chain.TestEngineer.Id;
            judge.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
            chain.Judge = await db.Missions.CreateAsync(judge).ConfigureAwait(false);
            return chain;
        }

        private async Task<Mission> CreateStageAsync(
            SqliteDatabaseDriver db,
            Vessel vessel,
            Voyage voyage,
            string persona,
            int stageOrder,
            string? dependsOn,
            MissionStatusEnum status,
            string output)
        {
            Mission mission = new Mission("[" + persona + "] report", "Report on the scope.");
            mission.VesselId = vessel.Id;
            mission.VoyageId = voyage.Id;
            mission.Persona = persona;
            mission.Mode = MissionModeEnum.Research;
            mission.StageOrder = stageOrder;
            mission.DependsOnMissionId = dependsOn;
            mission.BranchName = "armada/report-chain";
            mission.Status = status;
            mission.AgentOutput = output;
            mission.StartedUtc = DateTime.UtcNow.AddMinutes(-10);
            return await db.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        /// <summary>
        /// Builds a report-only chain that revises its report: a root Worker, a Linter that lists findings, a revision
        /// Worker, and a Judge. The stage named by <paramref name="pendingPersona"/> is the pending dependent, and every
        /// stage before it has run. A second root Worker of an unrelated chain shares the voyage.
        /// </summary>
        private async Task<Mission> CreateRevisedChainAsync(SqliteDatabaseDriver db, Vessel vessel, string pendingPersona)
        {
            Voyage voyage = await db.Voyages.CreateAsync(new Voyage("revised chain") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
            Mission root = await CreateStageAsync(db, vessel, voyage, "Worker", 1, null, MissionStatusEnum.Complete,
                "ROOT-REPORT-SENTINEL root report.").ConfigureAwait(false);
            await CreateStageAsync(db, vessel, voyage, "Worker", 1, null, MissionStatusEnum.Complete,
                "OTHER-CHAIN-SENTINEL report.").ConfigureAwait(false);
            bool revisionPending = pendingPersona == "Worker";
            Mission linter = await CreateStageAsync(db, vessel, voyage, "Linter", 2, root.Id,
                revisionPending ? MissionStatusEnum.WorkProduced : MissionStatusEnum.Complete,
                "## Findings\n1. **Blocking. LINTER-FINDING: the summary cites no path.**\n").ConfigureAwait(false);
            if (revisionPending) return await CreatePendingStageAsync(db, vessel, voyage, "Worker", 3, linter.Id).ConfigureAwait(false);

            Mission revision = await CreateStageAsync(db, vessel, voyage, "Worker", 3, linter.Id, MissionStatusEnum.WorkProduced,
                "REVISED-REPORT-SENTINEL revised report.").ConfigureAwait(false);
            return await CreatePendingStageAsync(db, vessel, voyage, "Judge", 4, revision.Id).ConfigureAwait(false);
        }

        private async Task<Mission> CreatePendingStageAsync(SqliteDatabaseDriver db, Vessel vessel, Voyage voyage, string persona, int stageOrder, string dependsOn)
        {
            Mission mission = new Mission("[" + persona + "] report", "Report on the scope.");
            mission.VesselId = vessel.Id;
            mission.VoyageId = voyage.Id;
            mission.Persona = persona;
            mission.Mode = MissionModeEnum.Research;
            mission.StageOrder = stageOrder;
            mission.Status = MissionStatusEnum.Pending;
            mission.DependsOnMissionId = dependsOn;
            mission.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
            return await db.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private sealed class ReportOnlyChain
        {
            public Mission Worker = null!;
            public Mission OtherRoot = null!;
            public Mission PlanReviewer = null!;
            public Mission TestEngineer = null!;
            public Mission Judge = null!;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("TryAssign_WorkerToTestEngineer_MissedHandoff_SelfHealsAndAssigns", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Mission worker = await CreateUpstreamAsync(testDb.Driver, vessel, "Worker", "armada/worker-fix").ConfigureAwait(false);
                    Mission testEngineer = await CreateDependentAsync(
                        testDb.Driver, vessel, "TestEngineer", worker.Id, "Original TestEngineer brief.").ConfigureAwait(false);
                    Captain captain = await CreateIdleCaptainAsync(testDb.Driver, "te-captain", "claude-opus-5", "[\"TestEngineer\"]").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "A missed Worker->TestEngineer handoff must self-heal and assign, not park forever.");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Self-healed dependent should be launched.");
                    AssertEqual(captain.Id, readBack.CaptainId, "Self-healed dependent should take the idle eligible captain.");
                    AssertEqual("armada/worker-fix", readBack.BranchName, "The upstream branch must be stamped onto the dependent.");
                    AssertContains("## Your Role: TestEngineer", readBack.Description ?? "", "The TestEngineer persona preamble must be injected.");
                    AssertContains("## Prior Stage Output", readBack.Description ?? "", "Prior-stage context must be injected.");
                    AssertContains("Branch: armada/worker-fix", readBack.Description ?? "", "The handoff context must carry the upstream branch.");
                    AssertContains("Original TestEngineer brief.", readBack.Description ?? "", "The dependent's original brief must be preserved.");
                    AssertContains("Worker implemented the fix.", readBack.Description ?? "", "The upstream agent output must be inlined.");
                }
            });

            await RunTest("TryAssign_LongReport_HandoffCarriesVerifiableArtifactReference", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Mission worker = await CreateUpstreamAsync(
                        testDb.Driver, vessel, "Worker", "armada/long-report").ConfigureAwait(false);
                    worker.AgentOutput = "HEAD-SENTINEL\n" + new string('m', 9000) + "\nTAIL-SENTINEL";
                    worker = await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);
                    Mission judge = await CreateDependentAsync(
                        testDb.Driver, vessel, "Judge", worker.Id, "Review the complete report.").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(judge, vessel).ConfigureAwait(false);
                    Mission readBack = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                    string description = readBack.Description ?? String.Empty;

                    AssertFalse(assigned, "No captain exists, but the handoff must still be prepared.");
                    AssertContains("HEAD-SENTINEL", description);
                    AssertContains("TAIL-SENTINEL", description);
                    AssertContains("bounded head/tail preview; middle omitted", description);
                    AssertContains("Authoritative safe artifact: mission-output:" + worker.Id, description);
                    AssertContains("UTF-8 SHA-256:", description);
                    AssertContains("Complete: True", description);
                    AssertContains("follow NextOffset until HasMore is false", description);
                    AssertFalse(description.Contains("/api/v1/missions/", StringComparison.Ordinal),
                        "The handoff must not offer a REST route a captain has no host or port for.");
                    AssertContains("digest mismatch is an evidence gap", description);
                    AssertFalse(description.Contains(new string('m', 8001), StringComparison.Ordinal),
                        "The handoff must not inline the complete oversized report.");
                }
            });

            // The verdict, blocking findings, follow-ups and added-tests list sit in the middle of a long
            // report, past both ends of the preview, and a large diff follows it. The next stage must
            // still receive them whole, with the complete-output reference, and keep them when the block
            // is compacted at the next hop.
            await RunTest("TryAssign_ReportWhoseEssentialsSitMidReport_HandoffCarriesThemPastPreviewAndDescriptionCaps", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Mission judge = await CreateUpstreamAsync(testDb.Driver, vessel, "Judge", "armada/essentials-report").ConfigureAwait(false);
                    string blocking = "2. **Blocking. The stressed price drops the captured slippage tick.** `Study.cs:504` prices from the bar open without the clerk.";
                    string followUp = "- Read the Missed, Expired and Stale counters in the report (`Report.cs:88`).";
                    judge.AgentOutput =
                        "## Completeness\n" + new string('c', 5000) + "\n" +
                        "## Correctness\n1. Verified the age rule.\n" + blocking + "\n" +
                        "## Suggested Follow-ups\n" + followUp + "\n" +
                        "## Verdict\nNEEDS_REVISION: one blocking defect.\n[ARMADA:VERDICT] NEEDS_REVISION\n" +
                        "## Tests\n" + new string('t', 5000) + "\n## Failure Modes\n" + new string('f', 5000);
                    judge.DiffSnapshot = "diff --git a/src/Study.cs b/src/Study.cs\n--- a/src/Study.cs\n+++ b/src/Study.cs\n@@ -1 +1,2 @@\n+" + new string('d', 50000) + "\n";
                    judge = await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);
                    Mission recorder = await CreateDependentAsync(testDb.Driver, vessel, "Recorder", judge.Id, "Record the voyage.").ConfigureAwait(false);

                    await missions.TryAssignAsync(recorder, vessel).ConfigureAwait(false);
                    string description = (await testDb.Driver.Missions.ReadAsync(recorder.Id).ConfigureAwait(false))!.Description ?? String.Empty;

                    AssertContains("bounded head/tail preview; middle omitted", description, "Precondition: the preview drops the middle of the report");
                    AssertContains(StageReportEssentials.Heading, description, "The essentials section survives the description cap");
                    AssertContains(blocking.Substring("2. ".Length), description, "The blocking finding body and anchor survive without duplicating its source number");
                    AssertContains(followUp, description, "The follow-up survives");
                    AssertContains("[ARMADA:VERDICT] NEEDS_REVISION", description, "The verdict survives");
                    AssertContains("Complete output: mission-output:" + judge.Id, description, "The complete-output reference survives");
                    AssertContains("UTF-8 SHA-256", description, "The digest survives");

                    string compacted = MissionService.CompactOlderHandoffBlocks(description);
                    AssertContains("## Prior Stage (compacted)", compacted, "Precondition: the block was compacted");
                    AssertContains(blocking.Substring("2. ".Length), compacted, "Compaction keeps the full blocking finding body and anchor");
                    AssertContains("Complete output: mission-output:" + judge.Id, compacted, "Compaction keeps the complete-output reference");
                    AssertFalse(compacted.Contains(new string('d', 1000), StringComparison.Ordinal), "Compaction still drops the diff");
                }
            });

            await RunTest("TryAssign_TestEngineerReport_HandoffCarriesTheAddedTestsList", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Mission tester = await CreateUpstreamAsync(testDb.Driver, vessel, "TestEngineer", "armada/added-tests").ConfigureAwait(false);
                    tester.AgentOutput = new string('n', 6000) + "\n## Tests added\n- StressedFill_KeepsClerkSlippage\n- FillBarPastAge_IsStale\n" + new string('z', 6000);
                    tester = await testDb.Driver.Missions.UpdateAsync(tester).ConfigureAwait(false);
                    Mission linter = await CreateDependentAsync(testDb.Driver, vessel, "Linter", tester.Id, "Lint the change.").ConfigureAwait(false);

                    await missions.TryAssignAsync(linter, vessel).ConfigureAwait(false);
                    string description = (await testDb.Driver.Missions.ReadAsync(linter.Id).ConfigureAwait(false))!.Description ?? String.Empty;
                    AssertContains("StressedFill_KeepsClerkSlippage", description, "The added-tests list survives the preview");
                    AssertContains("FillBarPastAge_IsStale", description);
                }
            });

            // In a report-only pipeline every reviewer judges the same root report. A Judge that names the
            // TestEngineer must still receive the root report whole, the findings of the parallel PlanReviewer,
            // and the prior stage's own report whole, and never a report from an unrelated chain.
            await RunTest("TryAssign_ReportOnlyJudge_ReceivesRootReportAndEveryEarlierReviewersFindings", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    ReportOnlyChain chain = await CreateReportOnlyChainAsync(testDb.Driver, vessel, MissionModeEnum.Research).ConfigureAwait(false);

                    await missions.TryAssignAsync(chain.Judge, vessel).ConfigureAwait(false);
                    string description = (await testDb.Driver.Missions.ReadAsync(chain.Judge.Id).ConfigureAwait(false))!.Description ?? String.Empty;

                    AssertContains("ROOT-MATRIX-MIDDLE-ROW", description, "The root report reaches the Judge whole, middle included");
                    AssertContains("Complete output: mission-output:" + chain.Worker.Id, description, "The root report's complete-output reference is pinned");
                    AssertContains("PLAN-REVIEWER-FINDING", description, "The parallel reviewer's blocking finding reaches the Judge");
                    AssertContains("TEST-ENGINEER-MIDDLE-SENTINEL", description, "The prior stage's report is inlined whole in a report-only pipeline");
                    AssertFalse(description.Contains("OTHER-CHAIN-SENTINEL", StringComparison.Ordinal), "An unrelated chain's root is never carried");
                    AssertEqual(1, CountOccurrences(description, "## Earlier Stage Reports"), "The earlier-stages section appears once");
                    AssertFalse(description.Contains("Revised Report", StringComparison.Ordinal), "A root Worker of another chain in the voyage does not make this a revision");

                    string compacted = MissionService.CompactOlderHandoffBlocks(description);
                    AssertContains("ROOT-MATRIX-MIDDLE-ROW", compacted, "Compacting handoff blocks leaves the earlier-stages section whole");
                }
            });

            await RunTest("TryAssign_ReportOnlyRevisionWorker_IsToldToReviseTheRootReport", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Mission revision = await CreateRevisedChainAsync(testDb.Driver, vessel, "Worker").ConfigureAwait(false);

                    await missions.TryAssignAsync(revision, vessel).ConfigureAwait(false);
                    string description = (await testDb.Driver.Missions.ReadAsync(revision.Id).ConfigureAwait(false))!.Description ?? String.Empty;

                    AssertContains("## Your Role: Worker (Revise the Report)", description, "A Worker after an earlier Worker revises the report");
                    AssertFalse(description.Contains("Investigate and Report", StringComparison.Ordinal), "The revision Worker is not told to write a new report");
                    AssertContains("This stage revises the root report", description, "The earlier-stages section names the revision");
                    AssertContains("ROOT-REPORT-SENTINEL", description, "The root report reaches the revision Worker");
                    AssertContains("LINTER-FINDING", description, "The reviewer's finding reaches the revision Worker");
                }
            });

            await RunTest("TryAssign_ReportOnlyJudgeAfterRevision_JudgesTheRevisedReport", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Mission judge = await CreateRevisedChainAsync(testDb.Driver, vessel, "Judge").ConfigureAwait(false);

                    await missions.TryAssignAsync(judge, vessel).ConfigureAwait(false);
                    string description = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!.Description ?? String.Empty;

                    AssertContains("## Your Role: Judge (Review the Revised Report)", description, "The Judge after a revision judges the revised report");
                    AssertContains("The prior stage revised the root report", description, "The earlier-stages section names the revised report as the deliverable");
                    AssertContains("REVISED-REPORT-SENTINEL", description, "The revised report reaches the Judge");
                    AssertContains("ROOT-REPORT-SENTINEL", description, "The root report reaches the Judge as the earlier version");
                    AssertContains("LINTER-FINDING", description, "The reviewer's finding reaches the Judge, so it can check the finding was addressed");
                    AssertFalse(description.Contains("OTHER-CHAIN-SENTINEL", StringComparison.Ordinal), "An unrelated chain's root is never carried");
                }
            });

            await RunTest("TryAssign_ImplementationJudge_KeepsSinglePredecessorHandoffAndDefaultPreview", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    ReportOnlyChain chain = await CreateReportOnlyChainAsync(testDb.Driver, vessel, MissionModeEnum.Implementation).ConfigureAwait(false);

                    await missions.TryAssignAsync(chain.Judge, vessel).ConfigureAwait(false);
                    string description = (await testDb.Driver.Missions.ReadAsync(chain.Judge.Id).ConfigureAwait(false))!.Description ?? String.Empty;

                    AssertFalse(description.Contains("## Earlier Stage Reports", StringComparison.Ordinal), "An implementation stage reviews the branch, not earlier reports");
                    AssertFalse(description.Contains("ROOT-MATRIX-MIDDLE-ROW", StringComparison.Ordinal), "No earlier report is carried");
                    AssertContains("bounded head/tail preview; middle omitted", description, "The prior stage's long report keeps the default preview");
                }
            });

            await RunTest("StageChainSection_RebuiltOnRepeat_AppearsOnceBeforeHandoffBlocks", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    ReportOnlyChain chain = await CreateReportOnlyChainAsync(testDb.Driver, vessel, MissionModeEnum.Research).ConfigureAwait(false);
                    List<Mission> voyageMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(chain.Judge.VoyageId!).ConfigureAwait(false);

                    string handoff = "\n\n---\n" + MissionService.BuildHandoffMarker(chain.TestEngineer.Id) + "\n## Prior Stage Output\nBranch: armada/report-chain\n";
                    string section = MissionService.BuildStageChainSection(chain.TestEngineer, chain.Judge, voyageMissions);
                    string once = MissionService.InsertStageChainSection("Base brief." + handoff, section);
                    string twice = MissionService.InsertStageChainSection(MissionService.RemoveStageChainSection(once), section);

                    AssertEqual(once, twice, "Rebuilding the section replaces it rather than adding a copy");
                    AssertEqual(1, CountOccurrences(twice, "## Earlier Stage Reports"), "The section appears once");
                    AssertTrue(twice.IndexOf("## Earlier Stage Reports", StringComparison.Ordinal) < twice.IndexOf(MissionService.BuildHandoffMarker(chain.TestEngineer.Id), StringComparison.Ordinal),
                        "The section sits before the handoff blocks");
                    AssertEqual("Base brief." + handoff, MissionService.RemoveStageChainSection(twice), "Removing the section restores the description exactly");
                    AssertContains("ROOT-MATRIX-MIDDLE-ROW", MissionService.StripHandoffBlock(twice, chain.TestEngineer.Id), "Stripping a handoff block leaves the section");

                    List<Mission> earlier = MissionService.CollectEarlierStages(chain.TestEngineer, chain.Judge, voyageMissions);
                    AssertEqual(2, earlier.Count, "Root and the parallel reviewer, not the unrelated root");
                    AssertEqual(chain.Worker.Id, earlier[0].Id, "The root comes first");
                    AssertEqual(chain.PlanReviewer.Id, earlier[1].Id, "Then the parallel reviewer");
                }
            });

            await RunTest("TryAssign_LongReportPreview_DoesNotSplitUnicodeScalars", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Mission worker = await CreateUpstreamAsync(testDb.Driver, vessel, "Worker", "armada/unicode-report").ConfigureAwait(false);
                    worker.AgentOutput = new string('h', 3399) + "🙂" + new string('m', 2202) + "🙂" + new string('t', 3399);
                    worker = await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);
                    Mission judge = await CreateDependentAsync(testDb.Driver, vessel, "Judge", worker.Id, "Review Unicode.").ConfigureAwait(false);

                    await missions.TryAssignAsync(judge, vessel).ConfigureAwait(false);
                    string description = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!.Description ?? String.Empty;
                    for (int i = 0; i < description.Length; i++)
                    {
                        if (Char.IsHighSurrogate(description[i]))
                        {
                            AssertTrue(i + 1 < description.Length && Char.IsLowSurrogate(description[i + 1]), "Preview has an unpaired high surrogate.");
                            i++;
                        }
                        else
                        {
                            AssertFalse(Char.IsLowSurrogate(description[i]), "Preview has an unpaired low surrogate.");
                        }
                    }
                }
            });

            await RunTest("TryAssign_TestEngineerToJudge_MissedHandoff_SelfHealsGenerically", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Mission testEngineer = await CreateUpstreamAsync(testDb.Driver, vessel, "TestEngineer", "armada/te-stage").ConfigureAwait(false);
                    Mission judge = await CreateDependentAsync(
                        testDb.Driver, vessel, "Judge", testEngineer.Id, "Original Judge brief.").ConfigureAwait(false);
                    Captain captain = await CreateIdleCaptainAsync(testDb.Driver, "judge-captain", "claude-opus-5", "[\"Judge\"]").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(judge, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "The TestEngineer->Judge handoff must self-heal generically too.");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Self-healed Judge should be launched.");
                    AssertEqual(captain.Id, readBack.CaptainId, "Self-healed Judge should take the idle eligible captain.");
                    AssertEqual("armada/te-stage", readBack.BranchName, "The upstream branch must be stamped onto the Judge.");
                    AssertContains("## Your Role: Judge (Review)", readBack.Description ?? "", "The Judge persona preamble must be injected.");
                }
            });

            await RunTest("TryAssign_MissedHandoff_NoEligibleCaptain_SelfHealIsIdempotent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Mission worker = await CreateUpstreamAsync(testDb.Driver, vessel, "Worker", "armada/worker-idem").ConfigureAwait(false);
                    Mission testEngineer = await CreateDependentAsync(
                        testDb.Driver, vessel, "TestEngineer", worker.Id, "Original brief.").ConfigureAwait(false);
                    // No captain exists, so assignment cannot complete after the self-heal.

                    bool firstAssigned = await missions.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);
                    Mission afterFirst = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;

                    AssertFalse(firstAssigned, "With no captain the dependent cannot assign even after self-heal.");
                    AssertEqual(MissionStatusEnum.Pending, afterFirst.Status, "Unassigned dependent stays Pending.");
                    AssertEqual("armada/worker-idem", afterFirst.BranchName, "The branch must still be stamped by the self-heal.");
                    AssertEqual(1, CountOccurrences(afterFirst.Description ?? "", "## Your Role: TestEngineer"),
                        "The persona preamble must be injected exactly once.");

                    // Second pass: the handoff is now prepared, so the self-heal must not re-run and
                    // must not double-inject the preamble/context.
                    bool secondAssigned = await missions.TryAssignAsync(afterFirst, vessel).ConfigureAwait(false);
                    Mission afterSecond = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;

                    AssertFalse(secondAssigned, "Still no captain, so the second pass also cannot assign.");
                    AssertEqual(1, CountOccurrences(afterSecond.Description ?? "", "## Your Role: TestEngineer"),
                        "A second assignment pass must not re-run the handoff or double-inject context.");
                    AssertEqual(1, CountOccurrences(afterSecond.Description ?? "", "## Prior Stage Output"),
                        "Prior-stage context must not be duplicated on a re-attempt.");
                }
            });

            await RunTest("TryAssign_ArchitectDependency_NotPrepared_StillDefers", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    // An Architect upstream's handoff is the parse-and-materialize path, which cannot
                    // be reconstructed lazily; the dependent must keep deferring (no branch stamp).
                    Mission architect = await CreateUpstreamAsync(testDb.Driver, vessel, "Architect", "armada/architect-plan").ConfigureAwait(false);
                    Mission worker = await CreateDependentAsync(
                        testDb.Driver, vessel, "Worker", architect.Id, "Original worker brief.").ConfigureAwait(false);
                    // An eligible idle captain is present, so a defer can only be due to the architect gate.
                    await CreateIdleCaptainAsync(testDb.Driver, "worker-captain", "claude-opus-5", "[\"Worker\"]").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(worker, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false);
                    AssertFalse(assigned, "An unprepared Architect dependency must still defer, not lazily self-heal.");
                    AssertEqual(MissionStatusEnum.Pending, readBack!.Status, "Deferred dependent stays Pending.");
                    AssertEqual(MissionAssignmentStateEnum.WaitingForDependency, readBack.AssignmentState, "Architect-gated dependent waits for the batch handoff.");
                    AssertNull(readBack.BranchName, "The Architect path must not stamp a branch.");
                    AssertFalse((readBack.Description ?? "").Contains("## Your Role: Worker", StringComparison.Ordinal),
                        "No persona preamble should be injected while deferring on the Architect handoff.");
                }
            });

            await RunTest("TryAssign_SweepInsideCompletionGate_DefersUntilGateClears", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Mission worker = await CreateUpstreamAsync(testDb.Driver, vessel, "Worker", "armada/worker-race").ConfigureAwait(false);
                    Mission testEngineer = await CreateDependentAsync(
                        testDb.Driver, vessel, "TestEngineer", worker.Id, "Original TestEngineer brief.").ConfigureAwait(false);
                    await CreateIdleCaptainAsync(testDb.Driver, "te-captain", "claude-opus-5", "[\"TestEngineer\"]").ConfigureAwait(false);

                    // Simulate the DoD gate still running: the upstream's completion is in the
                    // in-flight gate. Without the defer, the sweep tick would self-heal here and
                    // race the batch handoff that runs after the gate -- producing the double
                    // "pipeline handoff: prepared mission" the objective measured.
                    missions.InFlightCompletionsForTests.TryAdd(worker.Id, new MissionService.CompletionGuardEntry(Task.CompletedTask, null));

                    bool firstAssigned = await missions.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);
                    Mission afterFirst = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;

                    AssertFalse(firstAssigned, "A sweep tick inside the completion gate must defer, not self-heal.");
                    AssertEqual(MissionStatusEnum.Pending, afterFirst.Status, "Deferred dependent stays Pending.");
                    AssertEqual(MissionAssignmentStateEnum.WaitingForDependency, afterFirst.AssignmentState,
                        "The dependent parks on WaitingForDependency while the completion gate is open.");
                    AssertNull(afterFirst.BranchName,
                        "The branch must NOT be stamped during the gate window -- the batch handoff owns that.");
                    AssertFalse((afterFirst.Description ?? "").Contains("## Your Role: TestEngineer", StringComparison.Ordinal),
                        "No persona preamble should be injected while deferring on the in-flight gate.");
                    AssertFalse((afterFirst.Description ?? "").Contains("## Prior Stage Output", StringComparison.Ordinal),
                        "No prior-stage context should be injected while deferring on the in-flight gate.");

                    // Now the gate clears (simulating the DoD gate finishing and the batch handoff
                    // running). The next sweep tick should self-heal cleanly.
                    missions.InFlightCompletionsForTests.TryRemove(worker.Id, out _);

                    bool secondAssigned = await missions.TryAssignAsync(afterFirst, vessel).ConfigureAwait(false);
                    Mission afterSecond = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;

                    AssertTrue(secondAssigned, "Once the gate clears, the next sweep tick must self-heal and assign.");
                    AssertEqual(MissionStatusEnum.InProgress, afterSecond.Status, "Self-healed dependent launches normally.");
                    AssertEqual("armada/worker-race", afterSecond.BranchName, "The upstream branch is stamped after the gate clears.");
                    AssertContains("## Your Role: TestEngineer", afterSecond.Description ?? "",
                        "The TestEngineer persona preamble is injected exactly once on the post-gate pass.");
                    AssertEqual(1, CountOccurrences(afterSecond.Description ?? "", "## Prior Stage Output"),
                        "Prior-stage context is injected exactly once -- no double-prepare from the race.");
                }
            });

            await RunTest("TryAssign_MissedHandoff_MemoryBelowAdmissionFloor_HealsThenWaitsForResourcePressure", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    FixedResourcePressureProbe probe = new FixedResourcePressureProbe(1024L * 1024L);
                    MissionService missions = CreateMissionService(
                        testDb.Driver, settings, TestResourcePressure.WithAvailableMemory(settings, probe));
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Mission worker = await CreateUpstreamAsync(testDb.Driver, vessel, "Worker", "armada/worker-pressure").ConfigureAwait(false);
                    Mission testEngineer = await CreateDependentAsync(
                        testDb.Driver, vessel, "TestEngineer", worker.Id, "Original TestEngineer brief.").ConfigureAwait(false);
                    Captain captain = await CreateIdleCaptainAsync(testDb.Driver, "te-captain", "claude-opus-5", "[\"TestEngineer\"]").ConfigureAwait(false);

                    bool firstAssigned = await missions.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);
                    Mission afterFirst = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;
                    Captain captainAfterFirst = (await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;

                    AssertTrue(probe.ProbeCount > 0, "The admission gate must consult the memory probe.");
                    AssertFalse(firstAssigned, "Memory below the admission floor must defer the launch.");
                    AssertEqual(MissionStatusEnum.Pending, afterFirst.Status, "A memory-deferred dependent stays Pending.");
                    AssertEqual(MissionAssignmentStateEnum.WaitingForResourcePressure, afterFirst.AssignmentState,
                        "The deferral must name resource pressure, not an idle-captain or dependency wait.");
                    AssertNull(afterFirst.CaptainId, "A memory-deferred dependent must not hold a captain.");
                    AssertEqual(CaptainStateEnum.Idle, captainAfterFirst.State, "The eligible captain stays Idle.");
                    AssertEqual("armada/worker-pressure", afterFirst.BranchName,
                        "The handoff self-heal runs before admission, so the branch is already stamped.");
                    AssertEqual(1, CountOccurrences(afterFirst.Description ?? "", "## Your Role: TestEngineer"),
                        "The persona preamble is injected once by the self-heal.");

                    probe.AvailableMemoryBytes = TestResourcePressure.AmpleAvailableMemoryBytes;

                    bool secondAssigned = await missions.TryAssignAsync(afterFirst, vessel).ConfigureAwait(false);
                    Mission afterSecond = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;

                    AssertTrue(secondAssigned, "Once memory returns, the next pass must assign.");
                    AssertEqual(MissionStatusEnum.InProgress, afterSecond.Status, "The dependent launches after pressure clears.");
                    AssertEqual(captain.Id, afterSecond.CaptainId, "The dependent takes the idle eligible captain.");
                    AssertEqual(1, CountOccurrences(afterSecond.Description ?? "", "## Your Role: TestEngineer"),
                        "The retried pass must not re-run the handoff or double-inject the preamble.");
                    AssertEqual(1, CountOccurrences(afterSecond.Description ?? "", "## Prior Stage Output"),
                        "Prior-stage context stays injected exactly once across the deferral.");
                }
            });

            // An admiral restart while a completion waits for the host-wide gate slot leaves the upstream at
            // WorkProduced with no gate result and no in-flight completion entry. The self-heal must not read that
            // as a verified stage.
            await RunTest("TryAssign_UpstreamGateNeverRecorded_HoldsUpstreamInsteadOfHandingOff", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    WireDefinitionOfDoneGate(missions, testDb.Driver);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Mission worker = await CreateProducedWorkerAsync(testDb.Driver, vessel, "armada/worker-interrupted").ConfigureAwait(false);
                    Mission testEngineer = await CreateDependentAsync(
                        testDb.Driver, vessel, "TestEngineer", worker.Id, "Original TestEngineer brief.").ConfigureAwait(false);
                    await CreateIdleCaptainAsync(testDb.Driver, "te-captain", "claude-opus-5", "[\"TestEngineer\"]").ConfigureAwait(false);

                    bool firstAssigned = await missions.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);
                    Mission afterFirst = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;
                    Mission upstream = (await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!;

                    AssertFalse(firstAssigned, "A dependent of a stage whose gate never recorded a result must not be assigned.");
                    AssertEqual(MissionStatusEnum.Pending, afterFirst.Status, "The dependent stays Pending.");
                    AssertEqual(MissionAssignmentStateEnum.WaitingForDependency, afterFirst.AssignmentState,
                        "The dependent waits on its unverified dependency.");
                    AssertNull(afterFirst.BranchName, "The unverified upstream branch must not be handed off.");
                    AssertFalse((afterFirst.Description ?? "").Contains("## Prior Stage Output", StringComparison.Ordinal),
                        "No prior-stage context may be injected from an unverified stage.");
                    AssertEqual(MissionStatusEnum.WorkProduced, upstream.Status, "The upstream keeps its status.");
                    AssertTrue(upstream.HeldForOperatorReview, "The unverified upstream is held for the operator, not left waiting silently.");
                    AssertContains("definition_of_done_not_run", upstream.HeldForOperatorReviewReason ?? "",
                        "The hold names the missing definition-of-done result.");

                    bool secondAssigned = await missions.TryAssignAsync(afterFirst, vessel).ConfigureAwait(false);
                    Mission afterSecond = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;
                    AssertFalse(secondAssigned, "A later sweep pass must not hand off the held upstream either.");
                    AssertNull(afterSecond.BranchName, "The held upstream branch is still not handed off.");
                }
            });

            await RunTest("TryAssign_UpstreamGateResultFromEarlierLaunch_HoldsUpstream", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    WireDefinitionOfDoneGate(missions, testDb.Driver);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Mission worker = await CreateProducedWorkerAsync(testDb.Driver, vessel, "armada/worker-relaunched").ConfigureAwait(false);
                    // The pass belongs to a launch that ended before the current one started.
                    await RecordGateResultAsync(testDb.Driver, worker, DefinitionOfDoneResult.Pass(), worker.StartedUtc!.Value.AddHours(-1)).ConfigureAwait(false);
                    Mission testEngineer = await CreateDependentAsync(
                        testDb.Driver, vessel, "TestEngineer", worker.Id, "Original TestEngineer brief.").ConfigureAwait(false);
                    await CreateIdleCaptainAsync(testDb.Driver, "te-captain", "claude-opus-5", "[\"TestEngineer\"]").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);
                    Mission readBack = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;
                    Mission upstream = (await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!;

                    AssertFalse(assigned, "A pass recorded for an earlier launch does not verify the current one.");
                    AssertNull(readBack.BranchName, "The current launch's branch must not be handed off.");
                    AssertTrue(upstream.HeldForOperatorReview, "The upstream is held for the operator.");
                }
            });

            await RunTest("InterruptedGate_RerunPasses_ReleasesHoldAndHandsOff", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    WireDefinitionOfDoneGate(missions, testDb.Driver);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                    {
                        Name = "Passing Profile",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = "exit 0",
                        UnitTestCommand = "exit 0",
                        IsDefault = true,
                        Active = true
                    }).ConfigureAwait(false);
                    Mission worker = await CreateProducedWorkerAsync(testDb.Driver, vessel, "armada/worker-rerun").ConfigureAwait(false);
                    string worktree = Path.Combine(Path.GetTempPath(), "armada_rerun_dock_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(worktree);
                    try
                    {
                        Dock dock = new Dock(vessel.Id);
                        dock.WorktreePath = worktree;
                        dock.BranchName = "armada/worker-rerun";
                        dock = await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("rerun voyage") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                        worker.DockId = dock.Id;
                        worker.VoyageId = voyage.Id;
                        worker = await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);

                        Mission testEngineer = await CreateDependentAsync(
                            testDb.Driver, vessel, "TestEngineer", worker.Id, "Original TestEngineer brief.").ConfigureAwait(false);
                        testEngineer.VoyageId = voyage.Id;
                        testEngineer = await testDb.Driver.Missions.UpdateAsync(testEngineer).ConfigureAwait(false);
                        await missions.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);
                        Mission held = (await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!;
                        AssertTrue(MissionService.IsDefinitionOfDoneNotRunHold(held), "The interrupted gate holds the upstream first");

                        DefinitionOfDoneEvaluationOutcomeEnum? outcome = await missions.RerunInterruptedDefinitionOfDoneGateAsync(worker.Id, CancellationToken.None).ConfigureAwait(false);
                        AssertEqual(DefinitionOfDoneEvaluationOutcomeEnum.Passed, outcome, "The gate ran again and passed");

                        Mission released = (await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!;
                        AssertFalse(released.HeldForOperatorReview, "A gate that passes on the re-run releases the hold: " + released.HeldForOperatorReviewReason);

                        Mission dependent = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;
                        AssertContains(MissionService.BuildHandoffMarker(worker.Id), dependent.Description ?? "", "The released upstream is handed off to its dependent");
                    }
                    finally
                    {
                        try { Directory.Delete(worktree, true); } catch { }
                    }
                }
            });

            await RunTest("InterruptedGate_RerunFails_FailsTheMissionAndCancelsItsDependent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    WireDefinitionOfDoneGate(missions, testDb.Driver);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                    {
                        Name = "Failing Profile",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = "exit 0",
                        UnitTestCommand = "exit 1",
                        IsDefault = true,
                        Active = true
                    }).ConfigureAwait(false);
                    Mission worker = await CreateProducedWorkerAsync(testDb.Driver, vessel, "armada/worker-rerun-red").ConfigureAwait(false);
                    string worktree = Path.Combine(Path.GetTempPath(), "armada_rerun_red_dock_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(worktree);
                    try
                    {
                        Dock dock = new Dock(vessel.Id);
                        dock.WorktreePath = worktree;
                        dock.BranchName = "armada/worker-rerun-red";
                        dock = await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("rerun voyage") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                        worker.DockId = dock.Id;
                        worker.VoyageId = voyage.Id;
                        worker = await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);

                        Mission testEngineer = await CreateDependentAsync(
                            testDb.Driver, vessel, "TestEngineer", worker.Id, "Original TestEngineer brief.").ConfigureAwait(false);
                        testEngineer.VoyageId = voyage.Id;
                        testEngineer = await testDb.Driver.Missions.UpdateAsync(testEngineer).ConfigureAwait(false);
                        await missions.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);
                        Mission held = (await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!;
                        AssertTrue(MissionService.IsDefinitionOfDoneNotRunHold(held), "The interrupted gate holds the upstream first");

                        DefinitionOfDoneEvaluationOutcomeEnum? outcome = await missions.RerunInterruptedDefinitionOfDoneGateAsync(worker.Id, CancellationToken.None).ConfigureAwait(false);
                        AssertEqual(DefinitionOfDoneEvaluationOutcomeEnum.Failed, outcome, "The gate ran again and failed");

                        Mission failed = (await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.Failed, failed.Status, "A gate that fails on the re-run fails the mission so recovery takes it");
                        AssertFalse(failed.HeldForOperatorReview, "The failed mission is no longer held");
                        AssertStartsWith("DoD gate failed", failed.FailureReason ?? "", "The failure carries the gate's reason");

                        Mission dependent = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.Cancelled, dependent.Status, "The dependent of a failed upstream is cancelled, not handed unverified work");
                    }
                    finally
                    {
                        try { Directory.Delete(worktree, true); } catch { }
                    }
                }
            });

            await RunTest("InterruptedGate_SweepStartsOneRerunPerHeldMission_AndADocklessMissionStaysHeld", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    WireDefinitionOfDoneGate(missions, testDb.Driver);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    AssertEqual(0, await missions.StartInterruptedDefinitionOfDoneGateRerunsAsync().ConfigureAwait(false), "Nothing is held, so nothing re-runs");

                    Mission worker = await CreateProducedWorkerAsync(testDb.Driver, vessel, "armada/worker-dockless").ConfigureAwait(false);
                    Mission testEngineer = await CreateDependentAsync(
                        testDb.Driver, vessel, "TestEngineer", worker.Id, "Original TestEngineer brief.").ConfigureAwait(false);
                    await missions.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);

                    AssertEqual(1, await missions.StartInterruptedDefinitionOfDoneGateRerunsAsync().ConfigureAwait(false), "The held mission gets one re-run");
                    for (int i = 0; i < 100 && MissionService.GateRerunsInFlight().Contains(worker.Id); i++)
                        await Task.Delay(50).ConfigureAwait(false);
                    AssertFalse(MissionService.GateRerunsInFlight().Contains(worker.Id), "The re-run finishes");

                    Mission stillHeld = (await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!;
                    AssertTrue(MissionService.IsDefinitionOfDoneNotRunHold(stillHeld), "A mission with no dock cannot be re-gated and stays held");
                    string activity = File.ReadAllText(Path.Combine(settings.LogDirectory, "missions", worker.Id + ".log"));
                    AssertContains("the mission dock is gone", activity, "The mission says why the gate was not re-run");
                    AssertEqual(0, await missions.StartInterruptedDefinitionOfDoneGateRerunsAsync().ConfigureAwait(false),
                        "A mission whose gate cannot be re-run is reported once, not retried every sweep");
                }
            });

            await RunTest("TryAssign_UpstreamGatePassedThisLaunch_SelfHealsAndAssigns", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    WireDefinitionOfDoneGate(missions, testDb.Driver);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Mission worker = await CreateProducedWorkerAsync(testDb.Driver, vessel, "armada/worker-verified").ConfigureAwait(false);
                    await RecordGateResultAsync(testDb.Driver, worker, DefinitionOfDoneResult.Pass(), DateTime.UtcNow.AddMinutes(-1)).ConfigureAwait(false);
                    Mission testEngineer = await CreateDependentAsync(
                        testDb.Driver, vessel, "TestEngineer", worker.Id, "Original TestEngineer brief.").ConfigureAwait(false);
                    Captain captain = await CreateIdleCaptainAsync(testDb.Driver, "te-captain", "claude-opus-5", "[\"TestEngineer\"]").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);
                    Mission readBack = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;
                    Mission upstream = (await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!;

                    AssertTrue(assigned, "A stage whose gate passed on this launch is handed off by the self-heal.");
                    AssertEqual(captain.Id, readBack.CaptainId, "The dependent takes the idle eligible captain.");
                    AssertEqual("armada/worker-verified", readBack.BranchName, "The verified branch is handed off.");
                    AssertFalse(upstream.HeldForOperatorReview, "A verified upstream is not held.");
                }
            });

            await RunTest("RecoverDanglingHandoffs_UpstreamGateNeverRecorded_HoldsInsteadOfRedriving", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    WireDefinitionOfDoneGate(missions, testDb.Driver);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Interrupted gate voyage")).ConfigureAwait(false);
                    Mission worker = await CreateProducedWorkerAsync(testDb.Driver, vessel, "armada/worker-dangling").ConfigureAwait(false);
                    worker.VoyageId = voyage.Id;
                    worker = await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);
                    Mission testEngineer = await CreateDependentAsync(
                        testDb.Driver, vessel, "TestEngineer", worker.Id, "Original TestEngineer brief.").ConfigureAwait(false);
                    testEngineer.VoyageId = voyage.Id;
                    await testDb.Driver.Missions.UpdateAsync(testEngineer).ConfigureAwait(false);

                    int redriven = await missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                    Mission readBack = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;
                    Mission upstream = (await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!;

                    AssertEqual(0, redriven, "A stage with no gate result is not re-driven.");
                    AssertNull(readBack.BranchName, "The unverified branch is not handed off.");
                    AssertTrue(upstream.HeldForOperatorReview, "The unverified upstream is held for the operator.");
                }
            });
        }
    }
}
