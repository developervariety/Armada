namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    public class OAuth2ServiceTests : TestSuite
    {
        public override string Name => "OAuth2Service";

        protected override async Task RunTestsAsync()
        {
            // ----------------------------------------------------------------
            // PKCE (RFC 7636)
            // ----------------------------------------------------------------

            await RunTest("Pkce ComputeCodeChallenge matches RFC 7636 test vector", () =>
            {
                // RFC 7636 Appendix B reference verifier/challenge pair.
                string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
                string challenge = PkceHelper.ComputeCodeChallenge(verifier);
                AssertEqual("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", challenge);
            });

            await RunTest("Pkce GenerateCodeVerifier is URL-safe", () =>
            {
                string verifier = PkceHelper.GenerateCodeVerifier();
                AssertTrue(verifier.Length >= 43, "Verifier should be at least 43 chars");
                AssertFalse(verifier.Contains('+') || verifier.Contains('/') || verifier.Contains('='), "Verifier must be base64url");
            });

            // ----------------------------------------------------------------
            // State store (single-use, correlation)
            // ----------------------------------------------------------------

            await RunTest("StateStore Issue then Consume returns the flow once", () =>
            {
                OAuthStateStore store = new OAuthStateStore();
                string state = store.Issue("verifier-123");

                OAuthFlowState? first = store.Consume(state);
                AssertNotNull(first, "First consume should return the flow");
                AssertEqual("verifier-123", first!.CodeVerifier);

                OAuthFlowState? second = store.Consume(state);
                AssertNull(second, "Second consume must return null (single-use)");
            });

            await RunTest("StateStore Consume unknown state returns null", () =>
            {
                OAuthStateStore store = new OAuthStateStore();
                AssertNull(store.Consume("never-issued"), "Unknown state should return null");
                AssertNull(store.Consume(null), "Null state should return null");
            });

            // ----------------------------------------------------------------
            // Settings gating
            // ----------------------------------------------------------------

            await RunTest("Settings IsConfigured false when disabled or incomplete", () =>
            {
                OAuth2Settings cfg = new OAuth2Settings();
                AssertFalse(cfg.IsConfigured(), "Default settings are not configured");

                cfg.Enabled = true;
                AssertFalse(cfg.IsConfigured(), "Enabled but missing endpoints is not configured");
            });

            await RunTest("Settings IsConfigured true when complete", () =>
            {
                OAuth2Settings cfg = BuildConfiguredSettings();
                AssertTrue(cfg.IsConfigured(), "Fully populated settings should be configured");
            });

            // ----------------------------------------------------------------
            // Authorization URL
            // ----------------------------------------------------------------

            await RunTest("BuildAuthorizationUrl includes required OAuth params with PKCE", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    OAuth2Service svc = CreateService(testDb.Driver, BuildConfiguredSettings());
                    string url = svc.BuildAuthorizationUrl("https://armada.example.com/api/v1/auth/oauth/callback");

                    AssertContains("response_type=code", url);
                    AssertContains("client_id=test-client", url);
                    AssertContains("code_challenge=", url);
                    AssertContains("code_challenge_method=S256", url);
                    AssertContains("state=", url);
                    AssertStartsWith("https://idp.example.com/authorize", url);
                }
            });

            await RunTest("BuildAuthorizationUrl omits PKCE when disabled", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    OAuth2Settings cfg = BuildConfiguredSettings();
                    cfg.UsePkce = false;
                    OAuth2Service svc = CreateService(testDb.Driver, cfg);
                    string url = svc.BuildAuthorizationUrl("https://armada.example.com/api/v1/auth/oauth/callback");

                    AssertFalse(url.Contains("code_challenge"), "No PKCE challenge when disabled");
                }
            });

            // ----------------------------------------------------------------
            // User resolution / provisioning
            // ----------------------------------------------------------------

            await RunTest("ResolveOrProvisionUser returns existing user", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantMetadata tenant = new TenantMetadata("Test Tenant");
                    await db.Tenants.CreateAsync(tenant);
                    UserMaster user = new UserMaster(tenant.Id, "existing@example.com", "password");
                    await db.Users.CreateAsync(user);

                    OAuth2Settings cfg = BuildConfiguredSettings();
                    cfg.DefaultTenantId = tenant.Id;
                    OAuth2Service svc = CreateService(db, cfg);

                    UserMaster? resolved = await svc.ResolveOrProvisionUserAsync("existing@example.com", null);
                    AssertNotNull(resolved, "Existing user should resolve");
                    AssertEqual(user.Id, resolved!.Id);
                }
            });

            await RunTest("ResolveOrProvisionUser auto-provisions a new user", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantMetadata tenant = new TenantMetadata("Test Tenant");
                    await db.Tenants.CreateAsync(tenant);

                    OAuth2Settings cfg = BuildConfiguredSettings();
                    cfg.DefaultTenantId = tenant.Id;
                    cfg.AllowAutoProvision = true;
                    OAuth2Service svc = CreateService(db, cfg);

                    UserMaster? resolved = await svc.ResolveOrProvisionUserAsync("new@example.com", "Jane Doe");
                    AssertNotNull(resolved, "New user should be provisioned");
                    AssertEqual("new@example.com", resolved!.Email);
                    AssertEqual("Jane", resolved.FirstName);
                    AssertEqual("Doe", resolved.LastName);
                    AssertFalse(resolved.IsAdmin, "Provisioned user should not be admin");

                    UserMaster? persisted = await db.Users.ReadByEmailAsync(tenant.Id, "new@example.com");
                    AssertNotNull(persisted, "Provisioned user should be persisted");
                }
            });

            await RunTest("ResolveOrProvisionUser rejects unknown user when auto-provision disabled", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantMetadata tenant = new TenantMetadata("Test Tenant");
                    await db.Tenants.CreateAsync(tenant);

                    OAuth2Settings cfg = BuildConfiguredSettings();
                    cfg.DefaultTenantId = tenant.Id;
                    cfg.AllowAutoProvision = false;
                    OAuth2Service svc = CreateService(db, cfg);

                    UserMaster? resolved = await svc.ResolveOrProvisionUserAsync("nobody@example.com", null);
                    AssertNull(resolved, "Unknown user should be rejected when provisioning disabled");
                }
            });

            await RunTest("ResolveOrProvisionUser rejects when tenant missing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    OAuth2Settings cfg = BuildConfiguredSettings();
                    cfg.DefaultTenantId = "ten_does_not_exist";
                    OAuth2Service svc = CreateService(testDb.Driver, cfg);

                    UserMaster? resolved = await svc.ResolveOrProvisionUserAsync("someone@example.com", null);
                    AssertNull(resolved, "Should reject when the configured tenant does not exist");
                }
            });

            // ----------------------------------------------------------------
            // Public config
            // ----------------------------------------------------------------

            await RunTest("GetPublicConfig reflects enabled state and display name", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    OAuth2Settings cfg = BuildConfiguredSettings();
                    cfg.DisplayName = "Authentik";
                    OAuth2Service svc = CreateService(testDb.Driver, cfg);

                    OAuthConfigResult config = svc.GetPublicConfig();
                    AssertTrue(config.Enabled, "Should report enabled");
                    AssertEqual("Authentik", config.DisplayName);
                }
            });
        }

        #region Private-Helpers

        private static OAuth2Settings BuildConfiguredSettings()
        {
            OAuth2Settings cfg = new OAuth2Settings();
            cfg.Enabled = true;
            cfg.AuthorizationEndpoint = "https://idp.example.com/authorize";
            cfg.TokenEndpoint = "https://idp.example.com/token";
            cfg.UserInfoEndpoint = "https://idp.example.com/userinfo";
            cfg.ClientId = "test-client";
            cfg.ClientSecret = "test-secret";
            return cfg;
        }

        private static OAuth2Service CreateService(SqliteDatabaseDriver db, OAuth2Settings oauth)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            ArmadaSettings settings = new ArmadaSettings();
            settings.OAuth2 = oauth;

            SessionTokenService tokenSvc = new SessionTokenService();
            return new OAuth2Service(db, tokenSvc, settings, logging);
        }

        #endregion
    }
}
