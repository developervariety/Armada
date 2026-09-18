namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.TypedDecisions;
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
                // The gate reads the raw noul, as the built-in decisions do: 0.6 is below the 0.9 threshold.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("matches_intent", 0.6));
                CustomTypedDecisionAdapter adapter = new CustomTypedDecisionAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());

                CustomDecisionOutcome outcome = await adapter.RunAsync("example_decision", Context(), null, null, CancellationToken.None).ConfigureAwait(false);
                AssertEqual("recorded", outcome.Status, "below threshold records without flagging");
                AssertFalse(outcome.DidFlag, "and never flags");
            });

            await RunTest("Adapter_ConfidentlyFalseNoul_DoesNotFlag", async () =>
            {
                // A noul is the probability that its statement (the finding) is true. 0.02 is a
                // confident "no finding"; reading it as distance from 0.5 gave 0.96 and flagged it.
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionSettings settings = GatedSettings(CustomDecisionSeamEnum.MissionDiffFlag);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("matches_intent", 0.02));
                CustomTypedDecisionAdapter adapter = new CustomTypedDecisionAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());

                CustomDecisionOutcome outcome = await adapter.RunAsync("example_decision", Context(), null, null, CancellationToken.None).ConfigureAwait(false);
                AssertEqual("recorded", outcome.Status, "a confidently false finding records without flagging");
                AssertFalse(outcome.DidFlag, "and never flags");
                AssertEqual<double?>(0.02, outcome.Confidence, "the gate value is the raw noul");
            });

            await RunTest("Adapter_ChoiceAnswer_NeverGates", async () =>
            {
                // A custom definition does not say which option is the finding, so a confident choice
                // cannot flag; it is recorded and returned to the caller only.
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionSettings settings = GatedSettings(CustomDecisionSeamEnum.MissionDiffFlag);
                settings.Custom["example_decision"].Questions = new List<CustomTypedQuestionSettings>
                {
                    new CustomTypedQuestionSettings
                    {
                        Id = "kind", Type = "choice", Instructions = "what kind of change",
                        Options = new Dictionary<string, string> { ["clean"] = "no finding", ["risky"] = "a finding" }
                    }
                };
                TypedDecisionResult choice = new TypedDecisionResult
                {
                    Available = true,
                    Answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
                    {
                        ["kind"] = new TypedAnswer { Type = "choice", Choice = "clean", Confidence = 0.99 }
                    }
                };
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(choice);
                CustomTypedDecisionAdapter adapter = new CustomTypedDecisionAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());

                CustomDecisionOutcome outcome = await adapter.RunAsync("example_decision", Context(), null, null, CancellationToken.None).ConfigureAwait(false);
                AssertEqual(1, client.CallCount, "the provider is consulted");
                AssertEqual("recorded", outcome.Status, "a confident choice records without flagging");
                AssertFalse(outcome.DidFlag, "and never flags");
                AssertNull(outcome.Confidence, "a decision with no noul has no gate value");
            });

            await RunTest("Adapter_Flag_RecordsGatedEventUnderCustomDecisionPoint", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionSettings settings = GatedSettings(CustomDecisionSeamEnum.MissionDiffFlag);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("matches_intent", 0.97));
                CustomTypedDecisionAdapter adapter = new CustomTypedDecisionAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());

                await adapter.RunAsync("example_decision", Context(), null, null, CancellationToken.None).ConfigureAwait(false);

                List<ArmadaEvent> gated = await db.Driver.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false);
                AssertEqual(1, gated.Count, "a flag is one typed_decision.gated event");
                AssertContains("\"decision\":\"custom:example_decision\"", gated[0].Payload ?? "", "under the custom decision point");
                List<ArmadaEvent> shadow = await db.Driver.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false);
                AssertEqual(0, shadow.Count, "and no second event");
            });

            await RunTest("Adapter_RetainState_RetainsTheCustomDecisionsSample", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                string dataDirectory = Path.Combine(Path.GetTempPath(), "armada-custom-retain-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dataDirectory);
                try
                {
                    TypedDecisionSettings settings = GatedSettings(CustomDecisionSeamEnum.MissionDiffFlag);
                    settings.Retention.Enabled = true;
                    TypedDecisionSampleStore store = new TypedDecisionSampleStore(dataDirectory, new LoggingModule());
                    TypedDecisionRecorder recorder = new TypedDecisionRecorder(db.Driver, new LoggingModule(), store, () => settings);
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("matches_intent", 0.4));
                    CustomTypedDecisionAdapter adapter = new CustomTypedDecisionAdapter(client, recorder, settings, new LoggingModule());
                    string folder = Path.Combine(store.RootPath, "custom_example_decision");

                    await adapter.RunAsync("example_decision", Context(), null, null, CancellationToken.None).ConfigureAwait(false);
                    AssertFalse(Directory.Exists(folder), "a custom decision that has not opted in retains nothing");

                    settings.Custom["example_decision"].RetainState = true;
                    await adapter.RunAsync("example_decision", Context(), null, null, CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(Directory.Exists(folder), "the custom decision's own retainState retains its call");
                    string[] files = Directory.GetFiles(folder, "*.jsonl");
                    AssertEqual(1, files.Length, "one day file");
                    TypedDecisionSample? sample = JsonSerializer.Deserialize<TypedDecisionSample>(
                        File.ReadAllLines(files[0])[0], new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
                    AssertNotNull(sample, "the line is a sample");
                    AssertEqual("custom:example_decision", sample!.DecisionPoint, "keyed by the custom decision point");
                    AssertContains("-a", sample.RedactedState, "carrying the redacted state");
                }
                finally
                {
                    try { Directory.Delete(dataDirectory, true); } catch (IOException) { }
                }
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
