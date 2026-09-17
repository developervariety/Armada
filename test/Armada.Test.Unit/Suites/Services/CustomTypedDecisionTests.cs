namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
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
    /// The user-defined custom typed decision: it hot-reloads with the settings, its effective mode is
    /// capped by the global mode, and its adapter is advisory — it flags only when bound and gated at
    /// or above threshold, and never otherwise. These tests pin the safety property (an unbound or
    /// below-threshold decision never flags) and the plumbing (a custom decision survives a hot reload).
    /// </summary>
    public class CustomTypedDecisionTests : TestSuite
    {
        public override string Name => "Custom Typed Decision";

        private static TypedDecisionSettings GatedSettings(CustomDecisionSeamEnum binding, TypedDecisionModeEnum mode = TypedDecisionModeEnum.Gate)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.KeyAvailable = () => true;
            settings.Custom["example_decision"] = new CustomTypedDecisionSettings
            {
                Mode = mode,
                GateThreshold = 0.9,
                Description = "test",
                Surface = CustomDecisionSurfaceEnum.MissionDiff,
                Binding = binding,
                StateFields = new List<string> { "diff" },
                Questions = new List<CustomTypedQuestionSettings>
                {
                    new CustomTypedQuestionSettings { Id = "matches_intent", Type = "noul", Instructions = "matches source", TrueMeaning = "yes", FalseMeaning = "no" }
                }
            };
            return settings;
        }

        private static Dictionary<string, object?> Context() => new Dictionary<string, object?> { ["diff"] = "@@ -1 +1 @@\n-a\n+b" };

        protected override async Task RunTestsAsync()
        {
            await RunTest("HotReload_CarriesCustomDecisions", () =>
            {
                ArmadaSettings current = new ArmadaSettings();
                AssertEqual(0, current.TypedDecisions.Custom.Count, "no custom decisions by default");

                ArmadaSettings incoming = new ArmadaSettings();
                incoming.TypedDecisions.Custom["x"] = new CustomTypedDecisionSettings { Description = "carried", Mode = TypedDecisionModeEnum.Shadow };
                current.ApplyHotReloadableFrom(incoming);

                AssertTrue(current.TypedDecisions.Custom.ContainsKey("x"), "a hot reload carries the custom decision");
                AssertEqual("carried", current.TypedDecisions.Custom["x"].Description, "with its fields");
            });

            await RunTest("ForCustom_EffectiveModeIsMinOfGlobalAndDecision", () =>
            {
                TypedDecisionSettings settings = GatedSettings(CustomDecisionSeamEnum.None, TypedDecisionModeEnum.Gate);
                settings.Mode = TypedDecisionModeEnum.Shadow;
                AssertEqual(TypedDecisionModeEnum.Shadow, settings.ForCustom("example_decision").Mode, "the global cap wins");
                AssertEqual(TypedDecisionModeEnum.Off, settings.ForCustom("nope").Mode, "an unknown custom decision is Off");
            });

            await RunTest("Adapter_Off_MakesNoCallAndDoesNotFlag", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionSettings settings = GatedSettings(CustomDecisionSeamEnum.MissionDiffFlag, TypedDecisionModeEnum.Off);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("matches_intent", 0.97));
                CustomTypedDecisionAdapter adapter = new CustomTypedDecisionAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());

                CustomDecisionOutcome outcome = await adapter.RunAsync("example_decision", Context(), null, null, CancellationToken.None).ConfigureAwait(false);
                AssertEqual("inactive", outcome.Status, "an Off decision is inactive");
                AssertEqual(0, client.CallCount, "and never calls the provider");
                AssertFalse(outcome.DidFlag, "and never flags");
            });

            await RunTest("Adapter_GateBoundHighConfidence_Flags", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionSettings settings = GatedSettings(CustomDecisionSeamEnum.MissionDiffFlag);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("matches_intent", 0.97));
                CustomTypedDecisionAdapter adapter = new CustomTypedDecisionAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());

                CustomDecisionOutcome outcome = await adapter.RunAsync("example_decision", Context(), null, null, CancellationToken.None).ConfigureAwait(false);
                AssertEqual("flagged", outcome.Status, "a bound decision gated above threshold flags");
                AssertTrue(outcome.DidFlag, "and DidFlag is true");
            });

            await RunTest("Adapter_GateUnbound_NeverFlags", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionSettings settings = GatedSettings(CustomDecisionSeamEnum.None);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("matches_intent", 0.99));
                CustomTypedDecisionAdapter adapter = new CustomTypedDecisionAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());

                CustomDecisionOutcome outcome = await adapter.RunAsync("example_decision", Context(), null, null, CancellationToken.None).ConfigureAwait(false);
                AssertEqual("recorded", outcome.Status, "an unbound decision records but does not flag, even at high confidence");
                AssertFalse(outcome.DidFlag, "the safety property: no binding, no action");
            });

            await RunTest("Adapter_BelowThreshold_DoesNotFlag", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionSettings settings = GatedSettings(CustomDecisionSeamEnum.MissionDiffFlag);
                // noul 0.6 -> confidence |0.6-0.5|*2 = 0.2, below the 0.9 threshold.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("matches_intent", 0.6));
                CustomTypedDecisionAdapter adapter = new CustomTypedDecisionAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());

                CustomDecisionOutcome outcome = await adapter.RunAsync("example_decision", Context(), null, null, CancellationToken.None).ConfigureAwait(false);
                AssertEqual("recorded", outcome.Status, "below threshold records without flagging");
                AssertFalse(outcome.DidFlag, "and never flags");
            });

            await RunTest("Adapter_Unavailable_DoesNotFlag", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionSettings settings = GatedSettings(CustomDecisionSeamEnum.MissionDiffFlag);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("timeout"));
                CustomTypedDecisionAdapter adapter = new CustomTypedDecisionAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());

                CustomDecisionOutcome outcome = await adapter.RunAsync("example_decision", Context(), null, null, CancellationToken.None).ConfigureAwait(false);
                AssertEqual("unavailable", outcome.Status, "an unavailable provider yields unavailable");
                AssertFalse(outcome.DidFlag, "and never flags");
            });

            await RunTest("Adapter_NotFound_IsNotFound", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionSettings settings = GatedSettings(CustomDecisionSeamEnum.MissionDiffFlag);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("matches_intent", 0.97));
                CustomTypedDecisionAdapter adapter = new CustomTypedDecisionAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());

                CustomDecisionOutcome outcome = await adapter.RunAsync("missing", Context(), null, null, CancellationToken.None).ConfigureAwait(false);
                AssertEqual("not_found", outcome.Status, "an unknown decision is not_found");
                AssertEqual(0, client.CallCount, "and never calls the provider");
            });

            await RunTest("DescribeRequest_SelectsStateFieldsAndBuildsQuestions", () =>
            {
                TypedDecisionSettings settings = GatedSettings(CustomDecisionSeamEnum.MissionDiffFlag);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("matches_intent", 0.97));
                CustomTypedDecisionAdapter adapter = new CustomTypedDecisionAdapter(client, new TypedDecisionRecorder(new Armada.Core.Database.Sqlite.SqliteDatabaseDriver("Data Source=:memory:", new LoggingModule()), new LoggingModule()), settings, new LoggingModule());

                Dictionary<string, object?> context = new Dictionary<string, object?> { ["diff"] = "the-diff", ["ignored"] = "x" };
                TypedDecisionBatchItem? item = adapter.DescribeRequest("example_decision", context);
                AssertNotNull(item, "a request is built");
                AssertTrue(item!.Questions.ContainsKey("matches_intent"), "the question is present");
                AssertTrue(FakeTypedDecisionClient.StateText(new TypedDecisionRequest { DecisionPoint = "x", State = item.State.State, Questions = item.Questions }).Contains("the-diff"), "the selected state field is included");
            });
        }
    }
}
