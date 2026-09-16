namespace Armada.Test.Unit.Suites.Services
{
    using System;
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
    /// Tests for the D16 <c>routing_hint</c> adapter and its Routing V2 reorder. The adapter half is
    /// table-driven (Off / unavailable / shadow-below / above); the Select half proves the reorder only
    /// changes the order of already-eligible routes and honours every hard V2 constraint: a reserved
    /// persona is never reordered, a policy-sensitive mission with no tolerant route falls back to the V2
    /// default and records it, and a tagless route configuration behaves exactly as before.
    /// </summary>
    public class TypedRoutingHintAdapterTests : TestSuite
    {
        public override string Name => "Typed Routing Hint Adapter (D16)";

        private const string _Decision = "routing_hint";

        #region Adapter-Half

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.80)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static RoutingHintDecisionInput BuildInput()
        {
            return new RoutingHintDecisionInput
            {
                Mission = new Mission { Id = "msn_test", VesselId = "vsl_test", Persona = "Worker", Title = "mechanical rename" },
                ObjectiveTitle = "rename a symbol",
                Description = "a routine rename",
                AcceptanceCriteria = "compiles",
                Persona = "Worker",
                VesselName = "ExampleVessel",
                BriefByteSize = 100,
                EligibleRouteShapes = new List<string> { "mechanical", "policy-tolerant" }
            };
        }

        // shape choice + confidence, policy_sensitive noul.
        private static TypedDecisionResult HintResult(string shape, double shapeConfidence, double policySensitive)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                ["shape"] = new TypedAnswer { Type = "choice", Choice = shape, Confidence = shapeConfidence },
                ["policy_sensitive"] = new TypedAnswer { Type = "noul", Noul = policySensitive, Confidence = policySensitive },
                ["context_heavy"] = new TypedAnswer { Type = "noul", Noul = 0.2, Confidence = 0.2 },
                ["telemetry_needed"] = new TypedAnswer { Type = "noul", Noul = 0.2, Confidence = 0.2 }
            };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                .EnumerateAsync(new EnumerationQuery { EventType = eventType, PageNumber = 1, PageSize = 100 })
                .ConfigureAwait(false);
            return events.Objects.Count;
        }

        private static TypedRoutingHintAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedRoutingHintAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        #endregion

        #region Select-Half

        private static UsageAccountSettings Account(string id, double remaining, List<string>? reservedPersonas = null)
        {
            return new UsageAccountSettings
            {
                Id = id,
                CaptainIds = new List<string> { id },
                ReservedPersonas = reservedPersonas ?? new List<string>(),
                ManualSnapshot = new ProviderUsageSnapshot
                {
                    ObservedUtc = DateTime.UtcNow,
                    Source = "test",
                    Windows = new List<ProviderUsageWindow> { new ProviderUsageWindow { Name = "weekly", RemainingPercent = remaining, ResetsUtc = DateTime.UtcNow.AddDays(1) } }
                }
            };
        }

        private static UsageRoutingSettings PolicyWithShapes(List<string> firstShapes, List<string> secondShapes, List<string>? reservedPersonas = null)
        {
            UsageAccountSettings first = Account("first", 80, reservedPersonas);
            UsageAccountSettings second = Account("second", 80, reservedPersonas);
            return new UsageRoutingSettings
            {
                Enabled = true,
                Accounts = new List<UsageAccountSettings> { first, second },
                PersonaRoutes = new Dictionary<string, List<UsageRouteSettings>>
                {
                    ["Worker"] = new List<UsageRouteSettings>
                    {
                        new UsageRouteSettings { AccountId = "first", Shapes = firstShapes },
                        new UsageRouteSettings { AccountId = "second", Shapes = secondShapes }
                    },
                    ["Judge"] = new List<UsageRouteSettings>
                    {
                        new UsageRouteSettings { AccountId = "first", Shapes = firstShapes },
                        new UsageRouteSettings { AccountId = "second", Shapes = secondShapes }
                    }
                }
            };
        }

        private static UsageRoutingDecision Choose(UsageRoutingSettings policy, string persona, RoutingHint hint)
        {
            return new UsageRoutingService().Select(policy,
                new Mission { Persona = persona, Priority = 100 },
                new List<Captain> { new Captain("first") { Id = "first", Model = "model-a" }, new Captain("second") { Id = "second", Model = "model-b" } },
                Array.Empty<string>(), DateTime.UtcNow, hint);
        }

        #endregion

        protected override async Task RunTestsAsync()
        {
            // Adapter half.

            await RunTest("Off_ReturnsRule_NoCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(HintResult("mechanical", 0.99, 0.1));
                TypedRoutingHintAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));

                RoutingHint result = await adapter.DecideAsync(BuildInput(), RoutingHint.None(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Applied, "Off keeps the plain V2 order");
                AssertEqual(0, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Unavailable_ReturnsRule_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(new TypedDecisionResult { Available = false, UnavailableReason = "timeout" });
                TypedRoutingHintAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RoutingHint result = await adapter.DecideAsync(BuildInput(), RoutingHint.None(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Applied, "unavailable keeps the plain V2 order");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("BelowThreshold_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(HintResult("mechanical", 0.50, 0.1));
                TypedRoutingHintAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RoutingHint result = await adapter.DecideAsync(BuildInput(), RoutingHint.None(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Applied, "a below-threshold shape keeps the plain V2 order");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ShapeAboveThreshold_AppliesHint_RecordsGated", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(HintResult("mechanical", 0.95, 0.1));
                TypedRoutingHintAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RoutingHint result = await adapter.DecideAsync(BuildInput(), RoutingHint.None(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Applied, "a shape at threshold applies a hint");
                AssertEqual("mechanical", result.ChosenShape);
                AssertTrue(!result.PreferPolicyTolerant, "a non-sensitive shape does not prefer a tolerant route");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("PolicySensitiveAtFloor_PrefersPolicyTolerant_EvenWhenShapeNone", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(HintResult("none", 0.1, 0.95));
                TypedRoutingHintAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RoutingHint result = await adapter.DecideAsync(BuildInput(), RoutingHint.None(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Applied && result.PreferPolicyTolerant, "policy_sensitive >= 0.9 prefers a policy-tolerant route");
                AssertTrue(result.ChosenShape == null, "a 'none' shape carries no chosen shape");
            }).ConfigureAwait(false);

            await RunTest("PolicySensitiveBelowFloor_NoPreference", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(HintResult("none", 0.1, 0.85));
                TypedRoutingHintAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RoutingHint result = await adapter.DecideAsync(BuildInput(), RoutingHint.None(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Applied, "policy_sensitive below 0.9 and shape none keeps the plain order");
            }).ConfigureAwait(false);

            await RunTest("NeverThrows_ClientThrows_ReturnsRule", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                TypedRoutingHintAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RoutingHint result = await adapter.DecideAsync(BuildInput(), RoutingHint.None(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.Applied, "a client fault never reaches the caller; the plain order stands");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("CallerTokenReachesClient_AndDecisionPoint", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(HintResult("mechanical", 0.95, 0.1));
                TypedRoutingHintAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.DecideAsync(BuildInput(), RoutingHint.None(), cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken == cts.Token, "adapter must forward the caller token");
                AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
            }).ConfigureAwait(false);

            // Select half: the reorder over already-eligible routes.

            await RunTest("Select_NoHint_KeepsListOrder", () =>
            {
                UsageRoutingSettings policy = PolicyWithShapes(new List<string>(), new List<string> { "mechanical" });
                UsageRoutingDecision decision = Choose(policy, "Worker", RoutingHint.None());
                AssertEqual("first", decision.Candidates[0].Id);
                AssertTrue(decision.RoutingHintOutcome == null, "no hint records no outcome");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Select_ShapeMatch_ReordersEligibleRoutes", () =>
            {
                UsageRoutingSettings policy = PolicyWithShapes(new List<string>(), new List<string> { "mechanical" });
                UsageRoutingDecision decision = Choose(policy, "Worker", RoutingHint.Apply("mechanical", false));
                AssertEqual("second", decision.Candidates[0].Id);
                AssertEqual("applied_shape:mechanical", decision.RoutingHintOutcome);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Select_TaglessRoutes_Unchanged", () =>
            {
                UsageRoutingSettings policy = PolicyWithShapes(new List<string>(), new List<string>());
                UsageRoutingDecision decision = Choose(policy, "Worker", RoutingHint.Apply("mechanical", false));
                AssertEqual("first", decision.Candidates[0].Id);
                AssertEqual("no_shape_match", decision.RoutingHintOutcome);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Select_PolicyTolerant_ReordersToTolerantRoute", () =>
            {
                UsageRoutingSettings policy = PolicyWithShapes(new List<string>(), new List<string> { UsageRoutingService.PolicyTolerantTag });
                UsageRoutingDecision decision = Choose(policy, "Worker", RoutingHint.Apply(null, true));
                AssertEqual("second", decision.Candidates[0].Id);
                AssertEqual("policy_tolerant", decision.RoutingHintOutcome);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Select_PolicyTolerant_NoTolerantRoute_FallsBackAndRecords", () =>
            {
                UsageRoutingSettings policy = PolicyWithShapes(new List<string> { "mechanical" }, new List<string> { "reasoning-heavy" });
                UsageRoutingDecision decision = Choose(policy, "Worker", RoutingHint.Apply(null, true));
                AssertEqual("first", decision.Candidates[0].Id);
                AssertEqual("no_tolerant_route", decision.RoutingHintOutcome);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Select_ReservedPersona_NeverReordered", () =>
            {
                // Judge is reserved on both accounts: the hint must not reorder its routes.
                UsageRoutingSettings policy = PolicyWithShapes(new List<string>(), new List<string> { "mechanical" }, new List<string> { "Judge" });
                UsageRoutingDecision decision = Choose(policy, "Judge", RoutingHint.Apply("mechanical", false));
                AssertEqual("first", decision.Candidates[0].Id);
                AssertEqual("reserved_persona_unchanged", decision.RoutingHintOutcome);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
