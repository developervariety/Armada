namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
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
    using FleetRoutingSettings = global::Test.Shared.Infrastructure.FleetRoutingSettings;

    /// <summary>
    /// Behavioural coverage of Smart Routing: the Legacy Routing order filtered by account usage, persona model
    /// groups chosen by the capacity decision, optional route restrictions, and loading settings written for the
    /// retired route-order model.
    /// </summary>
    public sealed class SmartRoutingTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Smart Routing";

        private const string _Luna = "gpt-5.6-luna";
        private const string _DeepSeek = "opencode-go/deepseek-v4-flash";
        private const string _Opus = "claude-opus-5";
        private const string _Unlisted = "example/mid-audit";

        #region Fixtures

        private static ModelTierSettings Tiers()
        {
            ModelTierSettings tiers = FleetRoutingSettings.CreateModelTier();
            // Legacy Routing prefers DeepSeek, then Luna, within mid; Opus is high and reached last by a Worker.
            tiers.WithinTierPreferenceOrder["mid"] = new List<string> { _DeepSeek, _Luna, _Unlisted };
            return tiers;
        }

        private static Captain NewCaptain(string id, string model)
        {
            return new Captain(id) { Id = id, Model = model, AllowedPersonas = "[\"Worker\",\"Judge\"]", State = CaptainStateEnum.Idle };
        }

        private static UsageAccountSettings Account(string id, double remaining, params string[] captainIds)
        {
            return new UsageAccountSettings
            {
                Id = id,
                CaptainIds = new List<string>(captainIds),
                ManualSnapshot = new ProviderUsageSnapshot
                {
                    ObservedUtc = DateTime.UtcNow,
                    Source = "test",
                    Windows = new List<ProviderUsageWindow> { new ProviderUsageWindow { Name = "weekly", RemainingPercent = remaining, ResetsUtc = DateTime.UtcNow.AddDays(1) } }
                }
            };
        }

        private static UsageRoutingSettings Policy(params UsageAccountSettings[] accounts)
        {
            return new UsageRoutingSettings { Enabled = true, Accounts = new List<UsageAccountSettings>(accounts) };
        }

        private static PersonaModelSettings WorkerModels()
        {
            return new PersonaModelSettings
            {
                Default = new List<string> { _Luna },
                Lighter = new List<string> { _DeepSeek },
                Stronger = new List<string> { _Opus }
            };
        }

        private static List<Captain> Pool()
        {
            return new List<Captain> { NewCaptain("cpt-opus", _Opus), NewCaptain("cpt-luna", _Luna), NewCaptain("cpt-deepseek", _DeepSeek) };
        }

        private static Task<UsageRoutingDecision> SelectAsync(
            UsageRoutingSettings policy,
            List<Captain> pool,
            Mission? mission = null,
            CapacityEscalationResolver? capacity = null,
            bool withText = true)
        {
            return SmartRoutingSelector.SelectAsync(new SmartRoutingRequest
            {
                Tiers = Tiers(),
                Policy = policy,
                Usage = new UsageRoutingService(),
                Mission = mission ?? new Mission { Id = "msn_example", Persona = "Worker", Priority = 100, Title = "work" },
                Pool = pool,
                NowUtc = DateTime.UtcNow,
                RandomPick = n => 0,
                Capacity = capacity,
                WorkText = withText ? _ => Task.FromResult(new CapacityWorkText { Title = "Fix a subtle race", Description = "Diagnose the hang." }) : null
            });
        }

        private static List<string> Ids(IEnumerable<Captain> captains) => captains.Select(c => c.Id).ToList();

        private static TypedDecisionResult CapacityResult(string choice, double confidence)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                [TypedCapacityEscalationAdapter.QuestionId] = new TypedAnswer { Type = "choice", Choice = choice, Confidence = confidence }
            };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 10, OutputTokens = 2, LatencyMs = 5 };
        }

        private static CapacityEscalationResolver Resolver(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionModeEnum mode = TypedDecisionModeEnum.Gate)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[TypedCapacityEscalationAdapter.DecisionName] = new TypedDecisionRuleSettings { Mode = mode, GateThreshold = 0.90 };
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new CapacityEscalationResolver(new TypedCapacityEscalationAdapter(client, new TypedDecisionRecorder(db.Driver, logging), settings, logging));
        }

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                .EnumerateAsync(new EnumerationQuery { EventType = eventType, PageNumber = 1, PageSize = 100 })
                .ConfigureAwait(false);
            return events.Objects.Count;
        }

        private void AssertSequence(List<string> expected, List<string> actual, string label)
        {
            AssertEqual(String.Join(",", expected), String.Join(",", actual), label);
        }

        #endregion

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Enabled without persona models or routes keeps the legacy order, removes Exhausted and demotes Low", async () =>
            {
                List<Captain> pool = Pool();
                pool.Add(NewCaptain("cpt-luna-2", _Luna));
                UsageRoutingSettings policy = Policy(
                    Account("low", 20, "cpt-deepseek"),
                    Account("exhausted", 0, "cpt-luna"),
                    Account("normal", 80, "cpt-opus"));
                Mission mission = new Mission { Id = "msn_example", Persona = "Worker", Priority = 100 };
                List<string> legacy = Ids(LegacyCaptainSelector.Order(Tiers(), mission, pool, false, n => 0));
                AssertSequence(new List<string> { "cpt-deepseek", "cpt-luna", "cpt-luna-2", "cpt-opus" }, legacy, "legacy order");

                UsageRoutingDecision decision = await SelectAsync(policy, pool, mission);
                AssertSequence(legacy, Ids(decision.LegacyOrder), "the decision reports the legacy order");
                // cpt-luna-2 is in no account, so it keeps its legacy position like an Unknown account.
                AssertSequence(new List<string> { "cpt-luna-2", "cpt-opus", "cpt-deepseek" }, Ids(decision.Candidates), "usage-filtered order");
                AssertEqual("removed", decision.Verdicts.First(v => v.CaptainId == "cpt-luna").Outcome);
                AssertEqual("demoted", decision.Verdicts.First(v => v.CaptainId == "cpt-deepseek").Outcome);
                AssertEqual(UsageRoutingService.ReasonNoAccount, decision.Verdicts.First(v => v.CaptainId == "cpt-luna-2").Reason);
            });

            await RunTest("Disabled returns exactly the legacy selection and order", async () =>
            {
                List<Captain> pool = Pool();
                UsageRoutingSettings policy = Policy(Account("exhausted", 0, "cpt-deepseek"));
                policy.Enabled = false;
                policy.PersonaModels["Worker"] = WorkerModels();
                policy.PersonaRoutes["Worker"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = "exhausted" } };
                Mission mission = new Mission { Id = "msn_example", Persona = "Worker", Priority = 100 };
                UsageRoutingDecision decision = await SelectAsync(policy, pool, mission);
                AssertSequence(Ids(LegacyCaptainSelector.Order(Tiers(), mission, pool, false, n => 0)), Ids(decision.Candidates), "disabled order");
                AssertEqual(LegacyCaptainSelector.Select(Tiers(), mission, pool, false, n => 0)!.Id, decision.Candidates[0].Id);
                AssertEqual(SmartRoutingSelector.ReasonDisabled, decision.Reason);
                AssertFalse(decision.HasPersonaModels);
            });

            await RunTest("Persona Default models are preferred over the legacy first choice without a capacity reading", async () =>
            {
                UsageRoutingSettings policy = Policy();
                policy.PersonaModels["Worker"] = WorkerModels();
                UsageRoutingDecision decision = await SelectAsync(policy, Pool());
                AssertEqual("cpt-luna", decision.Candidates[0].Id, "legacy would pick DeepSeek; the Default list wins");
                AssertEqual(CapacityChoiceEnum.Default, decision.Capacity);
                AssertEqual(CapacityEscalationResolver.SourceNoAdapter, decision.CapacitySource);
                AssertSequence(new List<string> { "default", "stronger", "lighter", "unlisted" }, decision.Groups.Select(g => g.Name).ToList(), "group order");
            });

            await RunTest("Capacity reading stronger at threshold picks the Stronger group", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(CapacityResult("stronger", 0.90));
                    UsageRoutingSettings policy = Policy();
                    policy.PersonaModels["Worker"] = WorkerModels();
                    UsageRoutingDecision decision = await SelectAsync(policy, Pool(), capacity: Resolver(db, client));
                    AssertEqual("cpt-opus", decision.Candidates[0].Id);
                    AssertEqual(CapacityChoiceEnum.Stronger, decision.Capacity);
                    AssertEqual(1, client.CallCount);
                    AssertEqual(TypedCapacityEscalationAdapter.DecisionName, client.LastRequest!.DecisionPoint);
                    AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated));
                }
            });

            await RunTest("Capacity reading lighter picks the Lighter group", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(CapacityResult("lighter", 0.97));
                    UsageRoutingSettings policy = Policy();
                    policy.PersonaModels["Worker"] = WorkerModels();
                    UsageRoutingDecision decision = await SelectAsync(policy, Pool(), capacity: Resolver(db, client));
                    AssertEqual("cpt-deepseek", decision.Candidates[0].Id);
                    AssertSequence(new List<string> { "lighter", "default", "stronger", "unlisted" }, decision.Groups.Select(g => g.Name).ToList(), "group order");
                }
            });

            await RunTest("Capacity below threshold, Off, a throwing client, or a timeout picks the Default group", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    UsageRoutingSettings policy = Policy();
                    policy.PersonaModels["Worker"] = WorkerModels();

                    FakeTypedDecisionClient below = new FakeTypedDecisionClient(CapacityResult("stronger", 0.89));
                    UsageRoutingDecision belowDecision = await SelectAsync(policy, Pool(), capacity: Resolver(db, below));
                    AssertEqual("cpt-luna", belowDecision.Candidates[0].Id, "below threshold");
                    AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow), "below threshold records the model verdict");

                    FakeTypedDecisionClient off = new FakeTypedDecisionClient(CapacityResult("stronger", 0.99));
                    UsageRoutingDecision offDecision = await SelectAsync(policy, Pool(), capacity: Resolver(db, off, TypedDecisionModeEnum.Off));
                    AssertEqual("cpt-luna", offDecision.Candidates[0].Id, "Off");
                    AssertEqual(0, off.CallCount, "Off never calls the client");

                    FakeTypedDecisionClient throwing = FakeTypedDecisionClient.Throwing(new InvalidOperationException("provider fault"));
                    UsageRoutingDecision throwDecision = await SelectAsync(policy, Pool(), capacity: Resolver(db, throwing));
                    AssertEqual("cpt-luna", throwDecision.Candidates[0].Id, "throwing client");

                    FakeTypedDecisionClient timeout = new FakeTypedDecisionClient(new TypedDecisionResult { Available = false, UnavailableReason = "timeout" });
                    UsageRoutingDecision timeoutDecision = await SelectAsync(policy, Pool(), capacity: Resolver(db, timeout));
                    AssertEqual("cpt-luna", timeoutDecision.Candidates[0].Id, "timeout");
                    AssertEqual(2, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable), "throw and timeout each record unavailable");
                }
            });

            await RunTest("An exhausted Default group falls through to the next group", async () =>
            {
                UsageRoutingSettings policy = Policy(Account("luna-account", 0, "cpt-luna"));
                policy.PersonaModels["Worker"] = WorkerModels();
                UsageRoutingDecision decision = await SelectAsync(policy, Pool());
                AssertEqual("cpt-opus", decision.Candidates[0].Id, "Default is exhausted; Stronger follows Default");
                AssertEqual(SmartRoutingSelector.ReasonGroupPrefix + "stronger", decision.Reason);
            });

            await RunTest("A captain whose model is in no list stays reachable when every group is empty", async () =>
            {
                List<Captain> pool = Pool();
                pool.Add(NewCaptain("cpt-unlisted", _Unlisted));
                UsageRoutingSettings policy = Policy(Account("listed", 0, "cpt-luna", "cpt-opus", "cpt-deepseek"));
                policy.PersonaModels["Worker"] = WorkerModels();
                UsageRoutingDecision decision = await SelectAsync(policy, pool);
                AssertEqual("cpt-unlisted", decision.Candidates[0].Id);
                AssertEqual(1, decision.Candidates.Count);
            });

            await RunTest("Routes restrict a persona to named accounts and leave other personas unrestricted", async () =>
            {
                UsageRoutingSettings policy = Policy(Account("opus-account", 80, "cpt-opus"), Account("other", 80, "cpt-luna", "cpt-deepseek"));
                policy.PersonaRoutes["Worker"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = "opus-account" } };
                UsageRoutingDecision worker = await SelectAsync(policy, Pool());
                AssertSequence(new List<string> { "cpt-opus" }, Ids(worker.Candidates), "Worker is restricted");
                AssertTrue(worker.HasPersonaRoutes);
                AssertEqual(UsageRoutingService.OutcomeOutsideRoutes, worker.Verdicts.First(v => v.CaptainId == "cpt-luna").Outcome);

                UsageRoutingDecision judge = await SelectAsync(policy, Pool(), new Mission { Id = "msn_judge", Persona = "Judge", Priority = 100 });
                AssertFalse(judge.HasPersonaRoutes);
                AssertEqual(1, judge.Candidates.Count(c => c.Id == "cpt-opus"));
                AssertEqual(judge.LegacyOrder.Count, judge.Candidates.Count, "an unrestricted persona keeps every legacy candidate");
            });

            await RunTest("Capacity reading is cached per mission across assignment attempts", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(CapacityResult("stronger", 0.95));
                    CapacityEscalationResolver resolver = Resolver(db, client);
                    UsageRoutingSettings policy = Policy();
                    policy.PersonaModels["Worker"] = WorkerModels();
                    Mission mission = new Mission { Id = "msn_cached", Persona = "Worker", Priority = 100 };
                    UsageRoutingDecision first = await SelectAsync(policy, Pool(), mission, resolver);
                    UsageRoutingDecision second = await SelectAsync(policy, Pool(), mission, resolver);
                    AssertEqual(1, client.CallCount, "the second attempt reuses the reading");
                    AssertEqual(CapacityEscalationResolver.SourceCached, second.CapacitySource);
                    AssertEqual(first.Candidates[0].Id, second.Candidates[0].Id);
                }
            });

            await RunTest("Without work text the capacity client is not called and Default applies", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(CapacityResult("stronger", 0.99));
                    UsageRoutingSettings policy = Policy();
                    policy.PersonaModels["Worker"] = WorkerModels();
                    UsageRoutingDecision decision = await SelectAsync(policy, Pool(), capacity: Resolver(db, client), withText: false);
                    AssertEqual(0, client.CallCount);
                    AssertEqual(CapacityEscalationResolver.SourceNoWorkText, decision.CapacitySource);
                    AssertEqual("cpt-luna", decision.Candidates[0].Id);
                }
            });

            await RunTest("A concrete model pin skips persona model groups", async () =>
            {
                UsageRoutingSettings policy = Policy();
                policy.PersonaModels["Worker"] = WorkerModels();
                UsageRoutingDecision decision = await SelectAsync(policy, Pool(), new Mission { Id = "msn_pin", Persona = "Worker", Priority = 100, PreferredModel = _DeepSeek });
                AssertEqual("cpt-deepseek", decision.Candidates[0].Id);
                AssertFalse(decision.HasPersonaModels);
            });

            await RunTest("Settings written for route order and routing_hint still load", async () =>
            {
                string path = Path.Combine(Path.GetTempPath(), "armada_obsolete_routing_" + Guid.NewGuid().ToString("N") + ".json");
                string json = "{\"modelTier\":{\"usageRouting\":{\"enabled\":true,"
                    + "\"accounts\":[{\"id\":\"primary\",\"captainIds\":[\"cpt-a\"],\"reservedPersonas\":[\"Judge\"],\"reservedPriorityAtOrAbove\":10,\"recoveryRemainingPercent\":40}],"
                    + "\"personaRoutes\":{\"Worker\":[{\"accountId\":\"primary\",\"models\":[],\"shapes\":[\"mechanical\",\"policy-tolerant\"]}]}}},"
                    + "\"typedDecisions\":{\"mode\":\"Gate\",\"decisions\":{\"routing_hint\":{\"mode\":\"Gate\",\"gateThreshold\":0.8},\"refusal\":{\"mode\":\"Off\",\"gateThreshold\":0.9}}}}";
                try
                {
                    await File.WriteAllTextAsync(path, json);
                    ArmadaSettings loaded = await ArmadaSettings.LoadAsync(path);
                    AssertTrue(loaded.ModelTier.UsageRouting.Enabled);
                    AssertEqual("primary", loaded.ModelTier.UsageRouting.PersonaRoutes["Worker"][0].AccountId);
                    AssertEqual(40.0, loaded.ModelTier.UsageRouting.Accounts[0].RecoveryRemainingPercent);
                    AssertEqual(TypedDecisionModeEnum.Off, loaded.TypedDecisions.For("refusal").Mode, "a stored mode is kept");
                    ResolvedTypedDecision capacity = loaded.TypedDecisions.For(TypedCapacityEscalationAdapter.DecisionName);
                    AssertEqual(TypedDecisionModeEnum.Gate, capacity.Mode, "a decision missing from an older file runs at its shipped mode");
                    AssertEqual(0.90, capacity.GateThreshold);
                }
                finally
                {
                    File.Delete(path);
                }
            });

            await RunTest("Persona model preferences validate names and lists", () =>
            {
                UsageRoutingSettings policy = Policy();
                policy.PersonaModels["Worker"] = WorkerModels();
                UsageRoutingService.Validate(policy);
                policy.PersonaModels["TestEngineer"] = WorkerModels();
                policy.PersonaModels["Test Engineer"] = WorkerModels();
                AssertThrows<ArgumentException>(() => UsageRoutingService.Validate(policy), "duplicate persona after normalization");
                policy.PersonaModels.Remove("Test Engineer");
                policy.PersonaModels["Worker"].Default.Clear();
                AssertThrows<ArgumentException>(() => UsageRoutingService.Validate(policy), "empty Default list");
            });
        }
    }
}
