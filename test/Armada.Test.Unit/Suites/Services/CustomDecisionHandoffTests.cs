namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    /// <summary>
    /// Operator-defined custom decisions on the MissionDiff surface run when a Worker stage hands off its
    /// diff. These tests drive a real handoff through the mission service: a bound decision that flags
    /// becomes a Judge review instruction ahead of the Judge's own brief, and a decision that is Off,
    /// on the CaptainTool surface, scoped to another vessel, or unbound adds nothing.
    /// </summary>
    public sealed class CustomDecisionHandoffTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Custom Decision Handoff";

        private const string Diff = "diff --git a/src/Widget.cs b/src/Widget.cs\n--- a/src/Widget.cs\n+++ b/src/Widget.cs\n@@ -1 +1,2 @@\n a\n+public class WidgetCopy { }\n";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("A bound MissionDiff decision that flags adds a Judge review note at the Worker handoff", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    settings.TypedDecisions.Custom["duplicates_logic"] = Decision(CustomDecisionSurfaceEnum.MissionDiff, CustomDecisionSeamEnum.MissionDiffFlag, TypedDecisionModeEnum.Gate);
                    // Scoped by the vessel's NAME, which the handoff resolves from the mission's vessel id.
                    settings.TypedDecisions.Custom["duplicates_logic"].Vessels = new List<string> { "custom-handoff-vessel" };
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("duplicates_existing", 0.95));

                    Mission judge = await RunWorkerHandoffAsync(testDb, settings, client).ConfigureAwait(false);

                    AssertEqual(1, client.CallCount, "the decision reads the Worker's diff once");
                    AssertContains("public class WidgetCopy", FakeTypedDecisionClient.StateText(client.LastRequest), "the state carries the diff");
                    string brief = judge.Description ?? String.Empty;
                    AssertContains(MissionService.CustomDecisionNotePhrase, brief, "the note names the check");
                    AssertContains("duplicates_logic", brief, "and the decision that flagged");
                    AssertContains("duplicates_existing", brief, "and the question at or above the threshold");
                    AssertContains("not a verdict", brief, "and says a flag is not a verdict");
                    AssertContains("judge description", brief, "the Judge's own brief is kept");
                    int note = brief.IndexOf(MissionService.CustomDecisionNotePhrase, StringComparison.Ordinal);
                    AssertTrue(note >= 0 && note < brief.IndexOf("judge description", StringComparison.Ordinal),
                        "the note comes before the Judge's own description");
                }
            });

            await RunTest("A decision that is Off, on the CaptainTool surface, scoped elsewhere, or unbound adds no note", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    settings.TypedDecisions.Custom["off_decision"] = Decision(CustomDecisionSurfaceEnum.MissionDiff, CustomDecisionSeamEnum.MissionDiffFlag, TypedDecisionModeEnum.Off);
                    settings.TypedDecisions.Custom["tool_decision"] = Decision(CustomDecisionSurfaceEnum.CaptainTool, CustomDecisionSeamEnum.None, TypedDecisionModeEnum.Gate);
                    settings.TypedDecisions.Custom["unbound_decision"] = Decision(CustomDecisionSurfaceEnum.MissionDiff, CustomDecisionSeamEnum.None, TypedDecisionModeEnum.Gate);
                    CustomTypedDecisionSettings elsewhere = Decision(CustomDecisionSurfaceEnum.MissionDiff, CustomDecisionSeamEnum.MissionDiffFlag, TypedDecisionModeEnum.Gate);
                    elsewhere.Vessels = new List<string> { "some-other-vessel" };
                    settings.TypedDecisions.Custom["scoped_elsewhere"] = elsewhere;
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("duplicates_existing", 0.99));

                    Mission judge = await RunWorkerHandoffAsync(testDb, settings, client).ConfigureAwait(false);

                    AssertEqual(1, client.CallCount, "only the unbound MissionDiff decision is consulted; Off, CaptainTool, and another vessel's are not");
                    AssertFalse((judge.Description ?? String.Empty).Contains(MissionService.CustomDecisionNotePhrase, StringComparison.Ordinal),
                        "an unbound decision records its answer but adds no note");
                }
            });
        }

        private static CustomTypedDecisionSettings Decision(CustomDecisionSurfaceEnum surface, CustomDecisionSeamEnum binding, TypedDecisionModeEnum mode)
        {
            return new CustomTypedDecisionSettings
            {
                Mode = mode,
                GateThreshold = 0.9,
                Description = "reimplements existing logic",
                Surface = surface,
                Binding = binding,
                StateFields = new List<string> { "title", "diff" },
                Questions = new List<CustomTypedQuestionSettings>
                {
                    new CustomTypedQuestionSettings { Id = "duplicates_existing", Type = "noul", Instructions = "The change reimplements logic that already exists." }
                }
            };
        }

        private static ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_custom_handoff_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_custom_handoff_repos_" + Guid.NewGuid().ToString("N"));
            settings.TypedDecisions.Mode = TypedDecisionModeEnum.Gate;
            return settings;
        }

        private static async Task<Mission> RunWorkerHandoffAsync(TestDatabase testDb, ArmadaSettings settings, FakeTypedDecisionClient client)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            StubGitService git = new StubGitService { DiffResult = Diff };
            IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
            ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
            MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
            missionService.CustomDecisionAdapter = new CustomTypedDecisionAdapter(
                client, new TypedDecisionRecorder(testDb.Driver, logging), settings.TypedDecisions, logging);

            Vessel vessel = new Vessel("custom-handoff-vessel", "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_custom_handoff_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_custom_handoff_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Captain captain = new Captain("custom-handoff-captain");
            captain.State = CaptainStateEnum.Working;
            captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

            Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("custom-handoff-voyage")).ConfigureAwait(false);

            Mission worker = new Mission("[Worker] Implement", "worker description");
            worker.VesselId = vessel.Id;
            worker.VoyageId = voyage.Id;
            worker.CaptainId = captain.Id;
            worker.Persona = "Worker";
            worker.Status = MissionStatusEnum.InProgress;
            worker.BranchName = "armada/custom-handoff-captain/msn_worker";
            worker.DiffSnapshot = Diff;
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

            await missionService.HandleCompletionAsync(captain, worker.Id).ConfigureAwait(false);

            Mission? updated = await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
            if (updated == null) throw new InvalidOperationException("the Judge mission is gone after the handoff");
            return updated;
        }
    }
}
