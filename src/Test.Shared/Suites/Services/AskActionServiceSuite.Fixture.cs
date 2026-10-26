namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;
    using Armada.Server.Mcp;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class AskActionServiceSuite
    {
        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static AskActionService CreateService(DatabaseDriver database, LongRunningJobService? jobs = null)
        {
            return new AskActionService(database, jobs ?? new LongRunningJobService(), CreateLogging());
        }

        private static async Task<Fixture> CreateFixtureAsync(DatabaseDriver database, string? threadId = null, string? requestId = null)
        {
            TenantMetadata tenant = new TenantMetadata("Ask actions");
            await database.Tenants.CreateAsync(tenant).ConfigureAwait(false);
            UserMaster user = new UserMaster(tenant.Id, "ask-actions@example.invalid", "test-password");
            user.IsAdmin = true;
            await database.Users.CreateAsync(user).ConfigureAwait(false);
            AskThread thread = await database.AskThreads.CreateAsync(new AskThread
            {
                Id = threadId ?? new AskThread().Id,
                TenantId = tenant.Id,
                UserId = user.Id,
                Title = "Action test"
            }).ConfigureAwait(false);
            AskTurn turn = new AskTurn
            {
                ThreadId = thread.Id,
                RequestId = requestId ?? "request_" + Guid.NewGuid().ToString("N"),
                CaptainId = "cpt_test",
                Content = "Please do this"
            };
            AskTurnReservation reservation = await database.AskThreads.TryReserveTurnAsync(tenant.Id, user.Id, turn).ConfigureAwait(false);
            AssertEqual(AskTurnReservationResultEnum.Created, reservation.Result, "The fixture reserves one durable turn");
            AssertTrue(await database.AskThreads.MarkRunningAsync(tenant.Id, user.Id, thread.Id, turn.RequestId).ConfigureAwait(false), "The fixture marks its turn running");
            thread = await database.AskThreads.ReadAsync(tenant.Id, user.Id, thread.Id).ConfigureAwait(false) ?? throw new InvalidOperationException("Fixture thread was not stored.");
            return new Fixture(thread, turn, AuthContext.Authenticated(tenant.Id, user.Id, true, true, "Session"));
        }

        private static AuthContext ScopedCaller(Fixture fixture)
        {
            AuthContext caller = AuthContext.Authenticated(fixture.Owner.TenantId!, fixture.Owner.UserId!, true, true, "Session");
            caller.AskThreadId = fixture.Thread.Id;
            caller.AskTurnId = fixture.Turn.RequestId;
            return caller;
        }

        private static Func<JsonElement?, Task<object>> Register(AskActionService service, string toolName, Func<JsonElement?, Task<object>> handler)
        {
            Func<JsonElement?, Task<object>>? registered = null;
            service.Wrap((name, description, schema, callback) =>
            {
                if (String.Equals(name, toolName, StringComparison.Ordinal)) registered = callback;
            })(toolName, "test action", new { type = "object" }, handler);
            return registered ?? throw new InvalidOperationException("The action handler was not registered.");
        }

        private static async Task<object> CallAsync(Func<JsonElement?, Task<object>> handler, AuthContext caller, JsonElement? arguments = null)
        {
            using (McpCallerContext.Begin(caller))
                return await handler(arguments).ConfigureAwait(false);
        }

        private static JsonElement Args(string json)
        {
            using (JsonDocument document = JsonDocument.Parse(json)) return document.RootElement.Clone();
        }

        private sealed class Fixture
        {
            public AskThread Thread { get; }
            public AskTurn Turn { get; }
            public AuthContext Owner { get; }

            public Fixture(AskThread thread, AskTurn turn, AuthContext owner)
            {
                Thread = thread;
                Turn = turn;
                Owner = owner;
            }
        }
    }
}
