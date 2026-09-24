namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// The egress rules every path that sends decision state off the host must share: the content markers
    /// and the retry of a request the provider rejected as too large. The central case sends ONE marked
    /// state through the three egress paths - the adapter skeleton, a custom decision and a captain tool -
    /// and requires all three to refuse it.
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


        // The three shapes of one decision call a standalone adapter is driven with: a state that may leave the
        // host, a state about a mission or vessel on the exclusion list, and a state whose unredacted text names
        // an excluded marker.
        private enum EgressCase
        {
            Clean,
            ExcludedVessel,
            MarkedContent
        }

        private const string ExcludedVessel = "vsl_excluded";
        private const string AllowedVessel = "vsl_allowed";

        // One standalone decision seam: the decision it belongs to, whether it concerns a mission or vessel (so
        // the vessel exclusion applies), and how to drive it once with a given case.
        private sealed class StandaloneSender
        {
            public StandaloneSender(string decisionPoint, string seam, bool aboutVessel, Func<DatabaseDriver, TypedDecisionSettings, ITypedDecisionClient, string, string, Task> send)
            {
                DecisionPoint = decisionPoint;
                Seam = seam;
                AboutVessel = aboutVessel;
                Send = send;
            }

            public string DecisionPoint { get; }
            public string Seam { get; }
            public bool AboutVessel { get; }

            // (database, settings, client, vesselId, body)
            public Func<DatabaseDriver, TypedDecisionSettings, ITypedDecisionClient, string, string, Task> Send { get; }
        }

        private sealed class NoCandidateRouter : IFollowUpRouter
        {
            public Task<IReadOnlyList<FollowUpDuplicateCandidate>> GetDuplicateCandidatesAsync(string? vesselId, int limit, CancellationToken token)
                => Task.FromResult<IReadOnlyList<FollowUpDuplicateCandidate>>(new List<FollowUpDuplicateCandidate>());
            public Task<string?> CreateTriagedObjectiveAsync(FollowUpRouteRequest request, CancellationToken token) => Task.FromResult<string?>(null);
            public Task AppendEvidenceNoteAsync(FollowUpRouteRequest request, CancellationToken token) => Task.CompletedTask;
            public Task LinkDuplicateAsync(FollowUpRouteRequest request, string existingObjectiveId, CancellationToken token) => Task.CompletedTask;
            public Task FlagBlockingForOperatorAsync(FollowUpRouteRequest request, CancellationToken token) => Task.CompletedTask;
        }

        private static LoggingModule Quiet()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static Vessel VesselOf(string vesselId)
        {
            return new Vessel { Id = vesselId, Name = "ExampleVessel", LocalPath = "/repo", DefaultBranch = "main" };
        }

        private static PapercutGroup PapercutOf(string vesselId, string key, string detail)
        {
            return new PapercutGroup
            {
                Key = vesselId + "|BriefContradiction|" + key,
                VesselId = vesselId,
                Category = PapercutCategoryEnum.BriefContradiction,
                HighestSeverity = PapercutSeverityEnum.High,
                SampleTitle = "The brief contradicts the decoder layout " + key,
                SampleDetail = detail,
                Count = 3,
                DistinctCaptainCount = 2,
                LastSeenUtc = DateTime.UtcNow
            };
        }

        // Every decision that sends state from its own flow rather than through the adapter skeleton, one row
        // per seam. A row drives the real adapter with the case's vessel and body.
        private static List<StandaloneSender> StandaloneSenders()
        {
            return new List<StandaloneSender>
            {
                new StandaloneSender(TypedPriorArtAdapter.DecisionPoint, "preflight", true, async (db, settings, client, vesselId, body) =>
                {
                    TypedPriorArtAdapter adapter = new TypedPriorArtAdapter(client, new TypedDecisionRecorder(db, Quiet()), settings,
                        new FakePriorArtRetriever(FakePriorArtRetriever.RetrievalOf(FakePriorArtRetriever.Candidate(PriorArtWhereEnum.Landed, "src/Existing.cs:12", body))), Quiet());
                    await adapter.EvaluatePreflightAsync(new Objective { Id = "obj_egress", Title = "Port the decoder", Description = body },
                        VesselOf(vesselId), new ObjectiveDispatchPreview { VesselId = vesselId }, CancellationToken.None).ConfigureAwait(false);
                }),
                new StandaloneSender(TypedPriorArtAdapter.DecisionPoint, "judge", true, async (db, settings, client, vesselId, body) =>
                {
                    TypedPriorArtAdapter adapter = new TypedPriorArtAdapter(client, new TypedDecisionRecorder(db, Quiet()), settings,
                        new FakePriorArtRetriever(FakePriorArtRetriever.RetrievalOf(FakePriorArtRetriever.Candidate(PriorArtWhereEnum.Landed, "src/Existing.cs:12", body))), Quiet());
                    await adapter.EvaluateJudgeInstructionAsync(new Mission { Id = "msn_egress", VesselId = vesselId, Title = "port a decoder" },
                        VesselOf(vesselId), "class Decoder " + body, CancellationToken.None).ConfigureAwait(false);
                }),
                new StandaloneSender(RecorderMemoryReviewAdapter.DecisionPoint, "recorder", true, async (db, settings, client, vesselId, body) =>
                {
                    Mission mission = new Mission("Record lessons") { TenantId = Constants.DefaultTenantId, Persona = "Recorder", VesselId = vesselId };
                    await db.Memories.CreateAsync(new Memory
                    {
                        TenantId = Constants.DefaultTenantId,
                        Type = MemoryTypeEnum.Semantic,
                        Topic = "build",
                        Summary = "How the decoder is read",
                        Content = body,
                        Salience = 0.9,
                        SourceMissionId = mission.Id,
                        SourceKind = MemorySourceKindEnum.Mission,
                        VesselId = vesselId
                    }).ConfigureAwait(false);
                    RecorderMemoryReviewAdapter adapter = new RecorderMemoryReviewAdapter(settings, client, new TypedDecisionRecorder(db, Quiet()),
                        new DatabaseMemoryCandidateProposalWriter(db, Quiet()), db, Quiet());
                    await adapter.ReviewAsync(mission, CancellationToken.None).ConfigureAwait(false);
                }),
                new StandaloneSender(PapercutMergeAdapter.DecisionPoint, "listing", true, async (db, settings, client, vesselId, body) =>
                {
                    PapercutMergeAdapter adapter = new PapercutMergeAdapter(settings, client, new TypedDecisionRecorder(db, Quiet()), db, Quiet());
                    await adapter.MergeAsync(new List<PapercutGroup> { PapercutOf(vesselId, "a", body), PapercutOf(vesselId, "b", body) }, CancellationToken.None).ConfigureAwait(false);
                }),
                new StandaloneSender(MemoryCandidateAdapter.DecisionPoint, "sweep", true, async (db, settings, client, vesselId, body) =>
                {
                    MemoryCandidateAdapter adapter = new MemoryCandidateAdapter(settings, client, new TypedDecisionRecorder(db, Quiet()),
                        new DatabaseMemoryCandidateProposalWriter(db, Quiet()), Quiet());
                    await adapter.NominateAsync(new List<PapercutGroup> { PapercutOf(vesselId, "a", body) }, CancellationToken.None).ConfigureAwait(false);
                }),
                new StandaloneSender(FollowUpRoutingAdapter.DecisionPoint, "judge_follow_up", true, async (db, settings, client, vesselId, body) =>
                {
                    FollowUpRoutingAdapter adapter = new FollowUpRoutingAdapter(settings, client, new TypedDecisionRecorder(db, Quiet()), new NoCandidateRouter(), Quiet());
                    await adapter.RouteAsync(new JudgeFollowUp
                    {
                        JudgeMissionId = "msn_judge",
                        ReviewedMissionId = "msn_reviewed",
                        VesselId = vesselId,
                        JudgeVerdict = "PASS",
                        SuggestedFollowUps = "- " + body
                    }, "Port the decoder", CancellationToken.None).ConfigureAwait(false);
                }),
                new StandaloneSender(PreflightTextAdapter.DecisionPoint, "dispatch_preview", true, async (db, settings, client, vesselId, body) =>
                {
                    PreflightTextAdapter adapter = new PreflightTextAdapter(settings, client, new TypedDecisionRecorder(db, Quiet()), new FakeOwnerDecisionNotePoster(), Quiet());
                    await adapter.EvaluateAsync(new Objective { Id = "obj_egress", Title = "Port the decoder", Description = body, VesselIds = new List<string> { vesselId } },
                        VesselOf(vesselId), null, new ObjectiveDispatchPreview { VesselId = vesselId }, CancellationToken.None).ConfigureAwait(false);
                }),
                new StandaloneSender(CriteriaLintAdapter.DecisionPoint, "refinement_summary", false, async (db, settings, client, vesselId, body) =>
                {
                    CriteriaLintAdapter adapter = new CriteriaLintAdapter(settings, client, new TypedDecisionRecorder(db, Quiet()), Quiet());
                    await adapter.EvaluateAsync(new ObjectiveRefinementSummaryResponse
                    {
                        Summary = "Port the decoder.",
                        AcceptanceCriteria = new List<string> { body }
                    }, ObjectiveKindEnum.Feature, CancellationToken.None).ConfigureAwait(false);
                }),
                new StandaloneSender(InboxTriageAdapter.DecisionPoint, "inbox", false, async (db, settings, client, vesselId, body) =>
                {
                    InboxTriageAdapter adapter = new InboxTriageAdapter(settings, client, new TypedDecisionRecorder(db, Quiet()), Quiet());
                    await adapter.TriageInboxAsync(new List<InboxItem> { new InboxItem { Kind = "papercut", Title = "Decoder read", Detail = body } }, CancellationToken.None).ConfigureAwait(false);
                }),
                new StandaloneSender(InboxTriageAdapter.DecisionPoint, "board_notes", false, async (db, settings, client, vesselId, body) =>
                {
                    InboxTriageAdapter adapter = new InboxTriageAdapter(settings, client, new TypedDecisionRecorder(db, Quiet()), Quiet());
                    await adapter.TriageBoardNotesAsync(new List<BoardNoteTriageInput> { new BoardNoteTriageInput { Id = "note_1", AuthorType = "captain", Content = body } }, CancellationToken.None).ConfigureAwait(false);
                })
            };
        }

        private static TypedDecisionSettings StandaloneSettings()
        {
            TypedDecisionSettings settings = new TypedDecisionSettings
            {
                Mode = TypedDecisionModeEnum.Gate,
                EgressExcludedMarkers = new List<string> { Marker },
                EgressExcludedVesselIds = new List<string> { ExcludedVessel }
            };
            return settings;
        }

        private sealed class DriveResult
        {
            public int Calls { get; set; }
            public List<string> Reasons { get; set; } = new List<string>();
        }

        // Drive one seam with one case and return the provider calls and the unavailable reasons it recorded.
        private static async Task<DriveResult> DriveAsync(StandaloneSender sender, EgressCase egressCase)
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                // The provider is down for every call, so a sent state costs nothing downstream; the count of
                // calls is the measure of what would have left the host.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("http_429"));
                string vesselId = egressCase == EgressCase.ExcludedVessel ? ExcludedVessel : AllowedVessel;
                string body = egressCase == EgressCase.MarkedContent ? MarkedBody : CleanBody;
                await sender.Send(db.Driver, StandaloneSettings(), client, vesselId, body).ConfigureAwait(false);

                List<string> reasons = await UnavailableReasonsAsync(db.Driver).ConfigureAwait(false);
                return new DriveResult { Calls = client.CallCount, Reasons = reasons };
            }
        }

        // The unavailable reasons recorded, read from each event's message ("... unavailable=<reason>").
        private static async Task<List<string>> UnavailableReasonsAsync(DatabaseDriver database)
        {
            List<string> reasons = new List<string>();
            foreach (ArmadaEvent evt in await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeUnavailable, 100).ConfigureAwait(false))
            {
                int at = evt.Message.IndexOf("unavailable=", StringComparison.Ordinal);
                if (at >= 0) reasons.Add(evt.Message.Substring(at + "unavailable=".Length).Trim());
            }
            return reasons;
        }

        // The decision points the adapter skeleton serves, read from the adapters themselves: each concrete
        // subclass names its decision point as a constant, so an uninitialized instance can report it.
        private static HashSet<string> SkeletonDecisionPoints()
        {
            HashSet<string> points = new HashSet<string>(StringComparer.Ordinal);
            Type skeleton = typeof(TypedDecisionAdapterBase<,,>);
            foreach (Type type in skeleton.Assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsGenericTypeDefinition) continue;
                bool derives = false;
                for (Type? walk = type.BaseType; walk != null; walk = walk.BaseType)
                    if (walk.IsGenericType && walk.GetGenericTypeDefinition() == skeleton) { derives = true; break; }
                if (!derives) continue;

                PropertyInfo? property = type.GetProperty("DecisionPoint", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                object instance = RuntimeHelpers.GetUninitializedObject(type);
                if (property?.GetValue(instance) is string point) points.Add(point);
            }
            return points;
        }

        // Decisions whose only caller is a captain-facing helper tool: they send through the captain tool path,
        // which asks the shared egress guard for every call.
        private static readonly string[] CaptainToolDecisions = { "premise_check", "memory_record", "corpus_prelabel" };

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

            await RunTest("EveryShippedDecision_SendsThroughAGuardedPath", () =>
            {
                // A decision added later with its own send path and no row here fails this test until it is put
                // behind the shared guard and driven below.
                HashSet<string> covered = SkeletonDecisionPoints();
                AssertTrue(covered.Count >= 10, "the skeleton adapters are found by reflection (" + covered.Count + ")");
                foreach (StandaloneSender sender in StandaloneSenders()) covered.Add(sender.DecisionPoint);
                foreach (string captainOnly in CaptainToolDecisions) covered.Add(captainOnly);
                List<string> missing = TypedDecisionSettings.ShippedDecisionNames.Where(name => !covered.Contains(name)).ToList();
                AssertEqual(0, missing.Count, "decisions with no guarded send path: " + String.Join(", ", missing));
            });

            foreach (StandaloneSender sender in StandaloneSenders())
            {
                string label = sender.DecisionPoint + "/" + sender.Seam;

                await RunTest("Standalone_" + label + "_SendsACleanState", async () =>
                {
                    DriveResult clean = await DriveAsync(sender, EgressCase.Clean).ConfigureAwait(false);
                    AssertTrue(clean.Calls >= 1, label + " reaches the provider with a clean state, so a refusal below is the guard's (calls=" + clean.Calls + ")");
                }).ConfigureAwait(false);

                await RunTest("Standalone_" + label + "_RefusesAMarkedState", async () =>
                {
                    DriveResult marked = await DriveAsync(sender, EgressCase.MarkedContent).ConfigureAwait(false);
                    AssertEqual(0, marked.Calls, label + " sends nothing for a state carrying an excluded marker");
                    AssertTrue(marked.Reasons.Contains(TypedDecisionEgress.ExcludedContentReason),
                        label + " records " + TypedDecisionEgress.ExcludedContentReason + " (recorded: " + String.Join(",", marked.Reasons) + ")");
                }).ConfigureAwait(false);

                if (!sender.AboutVessel) continue;

                await RunTest("Standalone_" + label + "_RefusesAnExcludedVessel", async () =>
                {
                    DriveResult excluded = await DriveAsync(sender, EgressCase.ExcludedVessel).ConfigureAwait(false);
                    AssertEqual(0, excluded.Calls, label + " sends nothing about a vessel on the exclusion list");
                    AssertTrue(excluded.Reasons.Contains(TypedDecisionEgress.ExcludedVesselReason),
                        label + " records " + TypedDecisionEgress.ExcludedVesselReason + " (recorded: " + String.Join(",", excluded.Reasons) + ")");
                }).ConfigureAwait(false);
            }

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
        }
    }
}
