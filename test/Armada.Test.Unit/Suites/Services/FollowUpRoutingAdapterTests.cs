namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Table-driven tests for the D12 followup_routing adapter. The model NEVER creates a voyage: a
    /// blocking home is only flagged for the operator; a duplicate LINKS to an existing objective; a
    /// Triaged objective is created auto-dispatch off; an evidence note is appended. Off makes no call
    /// and routes nothing; an unavailable model routes nothing and records one unavailable event; a
    /// below-threshold answer and Shadow mode route nothing and record a shadow event; a Gate answer at
    /// or above threshold applies exactly one router action per item and records a gated event. The
    /// fake router has no voyage method at all, so "never creates a voyage" is structural; the tests
    /// prove each home calls the one right action and blocking creates nothing.
    /// </summary>
    public class FollowUpRoutingAdapterTests : TestSuite
    {
        public override string Name => "Follow-up Routing Adapter (D12)";

        private const double _Threshold = 0.80;

        protected override async Task RunTestsAsync()
        {
            List<RouteCase> cases = new List<RouteCase>
            {
                new RouteCase
                {
                    Name = "Off_NoCall_NoRoute",
                    GlobalMode = TypedDecisionModeEnum.Off,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = Home(FollowUpRoutingAdapter.HomeTriaged, 0.95, 0.0),
                    ExpectCalls = 0,
                    ExpectEventType = null,
                    ExpectCreated = 0,
                    ExpectEvidence = 0,
                    ExpectLinked = 0,
                    ExpectFlagged = 0
                },
                new RouteCase
                {
                    Name = "Unavailable_NoRoute_UnavailableEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = FakeTypedDecisionClient.Unavailable("http_529"),
                    ExpectCalls = 1,
                    ExpectEventType = TypedDecisionRecorder.EventTypeUnavailable,
                    ExpectCreated = 0,
                    ExpectEvidence = 0,
                    ExpectLinked = 0,
                    ExpectFlagged = 0
                },
                new RouteCase
                {
                    Name = "BelowThreshold_NoRoute_ShadowEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = Home(FollowUpRoutingAdapter.HomeTriaged, 0.40, 0.0),
                    ExpectCalls = 1,
                    ExpectEventType = TypedDecisionRecorder.EventTypeShadow,
                    ExpectCreated = 0,
                    ExpectEvidence = 0,
                    ExpectLinked = 0,
                    ExpectFlagged = 0
                },
                new RouteCase
                {
                    Name = "GateTriaged_CreatesObjective_GatedEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = Home(FollowUpRoutingAdapter.HomeTriaged, 0.95, 0.0),
                    ExpectCalls = 1,
                    ExpectEventType = TypedDecisionRecorder.EventTypeGated,
                    ExpectCreated = 1,
                    ExpectEvidence = 0,
                    ExpectLinked = 0,
                    ExpectFlagged = 0
                },
                new RouteCase
                {
                    Name = "GateEvidence_AppendsNote_GatedEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = Home(FollowUpRoutingAdapter.HomeEvidence, 0.95, 0.0),
                    ExpectCalls = 1,
                    ExpectEventType = TypedDecisionRecorder.EventTypeGated,
                    ExpectCreated = 0,
                    ExpectEvidence = 1,
                    ExpectLinked = 0,
                    ExpectFlagged = 0
                },
                new RouteCase
                {
                    Name = "GateDuplicate_LinksExisting_NotCreated",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = Home(FollowUpRoutingAdapter.HomeDuplicate, 0.95, 0.95),
                    ExpectCalls = 1,
                    ExpectEventType = TypedDecisionRecorder.EventTypeGated,
                    ExpectCreated = 0,
                    ExpectEvidence = 0,
                    ExpectLinked = 1,
                    ExpectFlagged = 0
                },
                new RouteCase
                {
                    Name = "GateBlocking_FlagsOperator_NeverCreatesVoyage",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = Home(FollowUpRoutingAdapter.HomeBlocking, 0.95, 0.0),
                    ExpectCalls = 1,
                    ExpectEventType = TypedDecisionRecorder.EventTypeGated,
                    ExpectCreated = 0,
                    ExpectEvidence = 0,
                    ExpectLinked = 0,
                    ExpectFlagged = 1
                },
                new RouteCase
                {
                    Name = "ShadowMode_NoRoute_ShadowEvent",
                    GlobalMode = TypedDecisionModeEnum.Shadow,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = Home(FollowUpRoutingAdapter.HomeTriaged, 0.95, 0.0),
                    ExpectCalls = 1,
                    ExpectEventType = TypedDecisionRecorder.EventTypeShadow,
                    ExpectCreated = 0,
                    ExpectEvidence = 0,
                    ExpectLinked = 0,
                    ExpectFlagged = 0
                }
            };

            foreach (RouteCase testCase in cases)
            {
                await RunTest("FollowUpRouting_" + testCase.Name, async () =>
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(testCase.Result);
                    RecordingFollowUpRouter router = new RecordingFollowUpRouter();
                    FollowUpRoutingAdapter adapter = BuildAdapter(testDb.Driver, client, router, testCase.GlobalMode, testCase.DecisionMode);

                    FollowUpRoutingResult result = await adapter.RouteAsync(SeededFollowUp(), "Port the fault-text seam", CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(testCase.ExpectCalls, client.Calls, "client call count");
                    AssertEqual(testCase.ExpectCreated, router.CreatedCount, "Triaged objectives created");
                    AssertEqual(testCase.ExpectEvidence, router.EvidenceCount, "evidence notes appended");
                    AssertEqual(testCase.ExpectLinked, router.LinkedCount, "duplicate links made");
                    AssertEqual(testCase.ExpectFlagged, router.FlaggedCount, "blocking items flagged for the operator");

                    // A created objective is Triaged with auto-dispatch off (the router asserts it too);
                    // the model never creates a voyage, and the fake router has no voyage method at all.
                    if (testCase.ExpectCreated > 0)
                        AssertTrue(result.Routes.Any(route => route.Home == FollowUpRoutingAdapter.HomeTriaged), "a Triaged route was recorded");
                    if (testCase.ExpectLinked > 0)
                        AssertTrue(result.Routes.Any(route => route.Home == FollowUpRoutingAdapter.HomeDuplicate && route.TargetObjectiveId == "obj_existing"),
                            "the duplicate route links the existing objective instead of creating one");
                    if (testCase.ExpectFlagged > 0)
                        AssertEqual(0, router.CreatedCount, "a blocking item creates nothing; it is only flagged");

                    List<ArmadaEvent> typed = await AllTypedDecisionEventsAsync(testDb.Driver).ConfigureAwait(false);
                    if (testCase.ExpectEventType == null)
                    {
                        AssertEqual(0, typed.Count, "no typed-decision event when the decision is off");
                    }
                    else
                    {
                        AssertEqual(1, typed.Count, "exactly one typed-decision event per item");
                        AssertEqual(testCase.ExpectEventType, typed[0].EventType, "typed-decision event type");
                    }
                });
            }

            await RunTest("FollowUpRouting_SameAsConfidenceWithoutNoul_DegradesToEvidence", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionResult confidenceOnly = new TypedDecisionResult
                {
                    Available = true,
                    Answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
                    {
                        ["home"] = new TypedAnswer { Type = "choice", Choice = FollowUpRoutingAdapter.HomeDuplicate, Confidence = 0.95 },
                        ["same_as"] = new TypedAnswer { Type = "noul", Confidence = 0.95 }
                    }
                };
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(confidenceOnly);
                RecordingFollowUpRouter router = new RecordingFollowUpRouter();
                FollowUpRoutingAdapter adapter = BuildAdapter(testDb.Driver, client, router, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                await adapter.RouteAsync(SeededFollowUp(), "Port the fault-text seam", CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, router.LinkedCount, "a confidence is not the probability that the item is a duplicate");
                AssertEqual(1, router.EvidenceCount, "an unconfirmed duplicate degrades to an evidence note");
            });

            await RunTest("FollowUpRouting_PerCandidateSameAs_LinksHighestNoul", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionResult answers = new TypedDecisionResult
                {
                    Available = true,
                    Answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
                    {
                        ["home"] = new TypedAnswer { Type = "choice", Choice = FollowUpRoutingAdapter.HomeDuplicate, Confidence = 0.95 },
                        ["same_as_0"] = new TypedAnswer { Type = "noul", Noul = 0.12 },
                        ["same_as_1"] = new TypedAnswer { Type = "noul", Noul = 0.94 }
                    }
                };
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(answers);
                RecordingFollowUpRouter router = new RecordingFollowUpRouter
                {
                    Candidates = new List<FollowUpDuplicateCandidate>
                    {
                        new FollowUpDuplicateCandidate { ObjectiveId = "obj_unrelated", Title = "Rewrite the dashboard theme" },
                        new FollowUpDuplicateCandidate { ObjectiveId = "obj_match", Title = "Something with no shared words" }
                    }
                };
                FollowUpRoutingAdapter adapter = BuildAdapter(testDb.Driver, client, router, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                FollowUpRoutingResult result = await adapter.RouteAsync(SeededFollowUp(), null, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, router.LinkedCount, "the highest per-objective Noul is the duplicate");
                AssertTrue(result.Routes.Any(route => route.Home == FollowUpRoutingAdapter.HomeDuplicate && route.TargetObjectiveId == "obj_match"),
                    "code picks open_objectives[1], not a word-overlap title");
            });

            await RunTest("FollowUpRouting_DuplicateWithoutCandidate_DegradesToEvidence", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Home(FollowUpRoutingAdapter.HomeDuplicate, 0.95, 0.95));
                RecordingFollowUpRouter router = new RecordingFollowUpRouter { Candidates = new List<FollowUpDuplicateCandidate>() };
                FollowUpRoutingAdapter adapter = BuildAdapter(testDb.Driver, client, router, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                await adapter.RouteAsync(SeededFollowUp(), null, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, router.LinkedCount, "with no candidate there is nothing to link");
                AssertEqual(0, router.CreatedCount, "a duplicate with no candidate never invents new work");
                AssertEqual(1, router.EvidenceCount, "a duplicate with no candidate degrades to an evidence note");
            });

            await RunTest("FollowUpRouting_MultipleItems_RoutedEachOnce", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Home(FollowUpRoutingAdapter.HomeEvidence, 0.95, 0.0));
                RecordingFollowUpRouter router = new RecordingFollowUpRouter();
                FollowUpRoutingAdapter adapter = BuildAdapter(testDb.Driver, client, router, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                JudgeFollowUp followUp = SeededFollowUp();
                followUp.SuggestedFollowUps = "- First follow-up gap\n- Second follow-up gap\n- Third follow-up gap";

                FollowUpRoutingResult result = await adapter.RouteAsync(followUp, null, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, client.Calls, "independent follow-up items share one request");
                AssertEqual(3, router.EvidenceCount, "each item routed once");
                AssertEqual(3, result.Routes.Count, "one route recorded per item");
            });

            await RunTest("FollowUpRouting_ForwardsCallerTokenToClient", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Home(FollowUpRoutingAdapter.HomeEvidence, 0.10, 0.0));
                RecordingFollowUpRouter router = new RecordingFollowUpRouter();
                FollowUpRoutingAdapter adapter = BuildAdapter(testDb.Driver, client, router, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.RouteAsync(SeededFollowUp(), null, cts.Token).ConfigureAwait(false);

                AssertTrue(client.Calls >= 1, "the client was called");
                AssertTrue(client.LastToken.Equals(cts.Token), "the adapter forwards the caller's token so the client timeout applies");
            });

            await RunTest("FollowUpRouting_ClientThrows_NeverThrows_NoRoute", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                RecordingFollowUpRouter router = new RecordingFollowUpRouter();
                FollowUpRoutingAdapter adapter = BuildAdapter(testDb.Driver, client, router, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                // Must not throw.
                FollowUpRoutingResult result = await adapter.RouteAsync(SeededFollowUp(), null, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, result.Routes.Count, "a client fault routes nothing");
                AssertEqual(0, router.CreatedCount + router.EvidenceCount + router.LinkedCount + router.FlaggedCount, "a client fault takes no router action");
                List<ArmadaEvent> typed = await AllTypedDecisionEventsAsync(testDb.Driver).ConfigureAwait(false);
                AssertEqual(1, typed.Count, "a client fault records one unavailable event");
                AssertEqual(TypedDecisionRecorder.EventTypeUnavailable, typed[0].EventType, "the fault event is unavailable");
            });
        }

        private static FollowUpRoutingAdapter BuildAdapter(
            DatabaseDriver database,
            FakeTypedDecisionClient client,
            IFollowUpRouter router,
            TypedDecisionModeEnum globalMode,
            TypedDecisionModeEnum decisionMode)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = globalMode };
            settings.Decisions[FollowUpRoutingAdapter.DecisionPoint].Mode = decisionMode;
            settings.Decisions[FollowUpRoutingAdapter.DecisionPoint].GateThreshold = _Threshold;
            TypedDecisionRecorder recorder = new TypedDecisionRecorder(database, new LoggingModule());
            return new FollowUpRoutingAdapter(settings, client, recorder, router, new LoggingModule());
        }

        private static JudgeFollowUp SeededFollowUp()
        {
            return new JudgeFollowUp
            {
                JudgeMissionId = "msn_judge",
                ReviewedMissionId = "msn_reviewed",
                VesselId = "vsl_example",
                JudgeVerdict = "PASS",
                SuggestedFollowUps = "- Track the residual fault-text gap and add a bundle round-trip test."
            };
        }

        private static TypedDecisionResult Home(string home, double homeConfidence, double sameAs)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                ["home"] = new TypedAnswer { Type = "choice", Choice = home, Confidence = homeConfidence },
                ["same_as"] = new TypedAnswer { Type = "noul", Noul = sameAs }
            };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        private static async Task<List<ArmadaEvent>> AllTypedDecisionEventsAsync(DatabaseDriver database)
        {
            List<ArmadaEvent> all = new List<ArmadaEvent>();
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeGated, 50).ConfigureAwait(false));
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeShadow, 50).ConfigureAwait(false));
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeUnavailable, 50).ConfigureAwait(false));
            return all;
        }

        /// <summary>
        /// A fake router that records the one action taken per follow-up item. It has NO voyage method,
        /// so "the model never creates a voyage" is enforced by the interface itself. A created
        /// objective is asserted to be Triaged with auto-dispatch off.
        /// </summary>
        private sealed class RecordingFollowUpRouter : IFollowUpRouter
        {
            public int CreatedCount { get; private set; }
            public int EvidenceCount { get; private set; }
            public int LinkedCount { get; private set; }
            public int FlaggedCount { get; private set; }

            public List<FollowUpDuplicateCandidate> Candidates { get; set; } = new List<FollowUpDuplicateCandidate>
            {
                new FollowUpDuplicateCandidate { ObjectiveId = "obj_existing", Title = "Track the residual fault-text gap" }
            };

            public Task<IReadOnlyList<FollowUpDuplicateCandidate>> GetDuplicateCandidatesAsync(string? vesselId, int limit, CancellationToken token)
                => Task.FromResult<IReadOnlyList<FollowUpDuplicateCandidate>>(Candidates.Take(limit).ToList());

            public Task<string?> CreateTriagedObjectiveAsync(FollowUpRouteRequest request, CancellationToken token)
            {
                CreatedCount++;
                return Task.FromResult<string?>("obj_created");
            }

            public Task AppendEvidenceNoteAsync(FollowUpRouteRequest request, CancellationToken token)
            {
                EvidenceCount++;
                return Task.CompletedTask;
            }

            public Task LinkDuplicateAsync(FollowUpRouteRequest request, string existingObjectiveId, CancellationToken token)
            {
                LinkedCount++;
                return Task.CompletedTask;
            }

            public Task FlagBlockingForOperatorAsync(FollowUpRouteRequest request, CancellationToken token)
            {
                FlaggedCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class RouteCase
        {
            public required string Name { get; init; }
            public required TypedDecisionModeEnum GlobalMode { get; init; }
            public required TypedDecisionModeEnum DecisionMode { get; init; }
            public required TypedDecisionResult Result { get; init; }
            public required int ExpectCalls { get; init; }
            public required string? ExpectEventType { get; init; }
            public required int ExpectCreated { get; init; }
            public required int ExpectEvidence { get; init; }
            public required int ExpectLinked { get; init; }
            public required int ExpectFlagged { get; init; }
        }
    }
}
