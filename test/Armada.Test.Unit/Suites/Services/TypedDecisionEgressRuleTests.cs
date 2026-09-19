namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// The egress rules every path that sends decision state off the host must share: the content markers, the
    /// retry of a request the provider rejected as too large, and the per-candidate filter context compaction
    /// applies. The central case sends ONE marked state through the three egress paths - the adapter skeleton,
    /// a custom decision and a captain tool - and requires all three to refuse it, because a guard held by
    /// three of four callers reads as held by all four.
    /// </summary>
    public class TypedDecisionEgressRuleTests : TestSuite
    {
        private sealed class RejectsBatchesClient : Armada.Core.Services.Interfaces.ITypedDecisionClient
        {
            public int CallCount { get; private set; }

            public Task<TypedDecisionResult> DecideAsync(TypedDecisionRequest request, CancellationToken token)
            {
                CallCount++;
                bool batched = request.Questions.Keys.Any(key => key.StartsWith("item", StringComparison.Ordinal) && key.Contains("__", StringComparison.Ordinal));
                if (batched) return Task.FromResult(new TypedDecisionResult { Available = false, UnavailableReason = "http_400" });
                Dictionary<string, TypedAnswer> answers = request.Questions.Keys.ToDictionary(
                    key => key, key => new TypedAnswer { Type = "noul", Noul = 0.5 }, StringComparer.Ordinal);
                return Task.FromResult(new TypedDecisionResult { Available = true, Answers = answers });
            }
        }

        /// <inheritdoc />
        public override string Name => "Typed Decision Egress Rules";

        // An absolute path under a workspace root, where dock and sibling-checkout paths live: the redactor
        // replaces it with a placeholder, so only a check of the unredacted state can see the marker in it.
        private const string MarkedBody = "var xml = File.ReadAllText(\"/home/user/docks/sibling/private-export/data/records.xml\");";
        private const string CleanBody = "AssertEqual(0x08, decoder.Decode(frame));";
        private const string Marker = "Private-Export";

        private static ArmadaSettings Settings(IEnumerable<string> markers)
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.TypedDecisions.EgressExcludedMarkers = markers.ToList();
            settings.TypedDecisions.CaptainTool.Enabled = true;
            settings.TypedDecisions.Decisions["test_covers"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.85 };
            settings.TypedDecisions.Custom["fidelity"] = new CustomTypedDecisionSettings
            {
                Mode = TypedDecisionModeEnum.Gate,
                GateThreshold = 0.9,
                Description = "test",
                Surface = CustomDecisionSurfaceEnum.MissionDiff,
                StateFields = new List<string> { "diff" },
                Questions = new List<CustomTypedQuestionSettings>
                {
                    new CustomTypedQuestionSettings { Id = "departs", Type = "noul", Instructions = "departs from source", TrueMeaning = "yes", FalseMeaning = "no" }
                }
            };
            return settings;
        }

        private static TestCoversDecisionInput CoversInput(string body)
        {
            return new TestCoversDecisionInput
            {
                Mission = new Mission { Id = "msn_egress", VesselId = "vsl_egress", Title = "port a decoder", Persona = "Test Engineer" },
                AddedTests = new List<TestCoversMethod> { new TestCoversMethod("Decode_ReadsTheRecords", body) },
                Symptom = "The decoder truncates the counter."
            };
        }

        private static async Task<(int skeleton, int custom, int captain)> SendThroughAllThreeAsync(TestDatabase db, ArmadaSettings settings, string body)
        {
            // Each path gets its own counting client, so a call on one path cannot hide a refusal on another.
            FakeTypedDecisionClient skeletonClient = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("covers_symptom_1", 0.9));
            TypedTestCoversAdapter skeleton = new TypedTestCoversAdapter(skeletonClient, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings.TypedDecisions, new LoggingModule());
            await skeleton.DecideAsync(CoversInput(body), TestCoversVerdict.NoInstructions(), CancellationToken.None).ConfigureAwait(false);

            FakeTypedDecisionClient customClient = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("departs", 0.1));
            CustomTypedDecisionAdapter custom = new CustomTypedDecisionAdapter(customClient, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings.TypedDecisions, new LoggingModule());
            await custom.RunAsync("fidelity", new Dictionary<string, object?> { ["diff"] = "+ " + body }, null, null, CancellationToken.None).ConfigureAwait(false);

            FakeTypedDecisionClient captainClient = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("cause", 0.1));
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);
            McpTypedDecisionTools.Register((name, _, _, handler) => { handlers[name] = handler; }, db.Driver, captainClient,
                new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
            JsonElement args = JsonSerializer.SerializeToElement(new
            {
                state = body,
                questions = new { cause = new { type = "noul", instructions = "Is this a source read?" } }
            });
            AuthContext caller = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer");
            using (McpCallerContext.Begin(caller))
                await handlers["armada_typed_decision"](args).ConfigureAwait(false);

            return (skeletonClient.CallCount, customClient.CallCount, captainClient.CallCount);
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("OneMarkedState_IsRefusedByTheSkeleton_ACustomDecision_AndACaptainTool", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                (int skeleton, int custom, int captain) = await SendThroughAllThreeAsync(db, Settings(new[] { Marker }), MarkedBody).ConfigureAwait(false);
                AssertEqual(0, skeleton, "the adapter skeleton sends nothing");
                AssertEqual(0, custom, "a custom decision sends nothing");
                AssertEqual(0, captain, "a captain tool sends nothing");
            }).ConfigureAwait(false);

            await RunTest("OneCleanState_IsSentByAllThree", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                (int skeleton, int custom, int captain) = await SendThroughAllThreeAsync(db, Settings(new[] { Marker }), CleanBody).ConfigureAwait(false);
                AssertEqual(1, skeleton, "the skeleton sends a clean state");
                AssertEqual(1, custom, "so does a custom decision");
                AssertEqual(1, captain, "and a captain tool");
            }).ConfigureAwait(false);

            await RunTest("TheMarkerIsFoundInTheUnredactedState_WhichTheRedactorWouldHide", () =>
            {
                // The point of checking before redaction, measured on the same text the paths send.
                string redacted = DecisionStateRedactor.Redact(MarkedBody, 60000);
                AssertTrue(redacted.IndexOf("private-export", StringComparison.OrdinalIgnoreCase) < 0,
                    "the redactor removes a workspace-root path, marker and all: " + redacted);
                AssertEqual(Marker, TypedDecisionSettings.FirstMarkerIn(new List<string> { Marker }, TypedDecisionEgress.RawText(MarkedBody)),
                    "the raw state still carries it, compared without case");
            });

            await RunTest("ADecisionsOwnList_ReplacesTheGlobalOne_AndAnEmptyListOptsOut", () =>
            {
                TypedDecisionSettings settings = new TypedDecisionSettings { EgressExcludedMarkers = new List<string> { "global-marker" } };
                settings.Decisions["preflight"].EgressExcludedMarkers = new List<string>();
                settings.Decisions["change_quality"].EgressExcludedMarkers = new List<string> { "own-marker" };
                AssertEqual("global-marker", settings.ExcludedMarkerIn("failure_cause", "x global-marker y"), "an unset decision inherits the global list");
                AssertEqual(null, settings.ExcludedMarkerIn("preflight", "x global-marker y"), "an empty list opts a brief-only decision out");
                AssertEqual(null, settings.ExcludedMarkerIn("change_quality", "x global-marker y"), "an own list replaces the global one");
                AssertEqual("own-marker", settings.ExcludedMarkerIn("change_quality", "x own-marker y"));
                AssertEqual("global-marker", settings.ExcludedMarkerIn("captain_tool_without_entry", "global-marker"), "a key with no entry takes the global list");
            });

            await RunTest("TheMarkers_SurviveAHotReload_InTheGlobalListAndInACustomDefinition", () =>
            {
                // A reload copies settings in place and clones every custom definition: a field left out of
                // either copy is silently lost on every reload.
                ArmadaSettings live = new ArmadaSettings();
                ArmadaSettings file = Settings(new[] { Marker });
                file.TypedDecisions.Custom["fidelity"].EgressExcludedMarkers = new List<string> { "custom-only" };
                live.ApplyHotReloadableFrom(file);
                AssertEqual(Marker, live.TypedDecisions.ExcludedMarkerIn("failure_cause", "x private-export y"), "the global list reaches the live object");
                AssertEqual("custom-only", TypedDecisionSettings.FirstMarkerIn(live.TypedDecisions.MarkersForCustom("fidelity"), "custom-only"),
                    "a custom definition's own list survives the clone");
                file.TypedDecisions.EgressExcludedMarkers.Add("added-later");
                AssertEqual(null, live.TypedDecisions.ExcludedMarkerIn("failure_cause", "added-later"), "the live list is a copy");
            });

            await RunTest("ARejectedRequest_IsRetriedOnceAtHalfTheState", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                List<int> sizes = new List<int>();
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(request =>
                {
                    sizes.Add(FakeTypedDecisionClient.StateText(request).Length);
                    return sizes.Count == 1
                        ? FakeTypedDecisionClient.Unavailable("http_400")
                        : FakeTypedDecisionClient.Noul("covers_symptom_1", 0.1);
                });
                TypedTestCoversAdapter adapter = new TypedTestCoversAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), Settings(new string[0]).TypedDecisions, new LoggingModule());
                await adapter.DecideAsync(CoversInput(new string('x', 20000)), TestCoversVerdict.NoInstructions(), CancellationToken.None).ConfigureAwait(false);
                AssertEqual(2, client.CallCount, "one retry after a rejection");
                AssertTrue(sizes[1] < sizes[0], "the retry sends a smaller state (" + sizes[0] + " -> " + sizes[1] + ")");
            }).ConfigureAwait(false);

            await RunTest("AnyOtherUnavailableReason_IsNotRetried", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("http_429"));
                TypedTestCoversAdapter adapter = new TypedTestCoversAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), Settings(new string[0]).TypedDecisions, new LoggingModule());
                await adapter.DecideAsync(CoversInput(new string('x', 20000)), TestCoversVerdict.NoInstructions(), CancellationToken.None).ConfigureAwait(false);
                AssertEqual(1, client.CallCount, "a rate limit is not a size problem, so halving would only repeat it");
            }).ConfigureAwait(false);

            await RunTest("ARejectedBatch_IsSplit_AndEachItemStillAnswered", async () =>
            {
                // A client that sees the whole batched request, as the provider does: it rejects any request that
                // carries more than one item, and answers a single one.
                RejectsBatchesClient client = new RejectsBatchesClient();
                List<TypedDecisionBatchItem> items = new List<TypedDecisionBatchItem>
                {
                    new TypedDecisionBatchItem(DecisionStateRedactor.RedactState(new Dictionary<string, object?> { ["text"] = "first" }, 1000),
                        new Dictionary<string, TypedQuestion>(StringComparer.Ordinal) { ["q1"] = new NoulQuestion("true?") }),
                    new TypedDecisionBatchItem(DecisionStateRedactor.RedactState(new Dictionary<string, object?> { ["text"] = "second" }, 1000),
                        new Dictionary<string, TypedQuestion>(StringComparer.Ordinal) { ["q1"] = new NoulQuestion("true?") })
                };
                List<TypedDecisionResult> results = await TypedDecisionBatcher.DecideAllAsync(client, "inbox_triage", items, 60000, CancellationToken.None).ConfigureAwait(false);
                AssertEqual(3, client.CallCount, "the rejected pair, then each half");
                AssertEqual(2, results.Count);
                AssertTrue(results.All(r => r.Available), "both items are answered after the split");
            }).ConfigureAwait(false);

            await RunTest("Compaction_DropsAMarkedCandidate_AndMapsSparedPositionsBack", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // After the marked candidate is removed, question slot 1 is the caller's position 1.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("keep_result_1", 0.95));
                TypedDecisionSettings settings = Settings(new[] { Marker }).TypedDecisions;
                settings.Decisions["context_compaction"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.55 };
                TypedContextCompactionAdapter adapter = new TypedContextCompactionAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());

                ContextCompactionVerdict verdict = await adapter.DecideAllowedAsync(new ContextCompactionDecisionInput
                {
                    Goal = "port the record reader",
                    Candidates = new List<ContextCompactionCandidate>
                    {
                        new ContextCompactionCandidate("read", "read private-export/data/records.xml", "<records>...", 40000, 30),
                        new ContextCompactionCandidate("bash", "count the parser copies", "11 matches across 7 files", 2048, 20),
                        new ContextCompactionCandidate("bash", "list the tests", "test_one.py", 900, 10)
                    }
                }, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, client.CallCount);
                AssertEqual(4, client.LastRequest!.Questions.Count, "only the two unmarked candidates are asked about, two questions each");
                AssertFalse(FakeTypedDecisionClient.StateText(client.LastRequest).Contains("<records>"), "the marked output never reaches the request");
                AssertEqual(1, verdict.SparedPositions.Count);
                AssertEqual(1, verdict.SparedPositions[0], "the spared slot maps back to the caller's position");
            }).ConfigureAwait(false);

            await RunTest("Compaction_WithEveryCandidateMarked_AsksNothing", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("keep_result_1", 0.95));
                TypedDecisionSettings settings = Settings(new[] { Marker }).TypedDecisions;
                settings.Decisions["context_compaction"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.55 };
                TypedContextCompactionAdapter adapter = new TypedContextCompactionAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
                ContextCompactionVerdict verdict = await adapter.DecideAllowedAsync(new ContextCompactionDecisionInput
                {
                    Candidates = new List<ContextCompactionCandidate>
                    {
                        new ContextCompactionCandidate("read", "read /x/private-export/a.xml", "<a/>", 900, 5)
                    }
                }, CancellationToken.None).ConfigureAwait(false);
                AssertEqual(0, client.CallCount, "nothing left to ask about means nothing is sent");
                AssertFalse(verdict.HasSpared);
            }).ConfigureAwait(false);
        }
    }
}
