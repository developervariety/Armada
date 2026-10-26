namespace Armada.Test.Unit.Suites.Services
{
    using System.Security.Cryptography;
    using Armada.Core;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    public class AuthenticationServiceTests : TestSuite
    {
        public override string Name => "AuthenticationService";

        protected override async Task RunTestsAsync()
        {
            await RunTest("AuthenticateAsync ApiKey ValidKey ReturnsAuthenticated", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    AuthenticationService svc = CreateService(db, apiKey: "testkey");
                    AuthContext ctx = await svc.AuthenticateAsync(null, null, "testkey");

                    AssertTrue(ctx.IsAuthenticated, "Should be authenticated with valid API key");
                    // API-key auth resolves to the DEFAULT tenant, not the system tenant. ten_system is
                    // an internal seeded tenant (AuthRoutes deliberately skips its users when listing);
                    // real records live under "default", so an API-key admin must operate there. This
                    // assertion predates that change and asserted the original multi-tenancy cut.
                    AssertEqual(Constants.DefaultTenantId, ctx.TenantId);
                    AssertEqual(Constants.DefaultUserId, ctx.UserId);
                    AssertTrue(ctx.IsAdmin, "API key auth should grant admin");
                    AssertEqual("ApiKey", ctx.AuthMethod);
                }
            });

            await RunTest("Recognized Ask tokens never fall back to API key and valid MCP Ask scope works", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantMetadata tenant = new TenantMetadata("Ask authentication tenant");
                    await db.Tenants.CreateAsync(tenant);
                    UserMaster user = new UserMaster(tenant.Id, "ask-auth@example.com", "test-password");
                    await db.Users.CreateAsync(user);

                    AskThread thread = new AskThread { TenantId = tenant.Id, UserId = user.Id };
                    await db.AskThreads.CreateAsync(thread);
                    AskTurn turn = new AskTurn
                    {
                        ThreadId = thread.Id,
                        RequestId = "ask-auth-turn",
                        CaptainId = "captain-ask-auth",
                        Content = "hello"
                    };
                    await db.AskThreads.TryReserveTurnAsync(tenant.Id, user.Id, turn);
                    AssertTrue(await db.AskThreads.MarkRunningAsync(tenant.Id, user.Id, thread.Id, turn.RequestId), "saved turn enters Running");

                    string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                    SessionTokenService tokens = new SessionTokenService(key);
                    string askToken = tokens.CreateAskTurnToken(tenant.Id, user.Id, thread.Id, turn.RequestId).Token!;
                    byte[] changedBytes = Convert.FromBase64String(askToken.Substring("ask1.".Length));
                    changedBytes[changedBytes.Length - 1] ^= 0x01;
                    string alteredAsk = "ask1." + Convert.ToBase64String(changedBytes);
                    AuthenticationService svc = CreateService(db, tokens, "testkey");

                    foreach (string badAsk in new[] { alteredAsk, "ask1.not-base64" })
                    {
                        AuthContext badBearer = await svc.AuthenticateAsync("Bearer " + badAsk, null, "testkey");
                        AssertFalse(badBearer.IsAuthenticated, "invalid recognized Ask bearer cannot fall back to API key");
                        AuthContext badSession = await svc.AuthenticateAsync(null, badAsk, "testkey");
                        AssertFalse(badSession.IsAuthenticated, "invalid recognized Ask X-Token cannot fall back to API key");

                        AuthContext badMcpBearer = await svc.AuthenticateMcpAsync("Bearer " + badAsk, null, "testkey");
                        AssertFalse(badMcpBearer.IsAuthenticated, "invalid recognized Ask MCP bearer is refused");
                        AuthContext badMcpSession = await svc.AuthenticateMcpAsync(null, badAsk, "testkey");
                        AssertFalse(badMcpSession.IsAuthenticated, "invalid recognized Ask MCP X-Token is refused");
                    }

                    AuthContext validMcp = await svc.AuthenticateMcpAsync("Bearer " + askToken, null, "testkey");
                    AssertTrue(validMcp.IsAuthenticated, "valid running Ask scope still authenticates MCP");
                    AssertEqual(thread.Id, validMcp.AskThreadId);
                    AssertEqual(turn.RequestId, validMcp.AskTurnId);
                    AuthContext validXTokenMcp = await svc.AuthenticateMcpAsync(null, askToken, "testkey");
                    AssertTrue(validXTokenMcp.IsAuthenticated, "valid Ask X-Token still authenticates MCP");
                }
            });
        }

        #region Private-Helpers

        private AuthenticationService CreateService(
            SqliteDatabaseDriver db,
            ISessionTokenService? sessionTokenService = null,
            string? apiKey = null)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            sessionTokenService ??= new SessionTokenService();

            ArmadaSettings settings = new ArmadaSettings();
            settings.ApiKey = apiKey;

            return new AuthenticationService(db, sessionTokenService, settings, logging);
        }

        #endregion
    }
}
