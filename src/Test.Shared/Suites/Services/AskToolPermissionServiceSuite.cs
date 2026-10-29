namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Native tool permission prompts of attended saved Ask turns: storage, decision, expiry, rules and reach.</summary>
    public sealed class AskToolPermissionServiceSuite : IArmadaTestSuite
    {
        private const string SuiteId = "Services.AskToolPermission";

        /// <summary>Register the permission prompt cases.</summary>
        public TestSuiteDescriptor Build()
        {
            return new TestSuiteDescriptor(SuiteId, "Ask native tool permissions", new List<TestCaseDescriptor>
            {
                Case("a_pending_request_waits_for_the_owner_and_allows_with_the_original_input", AllowWaitsForOwnerAsync, TestTags.Positive),
                Case("a_deny_decision_refuses_the_call_with_a_reason", DenyAsync, TestTags.Negative),
                Case("a_request_nobody_decides_expires_and_is_denied", ExpiryDeniesAsync, TestTags.Negative),
                Case("a_request_is_decided_once", DecideOnceAsync, TestTags.Negative),
                Case("a_remembered_rule_answers_later_requests_without_waiting", RememberedRuleAsync, TestTags.Positive),
                Case("a_rule_is_scoped_to_its_thread", RuleScopeAsync, TestTags.Negative),
                Case("only_the_thread_owner_reads_and_decides", OwnerScopeAsync, TestTags.Negative),
                Case("only_the_running_ask_turn_credential_reaches_the_prompt", CredentialReachAsync, TestTags.Negative),
                Case("deleting_the_thread_removes_its_requests_and_rules", ThreadDeleteAsync, TestTags.Positive),
                Case("the_prompt_tool_returns_the_permission_contract_json", ToolResultShapeAsync, TestTags.Positive)
            });
        }

        private static TestCaseDescriptor Case(string id, Func<Task> body, string tag)
        {
            return new TestCaseDescriptor(SuiteId, id, id, (CancellationToken _) => body(), new List<string> { tag });
        }

        private static async Task AllowWaitsForOwnerAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(db.Driver).ConfigureAwait(false);
                AskToolPermissionService service = CreateService(db.Driver);
                Task<AskToolPermissionPromptResult> pending = service.RequestAsync(fixture.Scoped, "Bash", "{\"command\":\"ls\"}", "toolu_1");
                AskToolPermissionRequest request = await WaitForPendingAsync(service, fixture).ConfigureAwait(false);
                AssertEqual("Bash", request.ToolName);
                AssertFalse(pending.IsCompleted, "the call waits for a decision");

                AskToolPermissionRequest? decided = await service.DecideAsync(fixture.Owner, fixture.Thread.Id, request.Id, true, false).ConfigureAwait(false);
                AssertEqual(AskToolPermissionStateEnum.Allowed, decided!.State);
                AskToolPermissionPromptResult result = await pending.ConfigureAwait(false);
                AssertEqual("allow", result.Behavior);
                AssertEqual("ls", result.UpdatedInput!.Value.GetProperty("command").GetString());
                AssertEqual(0, (await db.Driver.AskToolPermissions.EnumerateRulesAsync(fixture.Owner.TenantId!, fixture.Owner.UserId!, fixture.Thread.Id).ConfigureAwait(false)).Count, "an unremembered decision leaves no rule");
                AssertEqual(1, (await db.Driver.Events.EnumerateByTypeAsync("ask_tool_permission.decided", 10).ConfigureAwait(false)).Count, "the decision is audited");
            }
        }

        private static async Task DenyAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(db.Driver).ConfigureAwait(false);
                AskToolPermissionService service = CreateService(db.Driver);
                Task<AskToolPermissionPromptResult> pending = service.RequestAsync(fixture.Scoped, "Write", "{}", null);
                AskToolPermissionRequest request = await WaitForPendingAsync(service, fixture).ConfigureAwait(false);
                await service.DecideAsync(fixture.Owner, fixture.Thread.Id, request.Id, false, false).ConfigureAwait(false);
                AskToolPermissionPromptResult result = await pending.ConfigureAwait(false);
                AssertEqual("deny", result.Behavior);
                AssertContains("denied", result.Message!);
                AssertNull(result.UpdatedInput);
            }
        }

        private static async Task ExpiryDeniesAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(db.Driver).ConfigureAwait(false);
                AskSettings settings = new AskSettings { ToolApprovalTimeoutSeconds = 10 };
                AskToolPermissionService service = CreateService(db.Driver, settings);
                AskToolPermissionPromptResult result = await service.RequestAsync(fixture.Scoped, "Bash", "{}", null).ConfigureAwait(false);
                AssertEqual("deny", result.Behavior);
                AssertContains("No decision arrived", result.Message!);
                List<AskToolPermissionRequest> requests = (await service.ListRequestsAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false))!;
                AssertEqual(1, requests.Count);
                AssertEqual(AskToolPermissionStateEnum.Expired, requests[0].State);
                bool lateDecision = await db.Driver.AskToolPermissions.TryDecideAsync(fixture.Owner.TenantId!, fixture.Owner.UserId!, fixture.Thread.Id, requests[0].Id, true, AskToolPermissionSourceEnum.Owner, DateTime.UtcNow).ConfigureAwait(false);
                AssertFalse(lateDecision, "an expired request cannot be allowed afterward");
            }
        }

        private static async Task DecideOnceAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(db.Driver).ConfigureAwait(false);
                AskToolPermissionService service = CreateService(db.Driver);
                Task<AskToolPermissionPromptResult> pending = service.RequestAsync(fixture.Scoped, "Bash", "{}", null);
                AskToolPermissionRequest request = await WaitForPendingAsync(service, fixture).ConfigureAwait(false);
                await service.DecideAsync(fixture.Owner, fixture.Thread.Id, request.Id, false, false).ConfigureAwait(false);
                await pending.ConfigureAwait(false);
                await AssertThrowsAsync<InvalidOperationException>(() => service.DecideAsync(fixture.Owner, fixture.Thread.Id, request.Id, true, false)).ConfigureAwait(false);
                AskToolPermissionRequest stored = (await db.Driver.AskToolPermissions.ReadRequestAsync(fixture.Owner.TenantId!, fixture.Owner.UserId!, fixture.Thread.Id, request.Id).ConfigureAwait(false))!;
                AssertEqual(AskToolPermissionStateEnum.Denied, stored.State, "the first decision stands");
            }
        }

        private static async Task RememberedRuleAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(db.Driver).ConfigureAwait(false);
                AskToolPermissionService service = CreateService(db.Driver);
                Task<AskToolPermissionPromptResult> first = service.RequestAsync(fixture.Scoped, "Bash", "{\"command\":\"ls\"}", null);
                AskToolPermissionRequest request = await WaitForPendingAsync(service, fixture).ConfigureAwait(false);
                await service.DecideAsync(fixture.Owner, fixture.Thread.Id, request.Id, true, true).ConfigureAwait(false);
                await first.ConfigureAwait(false);

                Task<AskToolPermissionPromptResult> second = service.RequestAsync(fixture.Scoped, "Bash", "{\"command\":\"pwd\"}", null);
                AssertTrue(await Task.WhenAny(second, Task.Delay(5000)).ConfigureAwait(false) == second, "a remembered allow answers without waiting");
                AssertEqual("allow", (await second.ConfigureAwait(false)).Behavior);
                List<AskToolPermissionRequest> requests = (await service.ListRequestsAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false))!;
                AssertTrue(requests.Exists(item => item.DecisionSource == AskToolPermissionSourceEnum.Rule), "the rule decision is recorded as such");

                List<AskToolPermissionRule> rules = (await service.ListRulesAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false))!;
                AssertEqual(1, rules.Count);
                AssertTrue(await service.DeleteRuleAsync(fixture.Owner, fixture.Thread.Id, rules[0].Id).ConfigureAwait(false));
                Task<AskToolPermissionPromptResult> third = service.RequestAsync(fixture.Scoped, "Bash", "{}", null);
                await WaitForPendingAsync(service, fixture).ConfigureAwait(false);
                AssertFalse(third.IsCompleted, "a forgotten rule asks again");
                third.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        private static async Task RuleScopeAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(db.Driver).ConfigureAwait(false);
                AskThread other = await db.Driver.AskThreads.CreateAsync(new AskThread { TenantId = fixture.Owner.TenantId!, UserId = fixture.Owner.UserId!, Title = "Other" }).ConfigureAwait(false);
                await db.Driver.AskToolPermissions.UpsertRuleAsync(new AskToolPermissionRule
                {
                    TenantId = fixture.Owner.TenantId!, UserId = fixture.Owner.UserId!, ThreadId = other.Id, ToolName = "Bash", Action = AskToolPermissionRuleActionEnum.Allow
                }).ConfigureAwait(false);
                AskToolPermissionService service = CreateService(db.Driver);
                Task<AskToolPermissionPromptResult> pending = service.RequestAsync(fixture.Scoped, "Bash", "{}", null);
                await WaitForPendingAsync(service, fixture).ConfigureAwait(false);
                AssertFalse(pending.IsCompleted, "a rule of another thread does not answer this thread");
                pending.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        private static async Task OwnerScopeAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(db.Driver).ConfigureAwait(false);
                AskToolPermissionService service = CreateService(db.Driver);
                Task<AskToolPermissionPromptResult> pending = service.RequestAsync(fixture.Scoped, "Bash", "{}", null);
                AskToolPermissionRequest request = await WaitForPendingAsync(service, fixture).ConfigureAwait(false);

                AuthContext stranger = AuthContext.Authenticated(fixture.Owner.TenantId!, "usr_stranger", true, true, "Session");
                AssertNull(await service.ListRequestsAsync(stranger, fixture.Thread.Id).ConfigureAwait(false), "another user cannot read the requests");
                AssertNull(await service.DecideAsync(stranger, fixture.Thread.Id, request.Id, true, false).ConfigureAwait(false), "another user cannot decide");
                await AssertThrowsAsync<UnauthorizedAccessException>(() => service.DecideAsync(fixture.Scoped, fixture.Thread.Id, request.Id, true, false)).ConfigureAwait(false);
                AuthContext nonAdmin = AuthContext.Authenticated(fixture.Owner.TenantId!, fixture.Owner.UserId!, false, true, "Session");
                await AssertThrowsAsync<UnauthorizedAccessException>(() => service.DecideAsync(nonAdmin, fixture.Thread.Id, request.Id, true, false)).ConfigureAwait(false);
                AssertFalse(pending.IsCompleted, "no refused caller released the call");

                await service.DecideAsync(fixture.Owner, fixture.Thread.Id, request.Id, false, false).ConfigureAwait(false);
                await pending.ConfigureAwait(false);
            }
        }

        private static async Task CredentialReachAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(db.Driver).ConfigureAwait(false);
                AskToolPermissionService service = CreateService(db.Driver);

                AskToolPermissionPromptResult human = await service.RequestAsync(fixture.Owner, "Bash", "{}", null).ConfigureAwait(false);
                AssertEqual("deny", human.Behavior, "a human credential is refused");
                AuthContext mission = AuthContext.Authenticated(fixture.Owner.TenantId!, fixture.Owner.UserId!, true, true, "Session");
                mission.MissionId = "msn_example";
                AssertEqual("deny", (await service.RequestAsync(mission, "Bash", "{}", null).ConfigureAwait(false)).Behavior, "a mission credential is refused");

                AuthContext wrongTurn = AuthContext.Authenticated(fixture.Owner.TenantId!, fixture.Owner.UserId!, true, true, "Session");
                wrongTurn.AskThreadId = fixture.Thread.Id;
                wrongTurn.AskTurnId = "request_not_running";
                AskToolPermissionPromptResult stale = await service.RequestAsync(wrongTurn, "Bash", "{}", null).ConfigureAwait(false);
                AssertEqual("deny", stale.Behavior, "a turn that is not the thread's running turn is refused");
                AssertContains("no longer running", stale.Message!);

                await db.Driver.AskThreads.CompleteTurnAsync(fixture.Owner.TenantId!, fixture.Owner.UserId!, fixture.Thread.Id, fixture.Turn.RequestId, new CaptainChatResponse { Success = true, Reply = "Done" }).ConfigureAwait(false);
                AskToolPermissionPromptResult finished = await service.RequestAsync(fixture.Scoped, "Bash", "{}", null).ConfigureAwait(false);
                AssertEqual("deny", finished.Behavior, "a finished turn is refused");
                AssertEqual(0, (await service.ListRequestsAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false))!.Count, "refused calls store no request");

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);
                McpAskToolPermissionTools.Register((name, description, schema, handler) => handlers[name] = handler, service);
                AssertTrue(McpToolAccessPolicy.IsAllowed(fixture.Scoped, AskToolPolicy.PermissionPromptToolName), "the Ask turn credential sees the prompt tool");
                AssertFalse(McpToolAccessPolicy.IsAllowed(fixture.Owner, AskToolPolicy.PermissionPromptToolName), "a human credential does not see the prompt tool");
                AssertFalse(McpToolAccessPolicy.IsAllowed(mission, AskToolPolicy.PermissionPromptToolName), "a mission credential does not see the prompt tool");
            }
        }

        private static async Task ThreadDeleteAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(db.Driver).ConfigureAwait(false);
                AskToolPermissionService service = CreateService(db.Driver);
                Task<AskToolPermissionPromptResult> pending = service.RequestAsync(fixture.Scoped, "Bash", "{}", null);
                AskToolPermissionRequest request = await WaitForPendingAsync(service, fixture).ConfigureAwait(false);
                await service.DecideAsync(fixture.Owner, fixture.Thread.Id, request.Id, true, true).ConfigureAwait(false);
                await pending.ConfigureAwait(false);

                await db.Driver.AskThreads.CompleteTurnAsync(fixture.Owner.TenantId!, fixture.Owner.UserId!, fixture.Thread.Id, fixture.Turn.RequestId, new CaptainChatResponse { Success = true, Reply = "Done" }).ConfigureAwait(false);
                fixture.Thread.Archived = true;
                await db.Driver.AskThreads.UpdateAsync(fixture.Thread).ConfigureAwait(false);
                AssertEqual(AskThreadDeleteOutcomeEnum.Deleted, await db.Driver.AskThreads.TryDeleteAsync(fixture.Owner.TenantId!, fixture.Owner.UserId!, fixture.Thread.Id).ConfigureAwait(false));
                AssertEqual(0, (await db.Driver.AskToolPermissions.EnumerateRequestsAsync(fixture.Owner.TenantId!, fixture.Owner.UserId!, fixture.Thread.Id).ConfigureAwait(false)).Count);
                AssertEqual(0, (await db.Driver.AskToolPermissions.EnumerateRulesAsync(fixture.Owner.TenantId!, fixture.Owner.UserId!, fixture.Thread.Id).ConfigureAwait(false)).Count);
            }
        }

        private static async Task ToolResultShapeAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(db.Driver).ConfigureAwait(false);
                await db.Driver.AskToolPermissions.UpsertRuleAsync(new AskToolPermissionRule
                {
                    TenantId = fixture.Owner.TenantId!, UserId = fixture.Owner.UserId!, ThreadId = fixture.Thread.Id, ToolName = "Read", Action = AskToolPermissionRuleActionEnum.Allow
                }).ConfigureAwait(false);
                await db.Driver.AskToolPermissions.UpsertRuleAsync(new AskToolPermissionRule
                {
                    TenantId = fixture.Owner.TenantId!, UserId = fixture.Owner.UserId!, ThreadId = fixture.Thread.Id, ToolName = "Bash", Action = AskToolPermissionRuleActionEnum.Deny
                }).ConfigureAwait(false);
                AskToolPermissionService service = CreateService(db.Driver);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);
                McpAskToolPermissionTools.Register((name, description, schema, handler) => handlers[name] = handler, service);
                Func<JsonElement?, Task<object>> handler = handlers[AskToolPolicy.PermissionPromptToolName];

                string allowed = await CallAsync(handler, fixture.Scoped, "{\"tool_name\":\"Read\",\"input\":{\"file_path\":\"a.txt\"},\"tool_use_id\":\"toolu_2\"}").ConfigureAwait(false);
                using (JsonDocument allow = JsonDocument.Parse(allowed))
                {
                    AssertEqual("allow", allow.RootElement.GetProperty("behavior").GetString());
                    AssertEqual("a.txt", allow.RootElement.GetProperty("updatedInput").GetProperty("file_path").GetString());
                    AssertFalse(allow.RootElement.TryGetProperty("message", out _), "an allow carries no message");
                }

                string denied = await CallAsync(handler, fixture.Scoped, "{\"tool_name\":\"Bash\",\"input\":{}}").ConfigureAwait(false);
                using (JsonDocument deny = JsonDocument.Parse(denied))
                {
                    AssertEqual("deny", deny.RootElement.GetProperty("behavior").GetString());
                    AssertTrue(deny.RootElement.GetProperty("message").GetString()!.Length > 0, "a deny carries a reason");
                    AssertFalse(deny.RootElement.TryGetProperty("updatedInput", out _), "a deny carries no input");
                }

                AssertContains("\"behavior\":\"deny\"", await CallAsync(handler, fixture.Scoped, "{}").ConfigureAwait(false), "a call with no tool name is denied");
            }
        }

        private static async Task<string> CallAsync(Func<JsonElement?, Task<object>> handler, AuthContext caller, string arguments)
        {
            using (McpCallerContext.Begin(caller))
            using (JsonDocument document = JsonDocument.Parse(arguments))
            {
                object result = await handler(document.RootElement.Clone()).ConfigureAwait(false);
                return JsonSerializer.Serialize(result);
            }
        }

        private static async Task<AskToolPermissionRequest> WaitForPendingAsync(AskToolPermissionService service, Fixture fixture)
        {
            DateTime limit = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < limit)
            {
                List<AskToolPermissionRequest> requests = (await service.ListRequestsAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false))!;
                AskToolPermissionRequest? pending = requests.Find(item => item.State == AskToolPermissionStateEnum.Pending);
                if (pending != null) return pending;
                await Task.Delay(20).ConfigureAwait(false);
            }
            throw new InvalidOperationException("No pending permission request appeared.");
        }

        private static AskToolPermissionService CreateService(DatabaseDriver database, AskSettings? settings = null)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new AskToolPermissionService(database, settings ?? new AskSettings(), logging) { PollMilliseconds = 20 };
        }

        private static async Task<Fixture> CreateFixtureAsync(DatabaseDriver database)
        {
            TenantMetadata tenant = new TenantMetadata("Ask permissions");
            await database.Tenants.CreateAsync(tenant).ConfigureAwait(false);
            UserMaster user = new UserMaster(tenant.Id, "ask-permissions@example.invalid", "test-password") { IsAdmin = true };
            await database.Users.CreateAsync(user).ConfigureAwait(false);
            AskThread thread = await database.AskThreads.CreateAsync(new AskThread { TenantId = tenant.Id, UserId = user.Id, Title = "Permission test" }).ConfigureAwait(false);
            AskTurn turn = new AskTurn { ThreadId = thread.Id, RequestId = "request_" + Guid.NewGuid().ToString("N"), CaptainId = "cpt_test", Content = "Please look" };
            AskTurnReservation reservation = await database.AskThreads.TryReserveTurnAsync(tenant.Id, user.Id, turn).ConfigureAwait(false);
            AssertEqual(AskTurnReservationResultEnum.Created, reservation.Result);
            AssertTrue(await database.AskThreads.MarkRunningAsync(tenant.Id, user.Id, thread.Id, turn.RequestId).ConfigureAwait(false));
            thread = (await database.AskThreads.ReadAsync(tenant.Id, user.Id, thread.Id).ConfigureAwait(false))!;
            AuthContext owner = AuthContext.Authenticated(tenant.Id, user.Id, true, true, "Session");
            AuthContext scoped = AuthContext.Authenticated(tenant.Id, user.Id, true, true, "Session");
            scoped.AskThreadId = thread.Id;
            scoped.AskTurnId = turn.RequestId;
            return new Fixture(thread, turn, owner, scoped);
        }

        private static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception
        {
            try { await action().ConfigureAwait(false); }
            catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }

        private sealed class Fixture
        {
            public AskThread Thread { get; }
            public AskTurn Turn { get; }
            public AuthContext Owner { get; }
            public AuthContext Scoped { get; }
            public Fixture(AskThread thread, AskTurn turn, AuthContext owner, AuthContext scoped)
            {
                Thread = thread; Turn = turn; Owner = owner; Scoped = scoped;
            }
        }
    }
}
