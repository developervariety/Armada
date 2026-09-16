namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Table-driven tests for the D26 <c>prior_art</c> adapter across both admiral seams. Off adds no
    /// issue and makes no call; no candidates makes no call; unavailable and shadow/below-threshold add
    /// no issue and record an event. At or above threshold the preflight seam adds an
    /// <c>objective_prior_art_found</c> Error for <c>already_done</c>, an <c>objective_prior_art_integrate</c>
    /// advisory for <c>integrate_not_duplicate</c>, and a <c>prior_art_analyst_stage_recommended</c>
    /// advisory in the uncertain band on a large objective; the Judge seam returns a review instruction
    /// for <c>reimplements</c>, never a verdict. The caller token reaches the client, and a client fault
    /// never reaches the caller.
    /// </summary>
    public class TypedPriorArtAdapterTests : TestSuite
    {
        public override string Name => "Typed Prior Art Adapter (D26)";

        private const string _Decision = "prior_art";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.80)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static Objective SmallObjective()
        {
            return new Objective
            {
                Id = "obj_x",
                Title = "Add a decoder",
                Description = "Add PriorArtRetriever for the vessel.",
                AcceptanceCriteria = new List<string> { "PriorArtRetriever exists" },
                VesselIds = new List<string> { "vsl_x" }
            };
        }

        private static Objective LargeObjective()
        {
            return new Objective
            {
                Id = "obj_big",
                Title = "Add a decoder family",
                Description = "Add PriorArtRetriever for the vessel.",
                AcceptanceCriteria = new List<string> { "one exists", "two exists", "three exists" },
                VesselIds = new List<string> { "vsl_x" }
            };
        }

        private static Vessel BuildVessel()
        {
            return new Vessel { Id = "vsl_x", Name = "ExampleVessel", LocalPath = "/repo", DefaultBranch = "main" };
        }

        private static TypedDecisionResult Answer(double alreadyDone, double integrate, double reimplements)
        {
            Dictionary<string, TypedAnswer> map = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                [PriorArtDecisionShapes.AlreadyDoneId] = new TypedAnswer { Type = "noul", Noul = alreadyDone, Confidence = alreadyDone },
                [PriorArtDecisionShapes.IntegrateId] = new TypedAnswer { Type = "noul", Noul = integrate, Confidence = integrate },
                [PriorArtDecisionShapes.ReimplementsId] = new TypedAnswer { Type = "noul", Noul = reimplements, Confidence = reimplements },
                [PriorArtDecisionShapes.DeliversPrefix + "1"] = new TypedAnswer { Type = "choice", Choice = PriorArtDecisionShapes.DeliversSameCapability, Confidence = 0.9 }
            };
            return new TypedDecisionResult { Available = true, Answers = map, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        private static PriorArtRetrieval OneCandidate()
        {
            return FakePriorArtRetriever.RetrievalOf(
                FakePriorArtRetriever.Candidate(PriorArtWhereEnum.Landed, "src/Existing.cs:12"));
        }

        private static TypedPriorArtAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings, PriorArtRetrieval retrieval)
        {
            return new TypedPriorArtAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings,
                new FakePriorArtRetriever(retrieval), new LoggingModule());
        }

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                .EnumerateAsync(new EnumerationQuery { EventType = eventType, PageNumber = 1, PageSize = 100 })
                .ConfigureAwait(false);
            return events.Objects.Count;
        }

        private static bool HasIssue(ObjectiveDispatchPreview preview, string code)
        {
            return preview.Issues.Any(issue => String.Equals(issue.Code, code, StringComparison.Ordinal));
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Preflight_Off_NoIssue_NoCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(0.99, 0.0, 0.0));
                TypedPriorArtAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off), OneCandidate());
                ObjectiveDispatchPreview preview = new ObjectiveDispatchPreview { VesselId = "vsl_x" };

                await adapter.EvaluatePreflightAsync(SmallObjective(), BuildVessel(), preview, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, client.CallCount);
                AssertEqual(0, preview.Issues.Count);
            }).ConfigureAwait(false);

            await RunTest("Preflight_NoCandidates_NoCall_NoIssue", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(0.99, 0.0, 0.0));
                TypedPriorArtAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate), PriorArtRetrieval.Empty());
                ObjectiveDispatchPreview preview = new ObjectiveDispatchPreview { VesselId = "vsl_x" };

                await adapter.EvaluatePreflightAsync(SmallObjective(), BuildVessel(), preview, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, client.CallCount);
                AssertEqual(0, preview.Issues.Count);
            }).ConfigureAwait(false);

            await RunTest("Preflight_Unavailable_NoIssue_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("timeout"));
                TypedPriorArtAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate), OneCandidate());
                ObjectiveDispatchPreview preview = new ObjectiveDispatchPreview { VesselId = "vsl_x" };

                await adapter.EvaluatePreflightAsync(SmallObjective(), BuildVessel(), preview, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, preview.Issues.Count);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Preflight_Shadow_NoIssue_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(0.99, 0.99, 0.0));
                TypedPriorArtAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow), OneCandidate());
                ObjectiveDispatchPreview preview = new ObjectiveDispatchPreview { VesselId = "vsl_x" };

                await adapter.EvaluatePreflightAsync(SmallObjective(), BuildVessel(), preview, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, preview.Issues.Count);
                AssertEqual(1, client.CallCount);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Preflight_Gate_AlreadyDone_AddsErrorIssue_RecordsGated", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(0.95, 0.0, 0.0));
                TypedPriorArtAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate), OneCandidate());
                ObjectiveDispatchPreview preview = new ObjectiveDispatchPreview { VesselId = "vsl_x" };

                await adapter.EvaluatePreflightAsync(SmallObjective(), BuildVessel(), preview, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(HasIssue(preview, TypedPriorArtAdapter.AlreadyDoneIssueCode), "already_done adds the found Error issue");
                ObjectiveDispatchPreviewIssue issue = preview.Issues.First(i => i.Code == TypedPriorArtAdapter.AlreadyDoneIssueCode);
                AssertEqual(ReadinessSeverityEnum.Error, issue.Severity);
                AssertTrue(issue.Message.Contains("src/Existing.cs:12", StringComparison.Ordinal), "the issue cites the candidate path:line");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Preflight_Gate_Integrate_AddsAdvisoryIssue", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(0.0, 0.95, 0.0));
                TypedPriorArtAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate), OneCandidate());
                ObjectiveDispatchPreview preview = new ObjectiveDispatchPreview { VesselId = "vsl_x" };

                await adapter.EvaluatePreflightAsync(SmallObjective(), BuildVessel(), preview, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(HasIssue(preview, TypedPriorArtAdapter.IntegrateIssueCode), "integrate adds the seams-to-consume advisory");
                AssertTrue(!HasIssue(preview, TypedPriorArtAdapter.AlreadyDoneIssueCode), "already_done does not fire when low");
            }).ConfigureAwait(false);

            await RunTest("Preflight_Gate_BelowThreshold_NoIssue_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(0.30, 0.30, 0.30));
                TypedPriorArtAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate), OneCandidate());
                ObjectiveDispatchPreview preview = new ObjectiveDispatchPreview { VesselId = "vsl_x" };

                await adapter.EvaluatePreflightAsync(SmallObjective(), BuildVessel(), preview, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, preview.Issues.Count);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Preflight_UncertainBand_LargeObjective_RecommendsAnalystStage", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // already_done in [0.4, 0.7] and no other signal: no found/integrate issue, but a large
                // objective earns the read-only analyst-stage recommendation.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(0.55, 0.0, 0.0));
                TypedPriorArtAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate), OneCandidate());
                ObjectiveDispatchPreview preview = new ObjectiveDispatchPreview { VesselId = "vsl_x" };

                await adapter.EvaluatePreflightAsync(LargeObjective(), BuildVessel(), preview, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(HasIssue(preview, TypedPriorArtAdapter.AnalystStageIssueCode), "the uncertain band on a large objective recommends the analyst stage");
                AssertTrue(!HasIssue(preview, TypedPriorArtAdapter.AlreadyDoneIssueCode), "the found Error does not fire in the uncertain band");
            }).ConfigureAwait(false);

            await RunTest("Preflight_UncertainBand_SmallObjective_NoRecommendation", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(0.55, 0.0, 0.0));
                TypedPriorArtAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate), OneCandidate());
                ObjectiveDispatchPreview preview = new ObjectiveDispatchPreview { VesselId = "vsl_x" };

                await adapter.EvaluatePreflightAsync(SmallObjective(), BuildVessel(), preview, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!HasIssue(preview, TypedPriorArtAdapter.AnalystStageIssueCode), "a small objective in the band earns no stage recommendation");
            }).ConfigureAwait(false);

            await RunTest("Judge_Gate_Reimplements_ReturnsReviewInstruction_RecordsGated", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(0.0, 0.0, 0.95));
                TypedPriorArtAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate), OneCandidate());
                Mission mission = new Mission { Id = "msn_x", VesselId = "vsl_x", Persona = "Worker" };

                string? instruction = await adapter.EvaluateJudgeInstructionAsync(mission, BuildVessel(), "public class NewThing()", CancellationToken.None).ConfigureAwait(false);

                AssertTrue(instruction != null, "a re-implementation returns a review instruction");
                AssertTrue(instruction!.Contains("src/Existing.cs:12", StringComparison.Ordinal), "the instruction cites the candidate path:line");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Judge_Gate_BelowThreshold_ReturnsNull_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(0.0, 0.0, 0.20));
                TypedPriorArtAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate), OneCandidate());
                Mission mission = new Mission { Id = "msn_x", VesselId = "vsl_x", Persona = "Worker" };

                string? instruction = await adapter.EvaluateJudgeInstructionAsync(mission, BuildVessel(), "public class NewThing()", CancellationToken.None).ConfigureAwait(false);

                AssertTrue(instruction == null, "a low reimplements returns no instruction");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("NeverThrows_ClientThrows_NoIssue_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                TypedPriorArtAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate), OneCandidate());
                ObjectiveDispatchPreview preview = new ObjectiveDispatchPreview { VesselId = "vsl_x" };

                await adapter.EvaluatePreflightAsync(SmallObjective(), BuildVessel(), preview, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, preview.Issues.Count);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("CallerTokenReachesClient_AndDecisionPoint", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Answer(0.95, 0.0, 0.0));
                TypedPriorArtAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate), OneCandidate());
                ObjectiveDispatchPreview preview = new ObjectiveDispatchPreview { VesselId = "vsl_x" };

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.EvaluatePreflightAsync(SmallObjective(), BuildVessel(), preview, cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken == cts.Token, "adapter forwards the caller token");
                AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
            }).ConfigureAwait(false);
        }
    }
}
