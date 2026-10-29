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

        private static string CreateVersionedArchitectPlan(bool includeSecondBranch, bool singleIntegration)
        {
            VersionedArchitectPlan plan = new VersionedArchitectPlan
            {
                ContractVersion = 2,
                Requirements = new List<string> { "REQ-1" },
                Decisions = new List<string> { "Keep routing defaults" }
            };
            if (singleIntegration)
            {
                plan.Missions.Add(CreateVersionedPlanMission("Solo integration", "integration", "Join existing work.", null, true, "AC-1"));
            }
            else
            {
                plan.Missions.Add(CreateVersionedPlanMission("Branch A", "implementation", "Implement branch A.", null, false, "AC-1"));
                if (includeSecondBranch)
                    plan.Missions.Add(CreateVersionedPlanMission("Branch B", "implementation", "Implement branch B.", null, false, "AC-2"));
                plan.Missions.Add(CreateVersionedPlanMission("Final integration", "integration", "Join the branches.", null, true,
                    includeSecondBranch ? "AC-3" : "AC-2"));
            }
            return "```armada-plan\n" + System.Text.Json.JsonSerializer.Serialize(plan) + "\n```";
        }

        private static VersionedArchitectPlanMission CreateVersionedPlanMission(
            string title, string kind, string description, int? dependsOn, bool waitForOtherMissions, string criterionId)
        {
            return new VersionedArchitectPlanMission
            {
                Title = title,
                Kind = kind,
                Description = description,
                DependsOn = dependsOn,
                WaitForOtherMissions = waitForOtherMissions,
                ValidationCommands = new List<string> { "dotnet test" },
                AcceptanceCriteria = new List<VersionedArchitectAcceptanceCriterion>
                {
                    new VersionedArchitectAcceptanceCriterion
                    {
                        Id = criterionId,
                        RequirementIds = new List<string> { "REQ-1" },
                        Scenario = "Run the mission check.",
                        Expected = "The check passes.",
                        Verification = "The named test passes.",
                        NegativeCases = new List<string> { "Invalid input fails." }
                    }
                }
            };
        }

        private static string GetVersionedSliceBrief(string planOutput, int index)
        {
            ArchitectParseResult parsed = new ArchitectOutputParser().Parse(planOutput);
            return MissionService.ArchitectDerivedBriefPreamble + "\n\n" + parsed.Missions[index].Description;
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
                    AssertContains("`_briefing/handoff/" + worker.Id + ".md` in your checkout", description,
                        "the handoff names the dock file that holds the complete report");
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

            // A handoff longer than the description budget is cut in the record, but the next stage must still
            // read every line: the complete text is staged into its dock and the cut marker names that file.
            await RunTest("TryAssign_HandoffPastTheDescriptionBudget_StagesTheCompleteTextTheMarkerNames", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Mission worker = await CreateUpstreamAsync(testDb.Driver, vessel, "Worker", "armada/long-diff").ConfigureAwait(false);
                    System.Text.StringBuilder diff = new System.Text.StringBuilder();
                    for (int file = 0; file < 40; file++)
                    {
                        diff.Append("diff --git a/src/F" + file + ".cs b/src/F" + file + ".cs\n--- a/src/F" + file + ".cs\n+++ b/src/F" + file + ".cs\n@@ -1 +1,12 @@\n");
                        for (int line = 0; line < 12; line++) diff.Append("+DIFF-LINE-" + file + "-" + line + " keeps the reviewed change readable\n");
                    }
                    worker.DiffSnapshot = diff.ToString();
                    worker = await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);
                    Mission judge = await CreateDependentAsync(testDb.Driver, vessel, "Judge", worker.Id, "BASE-BRIEF-SENTINEL review the change.").ConfigureAwait(false);

                    await missions.TryAssignAsync(judge, vessel).ConfigureAwait(false);
                    Mission readBack = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                    string description = readBack.Description ?? String.Empty;

                    AssertTrue(worker.DiffSnapshot.Length > 20000, "Precondition: the diff alone is past the description budget");
                    AssertFalse(description.Contains("+DIFF-LINE-20-6 ", StringComparison.Ordinal), "Precondition: the record holds the cut copy");
                    AssertTrue(description.Length <= 20000 + 2000, "The record copy stays at the description budget");
                    AssertContains("full text: `" + MissionService.HandoffFullTextFirstFile + "`", description, "The cut marker names the dock file of the complete text");

                    string kept = await File.ReadAllTextAsync(MissionService.FullHandoffStorePath(settings.LogDirectory, judge.Id)).ConfigureAwait(false);
                    AssertContains("BASE-BRIEF-SENTINEL review the change.", kept, "The complete text holds the base brief");
                    AssertContains(worker.DiffSnapshot.TrimEnd('\n'), kept, "The complete text holds the whole diff");

                    string dock = Path.Combine(Path.GetTempPath(), "armada_full_handoff_dock_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dock);
                    try
                    {
                        await missions.GenerateClaudeMdAsync(dock, readBack, vessel).ConfigureAwait(false);
                        string first = Path.Combine(dock, MissionService.HandoffFullTextFirstFile.Replace('/', Path.DirectorySeparatorChar));
                        AssertTrue(File.Exists(first), "The file the marker names is staged in the dock");

                        string folder = Path.Combine(dock, MissionService.HandoffFullTextFolder.Replace('/', Path.DirectorySeparatorChar));
                        System.Text.StringBuilder joined = new System.Text.StringBuilder();
                        foreach (string part in Directory.GetFiles(folder, "*.md").OrderBy(p => p, StringComparer.Ordinal))
                        {
                            string content = await File.ReadAllTextAsync(part).ConfigureAwait(false);
                            joined.Append(content.Substring(content.IndexOf("\n\n", StringComparison.Ordinal) + 2));
                        }
                        AssertEqual(kept, joined.ToString(), "The staged files, joined in order, equal the complete handoff text");

                        string brief = await File.ReadAllTextAsync(Path.Combine(dock, "CLAUDE.md")).ConfigureAwait(false);
                        AssertContains("`" + MissionService.HandoffFullTextFirstFile + "`", brief, "The brief names the staged complete text");
                        AssertFalse(brief.Contains("+DIFF-LINE-20-6 ", StringComparison.Ordinal), "The brief keeps its cut, so its size is unchanged");
                    }
                    finally
                    {
                        try { Directory.Delete(dock, true); } catch { }
                    }
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
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("operator-note-reprepare")
                    {
                        Status = VoyageStatusEnum.InProgress
                    }).ConfigureAwait(false);
                    worker.VoyageId = voyage.Id;
                    worker = await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);
                    testEngineer.VoyageId = voyage.Id;
                    testEngineer = await testDb.Driver.Missions.UpdateAsync(testEngineer).ConfigureAwait(false);
                    Armada.Server.CoordinationService board = new Armada.Server.CoordinationService(CreateLogging(), testDb.Driver);
                    await board.PostMessageAsync(Armada.Server.CoordinationService.DefaultRoomKey,
                        CoordinationAuthorTypeEnum.Operator, "operator", "Operator", "OWNED-BOARD-NOTE", voyageId: voyage.Id).ConfigureAwait(false);
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

                    string requirement = "\nopaque operator requirement: retain both deadline assertions  \n";
                    afterSecond.Description += requirement;
                    afterSecond.BranchName = null; // Simulate a missed handoff, not an already prepared retry.
                    afterSecond = await testDb.Driver.Missions.UpdateAsync(afterSecond).ConfigureAwait(false);
                    await missions.TryAssignAsync(afterSecond, vessel).ConfigureAwait(false);
                    Mission preparedAgain = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;
                    AssertEqual(1, CountOccurrences(preparedAgain.Description ?? "", requirement), "real handoff preparation preserves the operator append exactly once");
                    AssertEqual(1, CountOccurrences(preparedAgain.Description ?? "", MissionService.BuildHandoffMarker(worker.Id)), "new closed handoff replacement remains idempotent");
                    string close = "<!-- ARMADA:HANDOFF-END:" + worker.Id + " -->";
                    AssertEqual(1, CountOccurrences(preparedAgain.Description ?? "", "OWNED-BOARD-NOTE"), "owned board notes refresh once with their handoff");
                    AssertEqual(1, CountOccurrences(preparedAgain.Description ?? "", close), "production emits one closing boundary");
                    AssertTrue(preparedAgain.Description!.IndexOf("OWNED-BOARD-NOTE", StringComparison.Ordinal)
                        < preparedAgain.Description.IndexOf(close, StringComparison.Ordinal), "board notes stay inside the generated boundary");
                    string dock = Path.Combine(Path.GetTempPath(), "armada_operator_note_dock_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dock);
                    try
                    {
                        await missions.GenerateClaudeMdAsync(dock, preparedAgain, vessel).ConfigureAwait(false);
                        AssertContains(requirement.Trim(), await File.ReadAllTextAsync(Path.Combine(dock, "CLAUDE.md")).ConfigureAwait(false),
                            "the actual generated captain brief receives the operator requirement");
                    }
                    finally
                    {
                        try { Directory.Delete(dock, true); } catch (IOException) { }
                    }

                    foreach (string priorId in new[] { worker.Id, "msn_older_legacy" })
                    {
                        string legacy = "Original brief.\n\n---\n" + MissionService.BuildHandoffMarker(priorId)
                            + "\nlegacy generated context\n### Board notes on this voyage\nlegacy board context\n" + requirement;
                        preparedAgain.Description = legacy;
                        preparedAgain.BranchName = null;
                        preparedAgain = await testDb.Driver.Missions.UpdateAsync(preparedAgain).ConfigureAwait(false);
                        await missions.TryAssignAsync(preparedAgain, vessel).ConfigureAwait(false);
                        preparedAgain = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;
                        AssertContains(legacy, preparedAgain.Description ?? "", "real preparation preserves a legacy block and its opaque tail verbatim");
                        AssertEqual(1, CountOccurrences(preparedAgain.Description ?? "", requirement), "legacy operator requirements remain unique");
                    }
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
                    AssertEqual(MissionAssignmentHold.DependencyCompletionInFlight, afterFirst.AssignmentHoldReason,
                        "The mission names the hold: its predecessor's completion handling is still running.");
                    AssertEqual(worker.Id, afterFirst.AssignmentHoldEntityId, "The hold names the predecessor that holds it.");

                    // A second tick inside the same gate keeps the hold without recording it again.
                    AssertFalse(await missions.TryAssignAsync(afterFirst, vessel).ConfigureAwait(false), "A second tick inside the gate still defers.");
                    afterFirst = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;
                    List<ArmadaEvent> heldEvents = await testDb.Driver.Events.EnumerateByTypeAsync(MissionAssignmentHold.HeldEventType).ConfigureAwait(false);
                    AssertEqual(1, heldEvents.Count(e => e.MissionId == testEngineer.Id),
                        "One hold event for the whole hold, not one per assignment tick.");
                    AssertContains(MissionAssignmentHold.DependencyCompletionInFlight, heldEvents.Single(e => e.MissionId == testEngineer.Id).Payload ?? "",
                        "The hold event carries the reason code.");
                    AssertContains(worker.Id, heldEvents.Single(e => e.MissionId == testEngineer.Id).Payload ?? "",
                        "The hold event carries the holding record.");

                    // Now the gate clears (simulating the DoD gate finishing and the batch handoff
                    // running). The next sweep tick should self-heal cleanly.
                    missions.InFlightCompletionsForTests.TryRemove(worker.Id, out _);

                    bool secondAssigned = await missions.TryAssignAsync(afterFirst, vessel).ConfigureAwait(false);
                    Mission afterSecond = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;
                    AssertNull(afterSecond.AssignmentHoldReason, "Assignment clears the hold reason.");
                    AssertNull(afterSecond.AssignmentHoldEntityId, "Assignment clears the holding record.");
                    List<ArmadaEvent> releasedEvents = await testDb.Driver.Events.EnumerateByTypeAsync(MissionAssignmentHold.ReleasedEventType).ConfigureAwait(false);
                    AssertEqual(1, releasedEvents.Count(e => e.MissionId == testEngineer.Id), "The release is recorded once.");

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
                        Dock afterHandoff = (await testDb.Driver.Docks.ReadAsync(dock.Id).ConfigureAwait(false))!;
                        AssertFalse(afterHandoff.Active, "The handoff releases the upstream dock, so the next stage can check the branch out");
                    }
                    finally
                    {
                        try { Directory.Delete(worktree, true); } catch { }
                    }
                }
            });

            await RunTest("InterruptedGate_OperatorClear_HandsOffAndReleasesTheDock", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    WireDefinitionOfDoneGate(missions, testDb.Driver);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Mission worker = await CreateProducedWorkerAsync(testDb.Driver, vessel, "armada/worker-cleared").ConfigureAwait(false);
                    string worktree = Path.Combine(Path.GetTempPath(), "armada_cleared_dock_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(worktree);
                    try
                    {
                        Dock dock = new Dock(vessel.Id);
                        dock.WorktreePath = worktree;
                        dock.BranchName = "armada/worker-cleared";
                        dock = await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("cleared voyage") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                        worker.DockId = dock.Id;
                        worker.VoyageId = voyage.Id;
                        worker = await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);

                        Mission testEngineer = await CreateDependentAsync(
                            testDb.Driver, vessel, "TestEngineer", worker.Id, "Original TestEngineer brief.").ConfigureAwait(false);
                        testEngineer.VoyageId = voyage.Id;
                        testEngineer = await testDb.Driver.Missions.UpdateAsync(testEngineer).ConfigureAwait(false);
                        await missions.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);
                        AssertTrue(MissionService.IsDefinitionOfDoneNotRunHold((await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!),
                            "The missing gate result holds the upstream first");

                        await missions.ClearOperatorReviewHoldAsync(worker.Id, "operator-a", "accept as it stands").ConfigureAwait(false);

                        Mission dependent = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;
                        AssertContains(MissionService.BuildHandoffMarker(worker.Id), dependent.Description ?? "", "The cleared upstream is handed off to its dependent");
                        Dock afterClear = (await testDb.Driver.Docks.ReadAsync(dock.Id).ConfigureAwait(false))!;
                        AssertFalse(afterClear.Active, "Clearing the hold releases the upstream dock, so the next stage can check the branch out");
                    }
                    finally
                    {
                        try { Directory.Delete(worktree, true); } catch { }
                    }
                }
            });

            await RunTest("InterruptedGate_ClearWhileRerunRuns_IsRefusedAndTheRerunReleasesTheHold", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    WireDefinitionOfDoneGate(missions, testDb.Driver);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                    {
                        Name = "Slow Passing Profile",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = "sleep 3",
                        UnitTestCommand = "exit 0",
                        IsDefault = true,
                        Active = true
                    }).ConfigureAwait(false);
                    Mission worker = await CreateProducedWorkerAsync(testDb.Driver, vessel, "armada/worker-rerun-slow").ConfigureAwait(false);
                    string worktree = Path.Combine(Path.GetTempPath(), "armada_rerun_slow_dock_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(worktree);
                    try
                    {
                        Dock dock = new Dock(vessel.Id);
                        dock.WorktreePath = worktree;
                        dock.BranchName = "armada/worker-rerun-slow";
                        dock = await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("slow rerun voyage") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                        worker.DockId = dock.Id;
                        worker.VoyageId = voyage.Id;
                        worker = await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);

                        Mission testEngineer = await CreateDependentAsync(
                            testDb.Driver, vessel, "TestEngineer", worker.Id, "Original TestEngineer brief.").ConfigureAwait(false);
                        testEngineer.VoyageId = voyage.Id;
                        testEngineer = await testDb.Driver.Missions.UpdateAsync(testEngineer).ConfigureAwait(false);
                        await missions.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);

                        AssertEqual(1, await missions.StartInterruptedDefinitionOfDoneGateRerunsAsync().ConfigureAwait(false), "The held mission gets one re-run");
                        AssertTrue(MissionService.GateRerunsInFlight().Contains(worker.Id), "The re-run is in flight");

                        InvalidOperationException? refused = null;
                        try
                        {
                            await missions.ClearOperatorReviewHoldAsync(worker.Id, "operator-b", "accept as it stands").ConfigureAwait(false);
                        }
                        catch (InvalidOperationException ex)
                        {
                            refused = ex;
                        }
                        AssertNotNull(refused, "Clearing the hold while its gate re-runs on the dock is refused");
                        AssertStartsWith(MissionService.GateRerunRunningReason, refused!.Message, "The refusal names its reason");
                        Mission stillHeld = (await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!;
                        AssertTrue(stillHeld.HeldForOperatorReview, "The refused clear leaves the hold in place");
                        Mission notHandedOff = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;
                        AssertNull(notHandedOff.BranchName, "Nothing is handed off while the re-run holds the dock");

                        for (int i = 0; i < 300 && MissionService.GateRerunsInFlight().Contains(worker.Id); i++)
                            await Task.Delay(100).ConfigureAwait(false);
                        AssertFalse(MissionService.GateRerunsInFlight().Contains(worker.Id), "The re-run finishes");

                        Mission released = (await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!;
                        AssertFalse(released.HeldForOperatorReview, "The passing re-run releases the hold itself");
                        Mission dependent = (await testDb.Driver.Missions.ReadAsync(testEngineer.Id).ConfigureAwait(false))!;
                        AssertContains(MissionService.BuildHandoffMarker(worker.Id), dependent.Description ?? "", "The re-run hands off to the dependent");
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

            await RunTest("HostFaultHold_SweepStartsOneGateRerun", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    WireDefinitionOfDoneGate(missions, testDb.Driver);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Mission worker = await CreateProducedWorkerAsync(testDb.Driver, vessel, "armada/worker-host-fault").ConfigureAwait(false);
                    worker.HeldForOperatorReview = true;
                    worker.HeldForOperatorReviewReason = MissionService.DefinitionOfDoneHostFaultHoldPrefix
                        + "Timeout. The gate failed as a host fault with no failing test.";
                    worker = await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);

                    AssertEqual(1, await missions.StartInterruptedDefinitionOfDoneGateRerunsAsync().ConfigureAwait(false),
                        "A host-fault hold is picked up by the existing gate re-run sweep");
                    for (int i = 0; i < 100 && MissionService.GateRerunsInFlight().Contains(worker.Id); i++)
                        await Task.Delay(50).ConfigureAwait(false);
                    AssertFalse(MissionService.GateRerunsInFlight().Contains(worker.Id), "The re-run finishes");
                    Mission stillHeld = (await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!;
                    AssertTrue(MissionService.IsDefinitionOfDoneGateRerunHold(stillHeld),
                        "A host-fault hold with no dock stays held after the sweep cannot re-gate it");
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

            await RunTest("Completion_CancellationWinsAtWrite_DoesNotPublishAnOutcome", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Mission worker = await CreateUpstreamAsync(testDb.Driver, vessel, "Worker", "armada/worker-cancel-race", MissionStatusEnum.InProgress).ConfigureAwait(false);
                    Captain captain = await CreateIdleCaptainAsync(testDb.Driver, "completion-captain", "claude-opus-5", "[\"Worker\"]").ConfigureAwait(false);
                    captain.CurrentMissionId = worker.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
                    int outcomes = 0;
                    missions.OnMissionOutcome = (_, _) => { outcomes++; return Task.CompletedTask; };

                    // Commit a competing cancellation at the completion write boundary.
                    using (Microsoft.Data.Sqlite.SqliteConnection connection = new Microsoft.Data.Sqlite.SqliteConnection(testDb.ConnectionString))
                    {
                        await connection.OpenAsync().ConfigureAwait(false);
                        using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
                        {
                            command.CommandText = "CREATE TRIGGER cancellation_wins BEFORE UPDATE ON missions WHEN OLD.id = '" + worker.Id
                                + "' AND OLD.status = 'InProgress' AND NEW.status = 'WorkProduced' BEGIN UPDATE missions SET status = 'Cancelled' WHERE id = OLD.id; SELECT RAISE(IGNORE); END;";
                            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }
                    await missions.HandleCompletionAsync(captain, worker.Id).ConfigureAwait(false);
                    AssertEqual(0, outcomes, "A completion whose first write loses to cancellation stops before outcome callbacks.");
                    AssertEqual(MissionStatusEnum.Cancelled, (await testDb.Driver.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!.Status);
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

            await RunTest("RecoverDanglingHandoffs_ArchitectRepairsPartialSliceChainIdempotently", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("Interrupted Architect fan-out") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);

                    Mission architect = new Mission("[Architect] Plan", "Plan the work.");
                    architect.VesselId = vessel.Id;
                    architect.VoyageId = voyage.Id;
                    architect.Persona = "Architect";
                    architect.Status = MissionStatusEnum.WorkProduced;
                    architect.AgentOutput =
                        "[ARMADA:MISSION]\n" +
                        "title: First slice\n" +
                        "description: Implement the first slice.\n" +
                        "[/ARMADA:MISSION]\n" +
                        "[ARMADA:MISSION]\n" +
                        "title: Second slice\n" +
                        "dependsOnMissionId: M1\n" +
                        "description: Implement the second slice.\n" +
                        "[/ARMADA:MISSION]";
                    architect = await testDb.Driver.Missions.CreateAsync(architect).ConfigureAwait(false);

                    string firstBrief = MissionService.ArchitectDerivedBriefPreamble + "\n\nImplement the first slice.";
                    string secondBrief = MissionService.ArchitectDerivedBriefPreamble + "\n\nImplement the second slice.";
                    Mission firstWorker = await CreateArchitectSliceStageAsync(
                        testDb.Driver, vessel, voyage, architect, "First slice [Worker]", "Worker",
                        "<!-- ARMADA:ARCHITECT-HANDOFF -->\n" + firstBrief, architect.Id).ConfigureAwait(false);
                    Mission firstTest = await CreateArchitectSliceStageAsync(
                        testDb.Driver, vessel, voyage, architect, "First slice [TestEngineer]", "TestEngineer",
                        firstBrief, firstWorker.Id).ConfigureAwait(false);
                    Mission firstJudge = await CreateArchitectSliceStageAsync(
                        testDb.Driver, vessel, voyage, architect, "First slice [Judge]", "Judge",
                        firstBrief, firstTest.Id).ConfigureAwait(false);

                    Mission secondWorker = await CreateArchitectSliceStageAsync(
                        testDb.Driver, vessel, voyage, architect, "Second slice [Worker]", "Worker",
                        "<!-- ARMADA:ARCHITECT-HANDOFF -->\n" + secondBrief, architect.Id).ConfigureAwait(false);
                    Mission secondTest = await CreateArchitectSliceStageAsync(
                        testDb.Driver, vessel, voyage, architect, "Second slice [TestEngineer]", "TestEngineer",
                        secondBrief, secondWorker.Id).ConfigureAwait(false);

                    Signal mail = new Signal(SignalTypeEnum.Mail, System.Text.Json.JsonSerializer.Serialize(
                        new VoyageMailboxSignalPayload { VoyageId = voyage.Id, Message = "Keep the public contract." }));
                    mail = await testDb.Driver.Signals.CreateAsync(mail).ConfigureAwait(false);
                    string notes = "[ORCHESTRATOR NOTES]\nKeep the public contract.\n[/ORCHESTRATOR NOTES]\n\n";
                    firstWorker.Description = notes + firstWorker.Description;
                    secondWorker.Description = notes + secondWorker.Description;
                    await testDb.Driver.Missions.UpdateAsync(firstWorker).ConfigureAwait(false);
                    await testDb.Driver.Missions.UpdateAsync(secondWorker).ConfigureAwait(false);

                    int firstRecovery = await missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                    List<Mission> afterFirstRecovery = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    Mission? repairedSecondJudge = afterFirstRecovery.SingleOrDefault(m =>
                        m.Persona == "Judge" && m.Title == "Second slice [Judge]");
                    Mission? readBackFirstWorker = await testDb.Driver.Missions.ReadAsync(firstWorker.Id).ConfigureAwait(false);
                    Mission? readBackFirstJudge = await testDb.Driver.Missions.ReadAsync(firstJudge.Id).ConfigureAwait(false);
                    Mission? readBackSecondWorker = await testDb.Driver.Missions.ReadAsync(secondWorker.Id).ConfigureAwait(false);

                    AssertTrue(firstRecovery > 0, "The interrupted fan-out is recovered.");
                    AssertTrue((await testDb.Driver.Signals.ReadAsync(mail.Id).ConfigureAwait(false))!.Read,
                        "A note already delivered to every slice is marked read after recovery.");
                    AssertEqual(notes + "<!-- ARMADA:ARCHITECT-HANDOFF -->\n" + firstBrief,
                        (await testDb.Driver.Missions.ReadAsync(firstWorker.Id).ConfigureAwait(false))!.Description,
                        "Recovery preserves the delivered mailbox block.");
                    AssertNotNull(repairedSecondJudge, "The missing final stage is created for the partial second chain.");
                    AssertEqual(secondTest.Id, repairedSecondJudge!.DependsOnMissionId, "The missing stage continues from the existing cloned stage.");
                    AssertEqual(firstWorker.Id, readBackFirstWorker!.Id, "The first template Worker keeps its id.");
                    AssertEqual(firstJudge.Id, readBackFirstJudge!.Id, "The first template chain keeps its existing terminal stage id.");
                    AssertEqual(firstJudge.Id, readBackSecondWorker!.DependsOnMissionId, "The second slice is sequenced after the first slice terminal stage.");

                    int missionCountAfterRepair = afterFirstRecovery.Count;
                    string repairedJudgeId = repairedSecondJudge.Id;
                    await missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                    List<Mission> afterSecondRecovery = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    Mission? secondPassJudge = afterSecondRecovery.SingleOrDefault(m => m.Id == repairedJudgeId);
                    Mission? secondPassArchitect = await testDb.Driver.Missions.ReadAsync(architect.Id).ConfigureAwait(false);

                    AssertEqual(missionCountAfterRepair, afterSecondRecovery.Count, "A second recovery creates no duplicate stages.");
                    AssertNotNull(secondPassJudge, "The repaired stage keeps its id on a repeated recovery.");
                    AssertContains("<!-- armada:fanout-complete -->", secondPassArchitect!.Description,
                        "A fully repaired fan-out records its completion marker.");
                    secondPassArchitect.AgentOutput = secondPassArchitect.AgentOutput!.Replace("Implement the second slice.", "A changed second slice.", StringComparison.Ordinal);
                    await testDb.Driver.Missions.UpdateAsync(secondPassArchitect).ConfigureAwait(false);
                    await missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                    AssertTrue((await testDb.Driver.Missions.ReadAsync(architect.Id).ConfigureAwait(false))!.HeldForOperatorReview,
                        "The completion marker cannot certify a different stored plan.");
                    AssertEqual(missionCountAfterRepair, (await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false)).Count);

                }
            });

            await RunTest("RecoverDanglingHandoffs_VersionedParallelPlanAcceptsExactTerminalMembers", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("Versioned parallel recovery") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                    string output = CreateVersionedArchitectPlan(includeSecondBranch: true, singleIntegration: false);
                    Mission architect = new Mission("[Architect] Versioned plan", "Plan parallel work.")
                    {
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Persona = "Architect",
                        Status = MissionStatusEnum.WorkProduced,
                        AgentOutput = output
                    };
                    architect = await testDb.Driver.Missions.CreateAsync(architect).ConfigureAwait(false);

                    string firstBrief = GetVersionedSliceBrief(output, 0);
                    string secondBrief = GetVersionedSliceBrief(output, 1);
                    Mission firstWorker = await CreateArchitectSliceStageAsync(testDb.Driver, vessel, voyage, architect,
                        "Branch A [Worker]", "Worker", "<!-- ARMADA:ARCHITECT-HANDOFF -->\n" + firstBrief, architect.Id).ConfigureAwait(false);
                    Mission firstTest = await CreateArchitectSliceStageAsync(testDb.Driver, vessel, voyage, architect,
                        "Branch A [TestEngineer]", "TestEngineer", firstBrief, firstWorker.Id).ConfigureAwait(false);
                    Mission firstJudge = await CreateArchitectSliceStageAsync(testDb.Driver, vessel, voyage, architect,
                        "Branch A [Judge]", "Judge", firstBrief, firstTest.Id).ConfigureAwait(false);
                    Mission secondWorker = await CreateArchitectSliceStageAsync(testDb.Driver, vessel, voyage, architect,
                        "Branch B [Worker]", "Worker", "<!-- ARMADA:ARCHITECT-HANDOFF -->\n" + secondBrief, architect.Id).ConfigureAwait(false);
                    Mission secondTest = await CreateArchitectSliceStageAsync(testDb.Driver, vessel, voyage, architect,
                        "Branch B [TestEngineer]", "TestEngineer", secondBrief, secondWorker.Id).ConfigureAwait(false);

                    AssertTrue(await missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false) > 0,
                        "Recovery repairs and accepts the interrupted versioned fan-out.");
                    List<Mission> rows = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    Mission secondJudge = rows.Single(m => m.Title == "Branch B [Judge]");
                    Mission integration = rows.Single(m => m.Title == "Final integration [Worker]");
                    MissionIntegrationBarrier barrier = integration.IntegrationBarrier!;
                    AssertEqual(MissionIntegrationBarrierPhaseEnum.Accepted, barrier.Phase,
                        "The final integration accepts membership only after recovery repairs its branch chain.");
                    AssertEqual(2, barrier.Members.Count, "Each independent Architect branch has one accepted terminal.");
                    AssertEqual(firstWorker.Id, barrier.Members[0].RootMissionId);
                    AssertEqual(firstJudge.Id, barrier.Members[0].AcceptedTerminalMissionId);
                    AssertEqual(secondWorker.Id, barrier.Members[1].RootMissionId);
                    AssertEqual(secondJudge.Id, barrier.Members[1].AcceptedTerminalMissionId);
                    AssertEqual(secondTest.Id, secondJudge.DependsOnMissionId, "The repaired terminal continues the partial chain.");

                    await missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                    Mission? repeated = await testDb.Driver.Missions.ReadAsync(integration.Id).ConfigureAwait(false);
                    AssertEqual(2, repeated!.IntegrationBarrier!.Members.Count, "Repeated recovery preserves the accepted member count.");
                    AssertEqual(firstJudge.Id, repeated.IntegrationBarrier.Members[0].AcceptedTerminalMissionId,
                        "Repeated recovery cannot replace the first terminal ID.");
                    AssertEqual(secondJudge.Id, repeated.IntegrationBarrier.Members[1].AcceptedTerminalMissionId,
                        "Repeated recovery cannot replace the repaired terminal ID.");
                }
            });

            await RunTest("RecoverDanglingHandoffs_SingleIntegrationAcceptsEmptyBarrier", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("Zero prior branches") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                    string output = CreateVersionedArchitectPlan(includeSecondBranch: false, singleIntegration: true);
                    Mission architect = new Mission("[Architect] Single integration", "Plan integration.")
                    {
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Persona = "Architect",
                        Status = MissionStatusEnum.WorkProduced,
                        AgentOutput = output
                    };
                    architect = await testDb.Driver.Missions.CreateAsync(architect).ConfigureAwait(false);
                    string brief = GetVersionedSliceBrief(output, 0);
                    Mission integration = await CreateArchitectSliceStageAsync(testDb.Driver, vessel, voyage, architect,
                        "Solo integration [Worker]", "Worker", "<!-- ARMADA:ARCHITECT-HANDOFF -->\n" + brief, architect.Id).ConfigureAwait(false);

                    await missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                    Mission? accepted = await testDb.Driver.Missions.ReadAsync(integration.Id).ConfigureAwait(false);
                    AssertEqual(MissionIntegrationBarrierPhaseEnum.Accepted, accepted!.IntegrationBarrier!.Phase,
                        "A zero-branch final integration accepts its empty barrier during recovery.");
                    AssertEqual(0, accepted.IntegrationBarrier.Members.Count, "No prior branch means an empty member list.");
                    AssertEqual(MissionStatusEnum.Pending, accepted.Status, "Acceptance does not complete or dispatch the integration mission.");
                }
            });

            await RunTest("RecoverDanglingHandoffs_AmbiguousTerminalPathsStayPreparing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("Ambiguous terminal paths") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                    string output = CreateVersionedArchitectPlan(includeSecondBranch: false, singleIntegration: false);
                    Mission architect = new Mission("[Architect] Ambiguous plan", "Plan work.")
                    {
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Persona = "Architect",
                        Status = MissionStatusEnum.WorkProduced,
                        AgentOutput = output
                    };
                    architect = await testDb.Driver.Missions.CreateAsync(architect).ConfigureAwait(false);
                    string brief = GetVersionedSliceBrief(output, 0);
                    Mission worker = await CreateArchitectSliceStageAsync(testDb.Driver, vessel, voyage, architect,
                        "Branch A [Worker]", "Worker", "<!-- ARMADA:ARCHITECT-HANDOFF -->\n" + brief, architect.Id).ConfigureAwait(false);
                    Mission reviewerA = await CreateArchitectSliceStageAsync(testDb.Driver, vessel, voyage, architect,
                        "Branch A [TestEngineer]", "TestEngineer", brief, worker.Id).ConfigureAwait(false);
                    Mission reviewerB = await CreateArchitectSliceStageAsync(testDb.Driver, vessel, voyage, architect,
                        "Branch A [Judge]", "Judge", brief, worker.Id).ConfigureAwait(false);
                    reviewerA.StageOrder = 2;
                    reviewerB.StageOrder = 2;
                    await testDb.Driver.Missions.UpdateAsync(reviewerA).ConfigureAwait(false);
                    await testDb.Driver.Missions.UpdateAsync(reviewerB).ConfigureAwait(false);

                    await missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                    Mission? heldArchitect = await testDb.Driver.Missions.ReadAsync(architect.Id).ConfigureAwait(false);
                    List<Mission> rows = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertTrue(heldArchitect!.HeldForOperatorReview,
                        "Recovery must not accept a guessed terminal when a branch has two terminal paths.");
                    AssertFalse(rows.Any(mission => mission.IntegrationBarrier?.Phase == MissionIntegrationBarrierPhaseEnum.Accepted),
                        "Ambiguous branch paths must not produce an accepted integration barrier.");
                }
            });

            await RunTest("RecoverDanglingHandoffs_UnexpectedPipelineChildStaysPreparing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("Unexpected pipeline child") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                    string output = CreateVersionedArchitectPlan(includeSecondBranch: false, singleIntegration: false);
                    Mission architect = new Mission("[Architect] Unexpected child", "Plan work.")
                    {
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Persona = "Architect",
                        Status = MissionStatusEnum.WorkProduced,
                        AgentOutput = output
                    };
                    architect = await testDb.Driver.Missions.CreateAsync(architect).ConfigureAwait(false);
                    string brief = GetVersionedSliceBrief(output, 0);
                    Mission worker = await CreateArchitectSliceStageAsync(testDb.Driver, vessel, voyage, architect,
                        "Branch A [Worker]", "Worker", "<!-- ARMADA:ARCHITECT-HANDOFF -->\n" + brief, architect.Id).ConfigureAwait(false);
                    Mission unexpected = await CreateArchitectSliceStageAsync(testDb.Driver, vessel, voyage, architect,
                        "Unexpected [TestEngineer]", "TestEngineer", brief, worker.Id).ConfigureAwait(false);

                    await missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                    Mission? heldArchitect = await testDb.Driver.Missions.ReadAsync(architect.Id).ConfigureAwait(false);
                    Mission? unchanged = await testDb.Driver.Missions.ReadAsync(unexpected.Id).ConfigureAwait(false);
                    List<Mission> rows = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertTrue(heldArchitect!.HeldForOperatorReview,
                        "Recovery must not treat an earlier completed stage as terminal when an unexpected child exists.");
                    AssertEqual("Unexpected [TestEngineer]", unchanged!.Title,
                        "Recovery must hold before retitling the unexpected child into the accepted pipeline.");
                    AssertFalse(rows.Any(mission => mission.IntegrationBarrier?.Phase == MissionIntegrationBarrierPhaseEnum.Accepted),
                        "An unexpected pipeline child must not produce an accepted integration barrier.");
                }
            });

            await RunTest("RecoverDanglingHandoffs_ArchitectRetriesCreateAfterLostAcknowledgement", async () =>
            {
                string[] lostCreateTitles =
                {
                    "Second slice [Worker]",
                    "Second slice [TestEngineer]",
                    "Second slice [Judge]"
                };

                foreach (string lostCreateTitle in lostCreateTitles)
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        ArmadaSettings settings = CreateSettings();
                        MissionService missions = CreateMissionService(testDb.Driver, settings);
                        Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                            new Voyage("Lost Architect create acknowledgement") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);

                        Mission architect = new Mission("[Architect] Plan", "Plan the work.");
                        architect.VesselId = vessel.Id;
                        architect.VoyageId = voyage.Id;
                        architect.Persona = "Architect";
                        architect.Status = MissionStatusEnum.WorkProduced;
                        architect.AgentOutput =
                            "[ARMADA:MISSION]\n" +
                            "title: First slice\n" +
                            "description: Implement the first slice.\n" +
                            "[/ARMADA:MISSION]\n" +
                            "[ARMADA:MISSION]\n" +
                            "title: Second slice\n" +
                            "dependsOnMissionId: M1\n" +
                            "description: Implement the second slice.\n" +
                            "[/ARMADA:MISSION]";
                        architect = await testDb.Driver.Missions.CreateAsync(architect).ConfigureAwait(false);

                        string firstBrief = MissionService.ArchitectDerivedBriefPreamble + "\n\nImplement the first slice.";
                        Mission firstWorker = await CreateArchitectSliceStageAsync(
                            testDb.Driver, vessel, voyage, architect, "First slice [Worker]", "Worker",
                            "<!-- ARMADA:ARCHITECT-HANDOFF -->\n" + firstBrief, architect.Id).ConfigureAwait(false);
                        Mission firstTest = await CreateArchitectSliceStageAsync(
                            testDb.Driver, vessel, voyage, architect, "First slice [TestEngineer]", "TestEngineer",
                            firstBrief, firstWorker.Id).ConfigureAwait(false);
                        Mission firstJudge = await CreateArchitectSliceStageAsync(
                            testDb.Driver, vessel, voyage, architect, "First slice [Judge]", "Judge",
                            firstBrief, firstTest.Id).ConfigureAwait(false);

                        using (Microsoft.Data.Sqlite.SqliteConnection connection = new Microsoft.Data.Sqlite.SqliteConnection(testDb.ConnectionString))
                        {
                            await connection.OpenAsync().ConfigureAwait(false);
                            using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "CREATE TRIGGER lose_architect_create_ack AFTER INSERT ON missions WHEN NEW.title = '" + lostCreateTitle
                                    + "' BEGIN SELECT RAISE(FAIL, 'lost create acknowledgement'); END;";
                                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }
                        }

                        await missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                        List<Mission> afterLostAcknowledgement = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                        List<Mission> persistedTarget = afterLostAcknowledgement.Where(m => m.Title == lostCreateTitle).ToList();
                        AssertEqual(1, persistedTarget.Count, "The insert committed before its acknowledgement was lost.");
                        string targetId = persistedTarget[0].Id;

                        using (Microsoft.Data.Sqlite.SqliteConnection connection = new Microsoft.Data.Sqlite.SqliteConnection(testDb.ConnectionString))
                        {
                            await connection.OpenAsync().ConfigureAwait(false);
                            using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "DROP TRIGGER lose_architect_create_ack;";
                                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }
                        }

                        await missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                        await missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                        List<Mission> afterRetries = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                        Mission? secondWorker = afterRetries.SingleOrDefault(m => m.Title == "Second slice [Worker]");
                        Mission? secondTest = afterRetries.SingleOrDefault(m => m.Title == "Second slice [TestEngineer]");
                        Mission? secondJudge = afterRetries.SingleOrDefault(m => m.Title == "Second slice [Judge]");
                        Mission? readBackArchitect = await testDb.Driver.Missions.ReadAsync(architect.Id).ConfigureAwait(false);

                        AssertEqual(7, afterRetries.Count, "Retries create exactly the two three-stage slices and the Architect.");
                        AssertEqual(afterRetries.Count, afterRetries.Select(m => m.Title).Distinct(StringComparer.Ordinal).Count(), "Recovery creates no duplicate titles.");
                        AssertNotNull(secondWorker, "The second Worker exists.");
                        AssertNotNull(secondTest, "The second TestEngineer exists.");
                        AssertNotNull(secondJudge, "The second Judge exists.");
                        Mission? retainedTarget = afterRetries.SingleOrDefault(m => m.Title == lostCreateTitle);
                        AssertEqual(targetId, retainedTarget!.Id, "Recovery keeps the row whose create acknowledgement was lost.");
                        AssertEqual(firstJudge.Id, secondWorker!.DependsOnMissionId, "The second root depends on the first slice terminal stage.");
                        AssertEqual(secondWorker.Id, secondTest!.DependsOnMissionId, "The second TestEngineer depends on its Worker.");
                        AssertEqual(secondTest.Id, secondJudge!.DependsOnMissionId, "The second Judge depends on its TestEngineer.");
                        AssertContains("<!-- armada:fanout-complete -->", readBackArchitect!.Description,
                            "Successful retries record the fan-out completion marker.");
                    }
                }
            });

            await RunTest("RecoverDanglingHandoffs_ArchitectDescriptionFallbackCompletionIsStable", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Fallback plan") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                    Mission architect = new Mission("Plan", "1. Build the widget");
                    architect.Persona = "Architect";
                    architect.Status = MissionStatusEnum.WorkProduced;
                    architect.VesselId = vessel.Id;
                    architect.VoyageId = voyage.Id;
                    architect = await testDb.Driver.Missions.CreateAsync(architect).ConfigureAwait(false);
                    await CreateArchitectSliceStageAsync(testDb.Driver, vessel, voyage, architect, "Placeholder", "Worker", "Waiting for the plan.", architect.Id).ConfigureAwait(false);
                    AssertEqual(1, await missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false));
                    string completedDescription = (await testDb.Driver.Missions.ReadAsync(architect.Id).ConfigureAwait(false))!.Description;
                    await missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                    Mission current = (await testDb.Driver.Missions.ReadAsync(architect.Id).ConfigureAwait(false))!;
                    AssertFalse(current.HeldForOperatorReview, "A completion marker must not change the numbered fallback plan it certifies.");
                    AssertEqual(completedDescription, current.Description, "Repeated recovery keeps one stable completion record.");
                    AssertEqual(2, (await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false)).Count);
                }
            });

            await RunTest("RecoverDanglingHandoffs_ArchitectLeavesStartedAmbiguousSliceForOperator", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("Started Architect slice") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);

                    Mission architect = new Mission("[Architect] Plan", "Plan the work.");
                    architect.VesselId = vessel.Id;
                    architect.VoyageId = voyage.Id;
                    architect.Persona = "Architect";
                    architect.Status = MissionStatusEnum.WorkProduced;
                    architect.AgentOutput =
                        "[ARMADA:MISSION]\n" +
                        "title: First slice\n" +
                        "description: Implement the first slice.\n" +
                        "[/ARMADA:MISSION]\n" +
                        "[ARMADA:MISSION]\n" +
                        "title: Second slice\n" +
                        "dependsOnMissionId: M1\n" +
                        "description: Implement the second slice.\n" +
                        "[/ARMADA:MISSION]";
                    architect = await testDb.Driver.Missions.CreateAsync(architect).ConfigureAwait(false);

                    string firstBrief = MissionService.ArchitectDerivedBriefPreamble + "\n\nImplement the first slice.";
                    string secondBrief = MissionService.ArchitectDerivedBriefPreamble + "\n\nImplement the second slice.";
                    Mission firstWorker = await CreateArchitectSliceStageAsync(
                        testDb.Driver, vessel, voyage, architect, "First slice [Worker]", "Worker",
                        "<!-- ARMADA:ARCHITECT-HANDOFF -->\n" + firstBrief, architect.Id).ConfigureAwait(false);
                    Mission firstTest = await CreateArchitectSliceStageAsync(
                        testDb.Driver, vessel, voyage, architect, "First slice [TestEngineer]", "TestEngineer",
                        firstBrief, firstWorker.Id).ConfigureAwait(false);
                    Mission firstJudge = await CreateArchitectSliceStageAsync(
                        testDb.Driver, vessel, voyage, architect, "First slice [Judge]", "Judge",
                        firstBrief, firstTest.Id).ConfigureAwait(false);
                    Mission secondWorker = await CreateArchitectSliceStageAsync(
                        testDb.Driver, vessel, voyage, architect, "Second slice [Worker]", "Worker",
                        "<!-- ARMADA:ARCHITECT-HANDOFF -->\n" + secondBrief, architect.Id).ConfigureAwait(false);
                    secondWorker.Status = MissionStatusEnum.InProgress;
                    secondWorker.StartedUtc = DateTime.UtcNow.AddMinutes(-1);
                    secondWorker.BranchName = "armada/started-slice";
                    secondWorker = await testDb.Driver.Missions.UpdateAsync(secondWorker).ConfigureAwait(false);
                    Mission existingChild = await CreateArchitectSliceStageAsync(
                        testDb.Driver, vessel, voyage, architect, "Second slice [TestEngineer]", "TestEngineer",
                        "Operator-edited brief.", secondWorker.Id).ConfigureAwait(false);
                    string existingDescription = secondWorker.Description;
                    string existingTitle = secondWorker.Title;
                    string existingDependency = secondWorker.DependsOnMissionId!;
                    string existingBranch = secondWorker.BranchName!;

                    await missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                    Mission? readBackArchitect = await testDb.Driver.Missions.ReadAsync(architect.Id).ConfigureAwait(false);
                    Mission? readBackSecondWorker = await testDb.Driver.Missions.ReadAsync(secondWorker.Id).ConfigureAwait(false);
                    Mission? readBackExistingChild = await testDb.Driver.Missions.ReadAsync(existingChild.Id).ConfigureAwait(false);
                    List<Mission> afterRecovery = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);

                    AssertTrue(readBackArchitect!.HeldForOperatorReview, "An unsafe started slice is held for operator review.");
                    AssertContains("architect_fanout_recovery", readBackArchitect.HeldForOperatorReviewReason ?? String.Empty,
                        "The hold names the unsafe Architect recovery.");
                    AssertEqual(existingDescription, readBackSecondWorker!.Description, "Recovery preserves the started Worker brief.");
                    AssertEqual(existingTitle, readBackSecondWorker.Title, "Recovery preserves the started Worker title.");
                    AssertEqual(existingDependency, readBackSecondWorker.DependsOnMissionId, "Recovery does not rewire the started Worker.");
                    AssertEqual(existingBranch, readBackSecondWorker.BranchName, "Recovery preserves the started Worker branch.");
                    AssertEqual("Operator-edited brief.", readBackExistingChild!.Description, "Recovery preserves the existing child brief.");
                    AssertEqual(1, afterRecovery.Count(m => m.Id == existingChild.Id), "The existing child remains unique.");
                    AssertEqual(6, afterRecovery.Count, "Recovery creates no stage behind the started partial chain.");
                    await AssertThrowsAsync<InvalidOperationException>(() => missions.ClearOperatorReviewHoldAsync(architect.Id, "operator", "retry safely"),
                        "Clearing a graph hold cannot bypass safe reconciliation.").ConfigureAwait(false);
                    AssertTrue((await testDb.Driver.Missions.ReadAsync(architect.Id).ConfigureAwait(false))!.HeldForOperatorReview);

                    AssertEqual(firstJudge.Id, (await testDb.Driver.Missions.ReadAsync(firstJudge.Id).ConfigureAwait(false))!.Id,
                        "The existing first-slice terminal stage remains present.");
                }
            });
        }

        private async Task<Mission> CreateArchitectSliceStageAsync(
            SqliteDatabaseDriver db,
            Vessel vessel,
            Voyage voyage,
            Mission architect,
            string title,
            string persona,
            string description,
            string dependsOn)
        {
            Mission mission = new Mission(title, description);
            mission.VesselId = vessel.Id;
            mission.VoyageId = voyage.Id;
            mission.Persona = persona;
            mission.Status = MissionStatusEnum.Pending;
            mission.DependsOnMissionId = dependsOn;
            mission.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
            mission.Mode = architect.Mode;
            return await db.Missions.CreateAsync(mission).ConfigureAwait(false);
        }
    }
}
