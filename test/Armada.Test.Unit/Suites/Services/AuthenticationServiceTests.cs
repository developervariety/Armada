namespace Armada.Test.Unit.Suites.Services
{
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
