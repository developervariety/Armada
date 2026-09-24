namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using System.Net;
    using System.Net.Http;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>Behavioral coverage of account conservation, routing preferences, and quota collection.</summary>
    public sealed class UsageRoutingTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Usage Routing";

        private static UsageAccountSettings Account(string id, double remaining)
        {
            return new UsageAccountSettings
            {
                Id = id, CaptainIds = new List<string> { id }, ReservedPersonas = new List<string> { "Judge" },
                ManualSnapshot = new ProviderUsageSnapshot
                {
                    ObservedUtc = DateTime.UtcNow, Source = "test",
                    Windows = new List<ProviderUsageWindow> { new ProviderUsageWindow { Name = "weekly", RemainingPercent = remaining, ResetsUtc = DateTime.UtcNow.AddDays(1) } }
                }
            };
        }

        private static UsageRoutingSettings Policy(UsageAccountSettings first, UsageAccountSettings second)
        {
            return new UsageRoutingSettings
            {
                Enabled = true, Accounts = new List<UsageAccountSettings> { first, second },
                PersonaRoutes = new Dictionary<string, List<UsageRouteSettings>>
                {
                    ["Worker"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = first.Id }, new UsageRouteSettings { AccountId = second.Id } },
                    ["Judge"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = first.Id }, new UsageRouteSettings { AccountId = second.Id } }
                }
            };
        }

        private static UsageRoutingDecision Choose(UsageRoutingService service, UsageRoutingSettings policy, string persona = "Worker", int priority = 100, string[]? busy = null, string? retrySkip = null)
        {
            // Unclassified models with a first-index pick make the Legacy Routing order the pool order: first, second.
            return SmartRoutingSelector.SelectAsync(new SmartRoutingRequest
            {
                Tiers = new ModelTierSettings(),
                Policy = policy,
                Usage = service,
                Mission = new Mission { Persona = persona, Priority = priority, RetrySkipCaptainIds = retrySkip },
                Pool = new List<Captain> { new Captain("first") { Id = "first", Model = "model-a" }, new Captain("second") { Id = "second", Model = "model-b" } },
                BusyCaptainIds = busy ?? Array.Empty<string>(),
                NowUtc = DateTime.UtcNow,
                RandomPick = n => 0
            }).GetAwaiter().GetResult();
        }

        private static ProviderUsageSnapshot MeasuredSnapshot(double remaining)
        {
            return new ProviderUsageSnapshot
            {
                ObservedUtc = DateTime.UtcNow, Source = "fake-provider",
                Windows = new List<ProviderUsageWindow> { new ProviderUsageWindow { Name = "weekly", RemainingPercent = remaining, ResetsUtc = DateTime.UtcNow.AddDays(1) } }
            };
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Retry can use an approved low account instead of repeating a degraded normal account", () =>
            {
                UsageRoutingSettings policy = Policy(Account("first", 80), Account("second", 20));
                UsageRoutingDecision decision = Choose(new UsageRoutingService(), policy, retrySkip: "first");
                AssertEqual("second", decision.Candidates[0].Id);
            });
            await RunTest("A persona without routes is unrestricted and a wildcard route restricts", () =>
            {
                UsageRoutingSettings policy = Policy(Account("first", 80), Account("second", 90));
                UsageRoutingDecision ungoverned = Choose(new UsageRoutingService(), policy, "Planner");
                AssertEqual(2, ungoverned.Candidates.Count);
                AssertFalse(ungoverned.HasPersonaRoutes);
                policy.PersonaRoutes["*"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = "second" } };
                UsageRoutingDecision restricted = Choose(new UsageRoutingService(), policy, "Planner");
                AssertEqual("second", restricted.Candidates[0].Id);
                AssertEqual(1, restricted.Candidates.Count);
            });
            await RunTest("HTTP collectors use fixed read endpoints and keep credentials out of snapshots", async () =>
            {
                string path = Path.GetTempFileName();
                try
                {
                    await File.WriteAllTextAsync(path, "test-secret");
                    using (UsageHandler handler = new UsageHandler())
                    using (HttpClient client = new HttpClient(handler))
                    {
                        UsageAccountSettings account = new UsageAccountSettings { Collector = "OpenCodeGo", CredentialFilePath = path };
                        ProviderUsageSnapshot snapshot = await SubscriptionUsageCollector.CollectAsync(account, client);
                        AssertEqual("https://opencode.ai/zen/go/v1/usage", handler.Url);
                        AssertEqual("GET", handler.Method);
                        AssertEqual("Bearer test-secret", handler.Authorization);
                        AssertFalse(JsonSerializer.Serialize(snapshot).Contains("test-secret"));
                        handler.Limited = true;
                        try { await SubscriptionUsageCollector.CollectAsync(account, client); throw new Exception("Expected rate limit"); }
                        catch (UsageCollectionException ex)
                        {
                            AssertEqual("usage_http_429", ex.Code);
                            AssertTrue(ex.RetryAfterUtc > DateTime.UtcNow.AddMinutes(1));
                            AssertFalse(ex.Message.Contains("test-secret"));
                        }
                    }
                }
                finally { File.Delete(path); }
            });
            await RunTest("Legacy order wins over more remaining allowance", () =>
            {
                UsageRoutingSettings policy = Policy(Account("first", 45), Account("second", 95));
                AssertEqual("first", Choose(new UsageRoutingService(), policy).Candidates[0].Id);
            });
            await RunTest("Low routine work is demoted but a reserved persona keeps its position", () =>
            {
                UsageRoutingSettings policy = Policy(Account("first", 20), Account("second", 95));
                UsageRoutingService service = new UsageRoutingService();
                UsageRoutingDecision routine = Choose(service, policy);
                AssertEqual("second", routine.Candidates[0].Id);
                AssertEqual("first", routine.Candidates[1].Id, "a demoted captain stays reachable");
                AssertEqual("first", Choose(service, policy, "Judge").Candidates[0].Id);
            });
            await RunTest("Priority reserve is opt in and lower numeric priority wins", () =>
            {
                UsageRoutingSettings policy = Policy(Account("first", 5), Account("second", 95));
                policy.Accounts[0].ReservedPriorityAtOrAbove = 10;
                AssertEqual("first", Choose(new UsageRoutingService(), policy, priority: 10).Candidates[0].Id);
                AssertEqual("second", Choose(new UsageRoutingService(), policy, priority: 11).Candidates[0].Id);
            });
            await RunTest("Exhaustion removes a captain even for Judges and Reserve only demotes routine work", () =>
            {
                UsageRoutingSettings policy = Policy(Account("first", 0), Account("second", 5));
                UsageRoutingDecision routine = Choose(new UsageRoutingService(), policy);
                AssertEqual(1, routine.Candidates.Count);
                AssertEqual("second", routine.Candidates[0].Id);
                AssertEqual(SmartRoutingSelector.ReasonDemotedOnly, routine.Reason);
                AssertEqual("second", Choose(new UsageRoutingService(), policy, "Judge").Candidates[0].Id);
                UsageRoutingSettings blocked = Policy(Account("first", 0), Account("second", 0));
                UsageRoutingDecision none = Choose(new UsageRoutingService(), blocked);
                AssertEqual(0, none.Candidates.Count);
                AssertEqual(SmartRoutingSelector.ReasonUsageBlocked, none.Reason);
            });
            await RunTest("Low allowance remains usable when no normal fallback exists", () =>
            {
                UsageRoutingSettings policy = Policy(Account("first", 20), Account("second", 0));
                AssertEqual("first", Choose(new UsageRoutingService(), policy).Candidates[0].Id);
            });
            await RunTest("Recovery threshold prevents oscillation", () =>
            {
                UsageAccountSettings first = Account("first", 20);
                UsageRoutingSettings policy = Policy(first, Account("second", 90));
                UsageRoutingService service = new UsageRoutingService();
                AssertEqual("second", Choose(service, policy).Candidates[0].Id);
                first.ManualSnapshot!.Windows[0].RemainingPercent = 30;
                AssertEqual("second", Choose(service, policy).Candidates[0].Id);
                first.ManualSnapshot.Windows[0].RemainingPercent = 40;
                AssertEqual("first", Choose(service, policy).Candidates[0].Id);
            });
            await RunTest("Stale and reset measurements become unknown rather than full", () =>
            {
                UsageAccountSettings account = Account("first", 90);
                account.ManualSnapshot!.ObservedUtc = DateTime.UtcNow.AddHours(-1);
                UsageRoutingService service = new UsageRoutingService();
                AssertEqual("Unknown", service.GetStatus(account, null, DateTime.UtcNow).State);
                account.ManualSnapshot.ObservedUtc = DateTime.UtcNow;
                account.ManualSnapshot.Windows[0].ResetsUtc = DateTime.UtcNow.AddSeconds(-1);
                AssertEqual("Unknown", service.GetStatus(account, null, DateTime.UtcNow).State);
            });
            await RunTest("Unknown Block cannot hide behind a known low window", () =>
            {
                UsageAccountSettings account = Account("first", 20);
                account.UnknownUsagePolicy = "Block";
                account.ManualSnapshot!.Windows.Add(new ProviderUsageWindow { Name = "session" });
                AssertEqual("second", Choose(new UsageRoutingService(), Policy(account, Account("second", 90))).Candidates[0].Id);
            });
            await RunTest("Model window does not consume unrelated model allowance", () =>
            {
                UsageAccountSettings account = Account("first", 0);
                account.ManualSnapshot!.Windows[0].Models.Add("model-a");
                account.ManualSnapshot.Windows.Add(new ProviderUsageWindow { Name = "general", RemainingPercent = 90 });
                UsageRoutingService service = new UsageRoutingService();
                AssertEqual("Exhausted", service.GetStatus(account, "model-a", DateTime.UtcNow).State);
                AssertEqual("Normal", service.GetStatus(account, "model-b", DateTime.UtcNow).State);
            });
            await RunTest("Manual window mappings isolate exhausted models without changing the snapshot", () =>
            {
                UsageAccountSettings account = Account("first", 0);
                account.WindowModels["WEEKLY"] = new List<string> { "model-a" };
                account.ManualSnapshot!.Windows.Add(new ProviderUsageWindow { Name = "general", RemainingPercent = 90 });
                UsageRoutingService service = new UsageRoutingService();
                AssertEqual("Exhausted", service.GetStatus(account, "model-a", DateTime.UtcNow).State);
                AssertEqual("Normal", service.GetStatus(account, "model-b", DateTime.UtcNow).State);
                AssertEqual(0, account.ManualSnapshot.Windows[0].Models.Count);
                account.WindowModels["WEEKLY"] = new List<string> { "model-b" };
                AssertEqual("Normal", service.GetStatus(account, "model-a", DateTime.UtcNow).State);
                AssertEqual("Exhausted", service.GetStatus(account, "model-b", DateTime.UtcNow).State);
            });
            await RunTest("Reset grace releases Low but never Reserve", () =>
            {
                UsageAccountSettings account = Account("first", 20);
                account.ResetGraceMinutes = 10;
                account.ManualSnapshot!.Windows[0].ResetsUtc = DateTime.UtcNow.AddMinutes(5);
                UsageRoutingService service = new UsageRoutingService();
                AssertEqual("Normal", service.GetStatus(account, null, DateTime.UtcNow).State);
                account.ManualSnapshot.Windows[0].RemainingPercent = 5;
                AssertEqual("Reserve", service.GetStatus(account, null, DateTime.UtcNow).State);
            });
            await RunTest("Account capacity counts shared captain reservations", () =>
            {
                UsageRoutingSettings policy = Policy(Account("first", 90), Account("second", 90));
                policy.Accounts[0].CaptainIds.Add("busy-sibling");
                policy.Accounts[0].MaxConcurrentMissions = 1;
                AssertEqual("second", Choose(new UsageRoutingService(), policy, busy: new[] { "busy-sibling" }).Candidates[0].Id);
            });
            await RunTest("Explicit routes never use unapproved model", () =>
            {
                UsageRoutingSettings policy = Policy(Account("first", 90), Account("second", 90));
                policy.PersonaRoutes["Worker"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = "first", Models = new List<string> { "other" } } };
                AssertEqual(0, Choose(new UsageRoutingService(), policy).Candidates.Count);
            });
            await RunTest("Disabled usage routing preserves original candidate order", () =>
            {
                UsageRoutingSettings policy = Policy(Account("first", 0), Account("second", 90));
                policy.Enabled = false;
                AssertEqual("first", Choose(new UsageRoutingService(), policy).Candidates[0].Id);
            });
            await RunTest("Expired manual override restores measured state", () =>
            {
                UsageAccountSettings account = Account("first", 0);
                account.OverrideState = "Normal";
                account.OverrideUntilUtc = DateTime.UtcNow.AddMinutes(1);
                UsageRoutingService service = new UsageRoutingService();
                AssertEqual("Normal", service.GetStatus(account, null, DateTime.UtcNow).State);
                AssertEqual("Exhausted", service.GetStatus(account, null, DateTime.UtcNow.AddMinutes(2)).State);
            });
            await RunTest("Duplicate account membership and bad thresholds reject before apply", () =>
            {
                UsageRoutingSettings policy = Policy(Account("first", 90), Account("second", 90));
                policy.Accounts[1].CaptainIds.Add("first");
                AssertThrows<ArgumentException>(() => UsageRoutingService.Validate(policy));
                policy.Accounts[1].CaptainIds.Remove("first");
                policy.Accounts[0].RecoveryRemainingPercent = 5;
                AssertThrows<ArgumentException>(() => UsageRoutingService.Validate(policy));
            });
            await RunTest("Codex multi bucket parsing preserves missing and exhausted windows", () =>
            {
                ProviderUsageSnapshot snapshot = CodexUsageCollector.Parse("{\"result\":{\"rateLimitsByLimitId\":{\"general\":{\"primary\":{\"usedPercent\":25}},\"special\":{\"primary\":{\"usedPercent\":100},\"secondary\":{}}}}}", DateTime.UtcNow);
                AssertEqual(3, snapshot.Windows.Count);
                AssertEqual(75.0, snapshot.Windows[0].RemainingPercent!.Value);
                AssertEqual(0.0, snapshot.Windows[1].RemainingPercent!.Value);
                AssertTrue(snapshot.Windows[2].RemainingPercent == null);
            });
            await RunTest("Snapshot file collected without resetting observed timestamp", async () =>
            {
                string path = Path.GetTempFileName();
                try
                {
                    UsageAccountSettings account = Account("first", 15);
                    account.Collector = "File"; account.UsageFilePath = path;
                    DateTime observed = account.ManualSnapshot!.ObservedUtc;
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(account.ManualSnapshot));
                    UsageRoutingService service = new UsageRoutingService();
                    await service.RefreshAsync(Policy(account, Account("second", 90)));
                    ProviderUsageStatus status = service.GetStatus(account, null, DateTime.UtcNow);
                    AssertEqual("Low", status.State);
                    AssertEqual(observed, status.ObservedUtc!.Value);
                }
                finally { File.Delete(path); }
            });
            await RunTest("Invalid file is unknown with a safe collection error", async () =>
            {
                string path = Path.GetTempFileName();
                try
                {
                    UsageAccountSettings account = Account("first", 90);
                    account.Collector = "File"; account.UsageFilePath = path;
                    await File.WriteAllTextAsync(path, "private invalid text");
                    UsageRoutingService service = new UsageRoutingService();
                    await service.RefreshAsync(Policy(account, Account("second", 90)));
                    ProviderUsageStatus status = service.GetStatus(account, null, DateTime.UtcNow);
                    AssertEqual("Unknown", status.State);
                    AssertEqual("usage_snapshot_unavailable_or_invalid", status.CollectionError);
                }
                finally { File.Delete(path); }
            });
            await RunTest("Claude scoped weekly limits remain separate from general allowance", () =>
            {
                ProviderUsageSnapshot snapshot = SubscriptionUsageCollector.Parse("Claude", "{\"five_hour\":{\"utilization\":10},\"seven_day\":{\"utilization\":20},\"limits\":[{\"kind\":\"weekly_scoped\",\"percent\":95,\"scope\":{\"model\":{\"id\":\"special-model\"}}}]}", DateTime.UtcNow);
                AssertEqual(3, snapshot.Windows.Count);
                AssertEqual("weekly_scoped/special-model", snapshot.Windows[2].Name);
                AssertEqual(5.0, snapshot.Windows[2].RemainingPercent!.Value);
            });
            await RunTest("Cursor fractions are percentages and independent pools are not averaged", () =>
            {
                ProviderUsageSnapshot snapshot = SubscriptionUsageCollector.Parse("Cursor", "{\"individualUsage\":{\"plan\":{\"autoPercentUsed\":0.5,\"apiPercentUsed\":100,\"totalPercentUsed\":50.25}}}", DateTime.UtcNow);
                AssertEqual(2, snapshot.Windows.Count);
                AssertEqual(99.5, snapshot.Windows[0].RemainingPercent!.Value);
                AssertEqual(0.0, snapshot.Windows[1].RemainingPercent!.Value);
            });
            await RunTest("Cursor API pool exhaustion leaves Composer and Grok captains available", async () =>
            {
                DateTime now = DateTime.UtcNow;
                UsageAccountSettings account = new UsageAccountSettings
                {
                    Id = "cursor", Collector = "Cursor",
                    ManualSnapshot = new ProviderUsageSnapshot
                    {
                        ObservedUtc = now, Source = "test",
                        Windows = new List<ProviderUsageWindow>
                        {
                            new ProviderUsageWindow { Name = "cursor_models", RemainingPercent = 40, ResetsUtc = now.AddDays(1) },
                            new ProviderUsageWindow { Name = "third_party", RemainingPercent = 0, ResetsUtc = now.AddDays(1) }
                        }
                    }
                };
                UsageRoutingSettings policy = new UsageRoutingSettings { Enabled = true, Accounts = new List<UsageAccountSettings> { account } };
                UsageRoutingService service = new UsageRoutingService { ProviderCollector = (_, _) => Task.FromResult(account.ManualSnapshot!) };
                await service.RefreshAsync(policy);
                AssertEqual("Partial", service.GetStatus(account, null, now).State);
                AssertEqual("Normal", service.GetStatus(account, "composer-2.5", now).State);
                AssertEqual("Normal", service.GetStatus(account, "cursor-grok-4.7-high", now).State);
                AssertEqual("Exhausted", service.GetStatus(account, "gpt-5.6-luna", now).State);
            });
            await RunTest("Cursor API captains lead only while their measured pool has usage", async () =>
            {
                DateTime now = DateTime.UtcNow;
                UsageAccountSettings account = new UsageAccountSettings
                {
                    Id = "cursor", Collector = "Cursor", CaptainIds = new List<string> { "api", "composer", "grok" },
                    ManualSnapshot = new ProviderUsageSnapshot
                    {
                        ObservedUtc = now, Source = "test",
                        Windows = new List<ProviderUsageWindow>
                        {
                            new ProviderUsageWindow { Name = "cursor_models", RemainingPercent = 40, ResetsUtc = now.AddDays(1) },
                            new ProviderUsageWindow { Name = "third_party", RemainingPercent = 20, ResetsUtc = now.AddDays(1) }
                        }
                    }
                };
                UsageRoutingSettings policy = new UsageRoutingSettings
                {
                    Enabled = true, Accounts = new List<UsageAccountSettings> { account },
                    PersonaRoutes = new Dictionary<string, List<UsageRouteSettings>>
                    {
                        ["Worker"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = "cursor" } }
                    },
                    PersonaModels = new Dictionary<string, PersonaModelSettings>
                    {
                        ["Worker"] = new PersonaModelSettings
                        {
                            Default = new List<string> { "composer-2.5" },
                            Stronger = new List<string> { "cursor-grok-4.7-high" }
                        }
                    }
                };
                List<Captain> captains = new List<Captain>
                {
                    new Captain("api") { Id = "api", Model = "gpt-6-luna" },
                    new Captain("composer") { Id = "composer", Model = "composer-2.5" },
                    new Captain("grok") { Id = "grok", Model = "cursor-grok-4.7-high" }
                };
                UsageRoutingService service = new UsageRoutingService { ProviderCollector = (_, _) => Task.FromResult(account.ManualSnapshot!) };
                await service.RefreshAsync(policy);
                UsageRoutingDecision decision = SmartRoutingSelector.SelectAsync(new SmartRoutingRequest
                {
                    Tiers = new ModelTierSettings(), Policy = policy, Usage = service,
                    Mission = new Mission { Persona = "Worker" }, Pool = captains,
                    BusyCaptainIds = Array.Empty<string>(), NowUtc = now, RandomPick = n => 0
                }).GetAwaiter().GetResult();
                AssertEqual("api", decision.Candidates[0].Id);
                account.ManualSnapshot.Windows[1].RemainingPercent = 0;
                decision = SmartRoutingSelector.SelectAsync(new SmartRoutingRequest
                {
                    Tiers = new ModelTierSettings(), Policy = policy, Usage = service,
                    Mission = new Mission { Persona = "Worker" }, Pool = captains,
                    BusyCaptainIds = Array.Empty<string>(), NowUtc = now, RandomPick = n => 0
                }).GetAwaiter().GetResult();
                AssertEqual("composer", decision.Candidates[0].Id);
            });
            await RunTest("OpenCode Go fractional percent and reset interval preserve units", () =>
            {
                DateTime now = DateTime.UtcNow;
                ProviderUsageSnapshot snapshot = SubscriptionUsageCollector.Parse("OpenCodeGo", "{\"usage\":{\"rolling\":{\"percent\":1,\"resetInSec\":600},\"weekly\":{\"percent\":70},\"monthly\":{\"percent\":90}}}", now);
                AssertEqual(99.0, snapshot.Windows[0].RemainingPercent!.Value);
                AssertEqual(now.AddSeconds(600), snapshot.Windows[0].ResetsUtc!.Value);
                AssertEqual(10.0, snapshot.Windows[2].RemainingPercent!.Value);
            });
            await RunTest("Missing provider fields never report full allowance", () =>
            {
                foreach (string provider in new[] { "Claude", "Cursor", "OpenCodeGo" })
                {
                    ProviderUsageSnapshot snapshot = SubscriptionUsageCollector.Parse(provider, "{}", DateTime.UtcNow);
                    AssertTrue(snapshot.Windows.All(w => w.RemainingPercent == null));
                }
            });
            await RunTest("Window model mapping isolates exhausted pools after collection", async () =>
            {
                string path = Path.GetTempFileName();
                try
                {
                    UsageAccountSettings account = Account("first", 0);
                    account.Collector = "File"; account.UsageFilePath = path;
                    account.WindowModels["weekly"] = new List<string> { "model-a" };
                    account.ManualSnapshot!.Windows.Add(new ProviderUsageWindow { Name = "general", RemainingPercent = 80 });
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(account.ManualSnapshot));
                    UsageRoutingService service = new UsageRoutingService();
                    await service.RefreshAsync(Policy(account, Account("second", 90)));
                    AssertEqual("Exhausted", service.GetStatus(account, "model-a", DateTime.UtcNow).State);
                    AssertEqual("Normal", service.GetStatus(account, "model-b", DateTime.UtcNow).State);
                }
                finally { File.Delete(path); }
            });
            await RunTest("Hard refresh reads one account now, bypassing the refresh interval that throttles timed refreshes", async () =>
            {
                UsageAccountSettings account = new UsageAccountSettings { Id = "claude-a", Collector = "Claude" };
                UsageRoutingSettings policy = new UsageRoutingSettings { Enabled = true, RefreshIntervalMinutes = 60, Accounts = new List<UsageAccountSettings> { account } };
                int calls = 0;
                UsageRoutingService service = new UsageRoutingService { ProviderCollector = (a, t) => { Interlocked.Increment(ref calls); return Task.FromResult(MeasuredSnapshot(80)); } };
                await service.RefreshAsync(policy);
                await service.RefreshAsync(policy);
                AssertEqual(1, calls, "a timed refresh inside the interval does not read again");
                UsageAccountRefreshResult? first = await service.RefreshAccountAsync(policy, "claude-a");
                UsageAccountRefreshResult? second = await service.RefreshAccountAsync(policy, "claude-a");
                AssertEqual(3, calls, "each hard refresh reads the provider");
                AssertNotNull(first);
                AssertTrue(second!.Collected);
                AssertEqual(UsageRoutingService.ReasonRefreshed, second.Reason);
                AssertEqual("Normal", second.Status.State);
                AssertNotNull(second.Status.ObservedUtc);
                AssertEqual(1, second.Status.Windows.Count);
                await service.RefreshAsync(policy);
                AssertEqual(3, calls, "a hard refresh does not reset the timed throttle into an extra read");
            });
            await RunTest("Back-to-back hard refreshes each read the provider, never reusing the read that just finished", async () =>
            {
                UsageAccountSettings account = new UsageAccountSettings { Id = "claude-sequential", Collector = "Claude" };
                UsageRoutingSettings policy = new UsageRoutingSettings { Enabled = true, Accounts = new List<UsageAccountSettings> { account } };
                int calls = 0;
                UsageRoutingService service = new UsageRoutingService { ProviderCollector = (a, t) => { Interlocked.Increment(ref calls); return Task.FromResult(MeasuredSnapshot(40)); } };
                const int refreshes = 500;
                for (int i = 0; i < refreshes; i++)
                    await service.RefreshAccountAsync(policy, "claude-sequential");
                AssertEqual(refreshes, calls, "a hard refresh that starts after the previous one finished reads the provider again");
            });
            await RunTest("Hard refresh honours an active provider retry-after and does not call the provider", async () =>
            {
                UsageAccountSettings account = new UsageAccountSettings { Id = "claude-limited", Collector = "Claude" };
                UsageRoutingSettings policy = new UsageRoutingSettings { Enabled = true, Accounts = new List<UsageAccountSettings> { account } };
                DateTime retryAt = DateTime.UtcNow.AddMinutes(5);
                int calls = 0;
                UsageRoutingService service = new UsageRoutingService { ProviderCollector = (a, t) => { Interlocked.Increment(ref calls); throw new UsageCollectionException("usage_provider_rate_limited", retryAt); } };
                await service.RefreshAsync(policy);
                AssertEqual(1, calls);
                UsageAccountRefreshResult? result = await service.RefreshAccountAsync(policy, "claude-limited");
                AssertEqual(1, calls, "the provider is not called while its retry-after is active");
                AssertFalse(result!.Collected);
                AssertEqual(UsageRoutingService.ReasonRefreshRateLimited, result.Reason);
                AssertEqual(retryAt, result.RetryAfterUtc!.Value);
                AssertEqual("claude-limited", result.Status.AccountId);
            });
            await RunTest("Concurrent hard refreshes of one account share one in-flight provider read", async () =>
            {
                UsageAccountSettings account = new UsageAccountSettings { Id = "claude-shared", Collector = "Claude" };
                UsageRoutingSettings policy = new UsageRoutingSettings { Enabled = true, Accounts = new List<UsageAccountSettings> { account } };
                TaskCompletionSource<bool> gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int calls = 0;
                UsageRoutingService service = new UsageRoutingService
                {
                    ProviderCollector = async (a, t) => { Interlocked.Increment(ref calls); await gate.Task; return MeasuredSnapshot(50); }
                };
                Task<UsageAccountRefreshResult?> one = service.RefreshAccountAsync(policy, "claude-shared");
                Task<UsageAccountRefreshResult?> two = service.RefreshAccountAsync(policy, "claude-shared");
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (calls == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
                await Task.Delay(50);
                gate.SetResult(true);
                await Task.WhenAll(one, two);
                AssertEqual(1, calls, "one provider read serves both refreshes");
                AssertTrue(one.Result!.Collected && two.Result!.Collected);
            });
            await RunTest("Hard refresh of an account missing from the policy returns nothing", async () =>
            {
                UsageRoutingService service = new UsageRoutingService { ProviderCollector = (a, t) => throw new InvalidOperationException("must not be called") };
                AssertNull(await service.RefreshAccountAsync(new UsageRoutingSettings(), "missing"));
            });
            await RunTest("Hot reload carries usage configuration", () =>
            {
                ArmadaSettings live = new ArmadaSettings();
                ArmadaSettings updated = new ArmadaSettings();
                updated.ModelTier.UsageRouting = Policy(Account("first", 90), Account("second", 90));
                live.ApplyHotReloadableFrom(updated);
                AssertTrue(live.ModelTier.UsageRouting.Enabled);
                AssertEqual(2, live.ModelTier.UsageRouting.Accounts.Count);
            });
        }
        private sealed class UsageHandler : HttpMessageHandler
        {
            internal string? Url;
            internal string? Method;
            internal string? Authorization;
            internal bool Limited;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Url = request.RequestUri?.AbsoluteUri;
                Method = request.Method.Method;
                Authorization = request.Headers.Authorization?.ToString();
                HttpResponseMessage response = new HttpResponseMessage(Limited ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"usage\":{\"rolling\":{\"percent\":20,\"resetInSec\":600}}}")
                };
                if (Limited) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                return Task.FromResult(response);
            }
        }

    }
}
