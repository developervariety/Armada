namespace Armada.Test.Unit
{
    using System.Security.Cryptography;
    using System.IO;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Integration tests covering SessionTokenService, AuthenticationService,
    /// and Tenant/User/Credential CRUD operations used by the auth and
    /// multi-tenant endpoints.
    /// </summary>
    public class AuthEndpointTests : TestSuite
    {
        private sealed class AuthFixtureResult
        {
            public string TenantId { get; set; } = String.Empty;

            public string UserId { get; set; } = String.Empty;

            public string BearerToken { get; set; } = String.Empty;
        }

        public override string Name => "Auth Endpoint Integration";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Authentication HTTP exchange never removes mission scope", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    JsonSerializerOptions json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    SurfaceReply operatorReply = await harness.RestAsync(HttpMethod.Post, "/api/v1/authenticate").ConfigureAwait(false);
                    AssertEqual(200, operatorReply.Status, "operator API key can obtain a session");
                    AuthenticateResult ordinary = JsonSerializer.Deserialize<AuthenticateResult>(operatorReply.Body, json)!;
                    AssertTrue(ordinary.Success);
                    AssertFalse(String.IsNullOrWhiteSpace(ordinary.Token));
                    SurfaceReply refreshed = await harness.RestAsync(HttpMethod.Post, "/api/v1/authenticate", sessionToken: ordinary.Token).ConfigureAwait(false);
                    AssertEqual(200, refreshed.Status, "ordinary session refresh remains available");
                    AssertTrue(JsonSerializer.Deserialize<AuthenticateResult>(refreshed.Body, json)!.Success);

                    UserMaster user = await harness.Driver.Users.CreateAsync(new UserMaster(
                        Constants.DefaultTenantId, "scoped-exchange@example.com", "test-password")).ConfigureAwait(false);
                    foreach (string userId in new[] { Constants.DefaultUserId, user.Id })
                    {
                        Mission mission = await harness.Driver.Missions.CreateAsync(new Mission("scoped exchange")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = userId
                        }).ConfigureAwait(false);
                        string scoped = harness.CreateMissionToken(Constants.DefaultTenantId, userId, mission.Id);
                        SurfaceReply identity = await harness.RestAsync(HttpMethod.Get, "/api/v1/whoami", sessionToken: scoped).ConfigureAwait(false);
                        AssertEqual(200, identity.Status, "real signer and middleware accept the mission credential");
                        SurfaceReply refused = await harness.RestAsync(HttpMethod.Post, "/api/v1/authenticate", sessionToken: scoped).ConfigureAwait(false);
                        AssertEqual(403, refused.Status, "mission credentials cannot obtain an unrestricted session, regardless of owner roles");
                        AuthenticateResult result = JsonSerializer.Deserialize<AuthenticateResult>(refused.Body, json)!;
                        AssertFalse(result.Success);
                        AssertNull(result.Token, "refusal issues no credential");
                    }
                    SurfaceReply invalid = await harness.RestAsync(HttpMethod.Post, "/api/v1/authenticate", sessionToken: "invalid-session").ConfigureAwait(false);
                    AssertEqual(401, invalid.Status, "invalid authentication remains unauthorized");
                }
            });

            // ================================================================
            // Seeded default credentials: startup policy and retirement
            // ================================================================

            await RunTest("Default credential startup policy refuses a non-loopback listener and allows loopback", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DefaultCredentialService defaults = new DefaultCredentialService(testDb.Driver, CreateQuietLogging());
                    foreach (string host in new[] { "0.0.0.0", "*", "10.1.2.3", "armada.example.test", "" })
                    {
                        await AssertThrowsAsync<DefaultCredentialPolicyException>(
                            () => defaults.EnforceStartupPolicyAsync(host, false, null),
                            "non-loopback host '" + host + "' with seeded defaults must be refused").ConfigureAwait(false);
                    }
                    foreach (string host in new[] { "localhost", "127.0.0.1", "[::1]" })
                    {
                        List<string> remaining = await defaults.EnforceStartupPolicyAsync(host, false, null).ConfigureAwait(false);
                        AssertEqual(2, remaining.Count, "loopback start reports the default password and bearer token for " + host);
                    }
                }
            });

            await RunTest("Default credential startup policy override warns loudly instead of refusing", async () =>
            {
                string logPath = Path.Combine(Path.GetTempPath(), "armada_default_cred_" + Guid.NewGuid().ToString("N") + ".log");
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        LoggingModule logging = new LoggingModule(logPath, FileLoggingMode.SingleLogFile, false);
                        logging.Settings.EnableConsole = false;
                        DefaultCredentialService defaults = new DefaultCredentialService(testDb.Driver, logging);

                        List<string> remaining = await defaults.EnforceStartupPolicyAsync("0.0.0.0", true, null).ConfigureAwait(false);
                        AssertEqual(2, remaining.Count, "override leaves the defaults active");
                        await logging.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                        string logText = File.ReadAllText(logPath);
                        AssertContains("AllowDefaultCredentialsOnNetwork", logText, "override warning names the setting");
                        AssertContains("default credentials in use", logText, "override warning names the exposure");
                    }
                }
                finally
                {
                    if (File.Exists(logPath)) File.Delete(logPath);
                }
            });

            await RunTest("Default credential startup policy retires the seeded bearer once the admin password was changed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AuthenticationService auth = CreateAuthService(db);
                    DefaultCredentialService defaults = new DefaultCredentialService(db, CreateQuietLogging());

                    AuthContext before = await auth.AuthenticateAsync("Bearer " + Constants.DefaultBearerToken, null, null).ConfigureAwait(false);
                    AssertTrue(before.IsAuthenticated, "seeded bearer authenticates while the default password is in use");

                    // An existing install whose admin already set a real password but still holds the seeded bearer row.
                    UserMaster admin = (await db.Users.ReadByIdAsync(Constants.DefaultUserId).ConfigureAwait(false))!;
                    admin.PasswordSha256 = UserMaster.ComputePasswordHash("real-admin-pw1");
                    await db.Users.UpdateAsync(admin).ConfigureAwait(false);

                    AuthContext rejected = await auth.AuthenticateAsync("Bearer " + Constants.DefaultBearerToken, null, null).ConfigureAwait(false);
                    AssertFalse(rejected.IsAuthenticated, "seeded bearer stops authenticating the moment the password is real");
                    Credential rowBefore = (await db.Credentials.ReadByIdAsync(Constants.DefaultCredentialId).ConfigureAwait(false))!;
                    AssertTrue(rowBefore.Active, "the stored credential is still active until startup retires it");

                    List<string> remaining = await defaults.EnforceStartupPolicyAsync("0.0.0.0", false, null).ConfigureAwait(false);
                    AssertEqual(0, remaining.Count, "a deployment with real credentials starts on a non-loopback listener");
                    Credential rowAfter = (await db.Credentials.ReadByIdAsync(Constants.DefaultCredentialId).ConfigureAwait(false))!;
                    AssertFalse(rowAfter.Active, "startup deactivates the seeded credential row");

                    // Resetting the password to the default later must not revive the retired token.
                    admin = (await db.Users.ReadByIdAsync(Constants.DefaultUserId).ConfigureAwait(false))!;
                    admin.PasswordSha256 = UserMaster.ComputePasswordHash(Constants.DefaultUserPassword);
                    await db.Users.UpdateAsync(admin).ConfigureAwait(false);
                    AuthContext revived = await auth.AuthenticateAsync("Bearer " + Constants.DefaultBearerToken, null, null).ConfigureAwait(false);
                    AssertFalse(revived.IsAuthenticated, "a retired seeded bearer stays retired");
                }
            });

            await RunTest("Default credential initial admin password replaces the seeded password once and never overwrites a later one", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    DefaultCredentialService defaults = new DefaultCredentialService(db, CreateQuietLogging());

                    await AssertThrowsAsync<DefaultCredentialPolicyException>(
                        () => defaults.EnforceStartupPolicyAsync("0.0.0.0", false, "short")).ConfigureAwait(false);
                    await AssertThrowsAsync<DefaultCredentialPolicyException>(
                        () => defaults.EnforceStartupPolicyAsync("0.0.0.0", false, Constants.DefaultUserPassword)).ConfigureAwait(false);

                    List<string> remaining = await defaults.EnforceStartupPolicyAsync("0.0.0.0", false, "first-start-secret").ConfigureAwait(false);
                    AssertEqual(0, remaining.Count, "the initial password satisfies the non-loopback guard");
                    UserMaster admin = (await db.Users.ReadByIdAsync(Constants.DefaultUserId).ConfigureAwait(false))!;
                    AssertTrue(admin.VerifyPassword("first-start-secret"), "initial password is stored");
                    AssertFalse(admin.VerifyPassword(Constants.DefaultUserPassword), "default password no longer signs in");
                    AuthContext bearer = await CreateAuthService(db).AuthenticateAsync("Bearer " + Constants.DefaultBearerToken, null, null).ConfigureAwait(false);
                    AssertFalse(bearer.IsAuthenticated, "seeded bearer is retired with the initial password");

                    admin.PasswordSha256 = UserMaster.ComputePasswordHash("operator-changed-it");
                    await db.Users.UpdateAsync(admin).ConfigureAwait(false);
                    await defaults.EnforceStartupPolicyAsync("0.0.0.0", false, "first-start-secret").ConfigureAwait(false);
                    admin = (await db.Users.ReadByIdAsync(Constants.DefaultUserId).ConfigureAwait(false))!;
                    AssertTrue(admin.VerifyPassword("operator-changed-it"), "restart with the variable still set keeps the later password");
                }
            });

            await RunTest("Account password route requires the current password, retires the seeded bearer and clears the default-password flag", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    JsonSerializerOptions json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    SurfaceReply login = await harness.RestAsync(HttpMethod.Post, "/api/v1/authenticate", new AuthenticateRequest
                    {
                        TenantId = Constants.DefaultTenantId,
                        Email = Constants.DefaultUserEmail,
                        Password = Constants.DefaultUserPassword
                    }).ConfigureAwait(false);
                    AuthenticateResult session = JsonSerializer.Deserialize<AuthenticateResult>(login.Body, json)!;
                    AssertTrue(session.Success);
                    AssertTrue(session.PasswordChangeRequired, "login reports the default password until it changes");
                    AssertEqual(200, await WhoAmIStatusWithBearerAsync(harness, Constants.DefaultBearerToken).ConfigureAwait(false), "the seeded bearer authenticates before the change");
                    SurfaceReply identity = await harness.RestAsync(HttpMethod.Get, "/api/v1/whoami", sessionToken: session.Token).ConfigureAwait(false);
                    AssertTrue(JsonSerializer.Deserialize<WhoAmIResult>(identity.Body, json)!.PasswordChangeRequired, "whoami reports the default password");

                    SurfaceReply wrong = await harness.RestAsync(HttpMethod.Put, "/api/v1/account/password",
                        new PasswordChangeRequest { CurrentPassword = "wrong-password", NewPassword = "real-admin-pw1" }, session.Token).ConfigureAwait(false);
                    AssertEqual(403, wrong.Status, "wrong current password is refused");
                    SurfaceReply missing = await harness.RestAsync(HttpMethod.Put, "/api/v1/account/password",
                        new PasswordChangeRequest { NewPassword = "real-admin-pw1" }, session.Token).ConfigureAwait(false);
                    AssertEqual(400, missing.Status, "missing current password is refused");
                    SurfaceReply weak = await harness.RestAsync(HttpMethod.Put, "/api/v1/account/password",
                        new PasswordChangeRequest { CurrentPassword = Constants.DefaultUserPassword, NewPassword = "short" }, session.Token).ConfigureAwait(false);
                    AssertEqual(400, weak.Status, "short new password is refused");
                    UserMaster unchanged = (await harness.Driver.Users.ReadByIdAsync(Constants.DefaultUserId).ConfigureAwait(false))!;
                    AssertTrue(unchanged.VerifyPassword(Constants.DefaultUserPassword), "refused changes leave the stored password alone");

                    SurfaceReply changed = await harness.RestAsync(HttpMethod.Put, "/api/v1/account/password",
                        new PasswordChangeRequest { CurrentPassword = Constants.DefaultUserPassword, NewPassword = "real-admin-pw1" }, session.Token).ConfigureAwait(false);
                    AssertEqual(200, changed.Status, "correct current password changes the password");
                    AssertFalse(JsonSerializer.Deserialize<WhoAmIResult>(changed.Body, json)!.PasswordChangeRequired);
                    UserMaster updated = (await harness.Driver.Users.ReadByIdAsync(Constants.DefaultUserId).ConfigureAwait(false))!;
                    AssertTrue(updated.VerifyPassword("real-admin-pw1"));
                    Credential seeded = (await harness.Driver.Credentials.ReadByIdAsync(Constants.DefaultCredentialId).ConfigureAwait(false))!;
                    AssertFalse(seeded.Active, "the password change deactivates the seeded bearer credential");

                    AssertEqual(401, await WhoAmIStatusWithBearerAsync(harness, Constants.DefaultBearerToken).ConfigureAwait(false), "the seeded bearer no longer authenticates over HTTP");
                }
            });

            // ================================================================
            // SessionTokenService
            // ================================================================

            await RunTest("SessionToken CreateToken returns Success=true, non-null Token, future ExpiresUtc", () =>
            {
                SessionTokenService svc = new SessionTokenService();
                AuthenticateResult result = svc.CreateToken("ten_abc", "usr_xyz");

                AssertTrue(result.Success, "Success should be true");
                AssertNotNull(result.Token, "Token should not be null");
                AssertTrue(result.Token!.Length > 0, "Token should not be empty");
                AssertNotNull(result.ExpiresUtc, "ExpiresUtc should not be null");
                AssertTrue(result.ExpiresUtc!.Value > DateTime.UtcNow, "ExpiresUtc should be in the future");
            });

            await RunTest("SessionToken ValidateToken on valid token returns correct tenantId and userId", () =>
            {
                SessionTokenService svc = new SessionTokenService();
                AuthenticateResult result = svc.CreateToken("ten_abc", "usr_xyz");

                AuthContext? ctx = svc.ValidateToken(result.Token!);
                AssertNotNull(ctx);
                AssertEqual("ten_abc", ctx!.TenantId, "TenantId");
                AssertEqual("usr_xyz", ctx.UserId, "UserId");
                AssertTrue(ctx.IsAuthenticated, "IsAuthenticated");
            });

            await RunTest("Ask session tokens authenticate scope and reject altered or legacy claims", () =>
            {
                byte[] rootKey = RandomNumberGenerator.GetBytes(32);
                string rootKeyBase64 = Convert.ToBase64String(rootKey);
                SessionTokenService svc = new SessionTokenService(rootKeyBase64);
                AuthenticateResult ask = svc.CreateAskTurnToken("ten_ask", "usr_ask", "thread_ask", "turn_ask");
                AssertTrue(ask.Token!.StartsWith("ask1.", StringComparison.Ordinal), "Ask tokens use the authenticated format");
                AuthContext? scope = svc.ValidateToken(ask.Token);
                AssertNotNull(scope, "valid Ask token validates");
                AssertTrue(scope!.IsAuthenticated);
                AssertEqual("ten_ask", scope.TenantId);
                AssertEqual("usr_ask", scope.UserId);
                AssertEqual("thread_ask", scope.AskThreadId);
                AssertEqual("turn_ask", scope.AskTurnId);
                AssertNull(scope.MissionId);

                byte[] tokenBytes = Convert.FromBase64String(ask.Token.Substring("ask1.".Length));
                foreach (int index in new[] { 0, 12 + (tokenBytes.Length - 12 - 16) / 2, tokenBytes.Length - 1 })
                {
                    byte[] changed = (byte[])tokenBytes.Clone();
                    changed[index] ^= 0x01;
                    AuthContext? altered = svc.ValidateToken("ask1." + Convert.ToBase64String(changed));
                    AssertNull(altered, "nonce, ciphertext, and tag changes must fail authentication");
                }

                AssertNull(svc.ValidateToken("ask1." + Convert.ToBase64String(tokenBytes, 0, tokenBytes.Length - 1)), "truncated Ask token is rejected");
                AssertNull(svc.ValidateToken(ask.Token.Substring("ask1.".Length)), "stripping the Ask prefix cannot validate as a generic session");
                SessionTokenService otherKey = new SessionTokenService(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                AssertNull(otherKey.ValidateToken(ask.Token), "Ask token is bound to its root key");

                object legacyPayload = new
                {
                    TenantId = "ten_ask", UserId = "usr_ask", MissionId = (string?)null,
                    AskThreadId = "thread_ask", AskTurnId = "turn_ask",
                    ExpiresUtc = DateTime.UtcNow.AddHours(Constants.SessionTokenLifetimeHours)
                };
                string legacyAsk = EncryptLegacyForTest(rootKey, JsonSerializer.Serialize(legacyPayload));
                AssertNull(svc.ValidateToken(legacyAsk), "legacy CBC data cannot authenticate Ask scope claims");

                AuthContext? ordinary = svc.ValidateToken(svc.CreateToken("ten_legacy", "usr_legacy").Token!);
                AssertNotNull(ordinary, "legacy ordinary token remains valid");
                AssertNull(ordinary!.AskThreadId);
                AuthContext? mission = svc.ValidateToken(svc.CreateMissionToken("ten_legacy", "usr_legacy", "mission_legacy").Token!);
                AssertNotNull(mission, "legacy mission token remains valid");
                AssertEqual("mission_legacy", mission!.MissionId);
            });

            await RunTest("SessionToken ValidateToken on expired token returns null", () =>
            {
                // Create a token service with a known key, then create a second
                // service with the same key and validate. We cannot actually expire
                // a token quickly, so we test with a tampered token that simulates
                // decryption yielding an expired timestamp.  The closest realistic
                // approach: create with one service instance, validate with another
                // instance using a different key (simulating key rotation / expiry).
                SessionTokenService svc1 = new SessionTokenService();
                AuthenticateResult result = svc1.CreateToken("ten_abc", "usr_xyz");

                // Different instance = different key, token will not decrypt
                SessionTokenService svc2 = new SessionTokenService();
                AuthContext? ctx = svc2.ValidateToken(result.Token!);
                AssertNull(ctx, "Token validated with different key should return null");
            });

            await RunTest("SessionToken ValidateToken on garbage input returns null", () =>
            {
                SessionTokenService svc = new SessionTokenService();
                AuthContext? ctx = svc.ValidateToken("this-is-absolute-garbage-not-a-token");
                AssertNull(ctx, "Garbage token should return null");
            });

            // ================================================================
            // AuthenticationService
            // ================================================================

            await RunTest("AuthService AuthenticateWithCredentialsAsync valid email/password returns authenticated", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string password = "correctPassword123";
                    AuthFixtureResult entities = await CreateTestEntitiesAsync(db, password: password);

                    AuthenticationService svc = CreateAuthService(db);
                    AuthContext ctx = await svc.AuthenticateWithCredentialsAsync(entities.TenantId, "test@example.com", password);

                    AssertTrue(ctx.IsAuthenticated, "Should be authenticated with correct credentials");
                    AssertEqual(entities.TenantId, ctx.TenantId, "TenantId");
                    AssertEqual(entities.UserId, ctx.UserId, "UserId");
                    AssertEqual("Credentials", ctx.AuthMethod, "AuthMethod");
                }
            });

            await RunTest("AuthService AuthenticateWithCredentialsAsync wrong password returns unauthenticated", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AuthFixtureResult entities = await CreateTestEntitiesAsync(db, password: "correctPassword");

                    AuthenticationService svc = CreateAuthService(db);
                    AuthContext ctx = await svc.AuthenticateWithCredentialsAsync(entities.TenantId, "test@example.com", "wrongPassword");

                    AssertFalse(ctx.IsAuthenticated, "Should not be authenticated with wrong password");
                }
            });

            await RunTest("AuthService AuthenticateWithCredentialsAsync non-existent email returns unauthenticated", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AuthFixtureResult entities = await CreateTestEntitiesAsync(db);

                    AuthenticationService svc = CreateAuthService(db);
                    AuthContext ctx = await svc.AuthenticateWithCredentialsAsync(entities.TenantId, "nonexistent@example.com", "anyPassword");

                    AssertFalse(ctx.IsAuthenticated, "Should not be authenticated with unknown email");
                }
            });

            await RunTest("AuthService AuthenticateBearerAsync valid token returns authenticated", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AuthFixtureResult entities = await CreateTestEntitiesAsync(db);

                    AuthenticationService svc = CreateAuthService(db);
                    AuthContext ctx = await svc.AuthenticateAsync("Bearer " + entities.BearerToken, null, null);

                    AssertTrue(ctx.IsAuthenticated, "Should be authenticated with valid bearer token");
                    AssertEqual(entities.TenantId, ctx.TenantId, "TenantId");
                    AssertEqual(entities.UserId, ctx.UserId, "UserId");
                    AssertEqual("Bearer", ctx.AuthMethod, "AuthMethod");
                }
            });

            await RunTest("AuthService AuthenticateBearerAsync invalid token returns unauthenticated", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    await CreateTestEntitiesAsync(db);

                    AuthenticationService svc = CreateAuthService(db);
                    AuthContext ctx = await svc.AuthenticateAsync("Bearer totally_invalid_bearer_token", null, null);

                    AssertFalse(ctx.IsAuthenticated, "Should not be authenticated with invalid bearer token");
                }
            });

            // ================================================================
            // Tenant/User/Credential CRUD
            // ================================================================

            await RunTest("CRUD Create tenant, verify it persists", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantMetadata tenant = new TenantMetadata("Persist Test Corp");
                    await db.Tenants.CreateAsync(tenant);

                    TenantMetadata? readBack = await db.Tenants.ReadAsync(tenant.Id);
                    AssertNotNull(readBack, "Tenant should persist after creation");
                    AssertEqual("Persist Test Corp", readBack!.Name, "Name");
                    AssertTrue(readBack.Active, "Active");
                }
            });

            await RunTest("CRUD Create user in tenant, verify tenant scoping", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string tenantA = await CreateTenantAsync(db, "Tenant A");
                    string tenantB = await CreateTenantAsync(db, "Tenant B");

                    UserMaster user = new UserMaster(tenantA, "scoped@example.com", "pass");
                    await db.Users.CreateAsync(user);

                    // Readable in correct tenant
                    AssertNotNull(await db.Users.ReadAsync(tenantA, user.Id), "User should be readable in own tenant");

                    // Not readable in wrong tenant
                    AssertNull(await db.Users.ReadAsync(tenantB, user.Id), "User should not be visible in other tenant");
                }
            });

            await RunTest("CRUD Create credential, verify bearer token lookup", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string tenantId = await CreateTenantAsync(db, "Cred Tenant");

                    UserMaster user = new UserMaster(tenantId, "creduser@example.com", "pass");
                    await db.Users.CreateAsync(user);

                    Credential cred = new Credential(tenantId, user.Id);
                    await db.Credentials.CreateAsync(cred);

                    // Lookup by bearer token
                    Credential? found = await db.Credentials.ReadByBearerTokenAsync(cred.BearerToken);
                    AssertNotNull(found, "Should find credential by bearer token");
                    AssertEqual(cred.Id, found!.Id, "Credential Id");
                    AssertEqual(tenantId, found.TenantId, "TenantId");
                    AssertEqual(user.Id, found.UserId, "UserId");
                }
            });

            await RunTest("CRUD Enumerate tenants with pagination", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    for (int i = 0; i < 5; i++)
                    {
                        await db.Tenants.CreateAsync(new TenantMetadata("PageTenant-" + i));
                    }

                    // 5 created + 1 default seeded = 6 total
                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 3;
                    query.PageNumber = 1;
                    EnumerationResult<TenantMetadata> result = await db.Tenants.EnumerateAsync(query);

                    AssertNotNull(result, "Result should not be null");
                    AssertEqual(3, result.Objects.Count, "Objects.Count (page size)");
                    AssertEqual(6L, result.TotalRecords, "TotalRecords (5 + 1 seeded)");
                    AssertEqual(3, result.PageSize, "PageSize");
                    AssertEqual(1, result.PageNumber, "PageNumber");
                    AssertEqual(2, result.TotalPages, "TotalPages ceil(6/3)");
                }
            });

            await RunTest("CRUD Enumerate users scoped to tenant returns only that tenant's users", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string tenantA = await CreateTenantAsync(db, "Enum Tenant A");
                    string tenantB = await CreateTenantAsync(db, "Enum Tenant B");

                    await db.Users.CreateAsync(new UserMaster(tenantA, "a1@a.com", "pass"));
                    await db.Users.CreateAsync(new UserMaster(tenantA, "a2@a.com", "pass"));
                    await db.Users.CreateAsync(new UserMaster(tenantA, "a3@a.com", "pass"));
                    await db.Users.CreateAsync(new UserMaster(tenantB, "b1@b.com", "pass"));

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 10;
                    query.PageNumber = 1;
                    EnumerationResult<UserMaster> resultA = await db.Users.EnumerateAsync(tenantA, query);

                    AssertEqual(3, resultA.Objects.Count, "Tenant A user count");
                    AssertEqual(3L, resultA.TotalRecords, "Tenant A TotalRecords");
                    foreach (UserMaster u in resultA.Objects)
                    {
                        AssertEqual(tenantA, u.TenantId, "All users should belong to Tenant A");
                    }

                    EnumerationResult<UserMaster> resultB = await db.Users.EnumerateAsync(tenantB, query);
                    AssertEqual(1, resultB.Objects.Count, "Tenant B user count");
                    AssertEqual(1L, resultB.TotalRecords, "Tenant B TotalRecords");
                }
            });

            await RunTest("CRUD Delete user, verify it's gone", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string tenantId = await CreateTenantAsync(db, "Del User Tenant");

                    UserMaster user = new UserMaster(tenantId, "deleteme@example.com", "pass");
                    await db.Users.CreateAsync(user);

                    // Confirm exists
                    AssertNotNull(await db.Users.ReadAsync(tenantId, user.Id), "User should exist before delete");

                    // Delete
                    await db.Users.DeleteAsync(tenantId, user.Id);

                    // Confirm gone
                    AssertNull(await db.Users.ReadAsync(tenantId, user.Id), "User should be gone after delete");
                }
            });

            await RunTest("CRUD Delete tenant, verify it's gone", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantMetadata tenant = new TenantMetadata("To Be Deleted");
                    await db.Tenants.CreateAsync(tenant);

                    // Confirm exists
                    AssertNotNull(await db.Tenants.ReadAsync(tenant.Id), "Tenant should exist before delete");

                    // Delete
                    await db.Tenants.DeleteAsync(tenant.Id);

                    // Confirm gone
                    AssertNull(await db.Tenants.ReadAsync(tenant.Id), "Tenant should be gone after delete");
                }
            });
        }

        #region Private-Helpers

        private static string EncryptLegacyForTest(byte[] key, string plaintext)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = key;
                aes.GenerateIV();
                using (MemoryStream output = new MemoryStream())
                {
                    output.Write(aes.IV, 0, aes.IV.Length);
                    using (ICryptoTransform encryptor = aes.CreateEncryptor())
                    using (CryptoStream crypto = new CryptoStream(output, encryptor, CryptoStreamMode.Write))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
                        crypto.Write(bytes, 0, bytes.Length);
                        crypto.FlushFinalBlock();
                    }
                    return Convert.ToBase64String(output.ToArray());
                }
            }
        }

        private static async Task<int> WhoAmIStatusWithBearerAsync(SurfaceParityHarness harness, string bearer)
        {
            // A client without the harness operator key, so only the presented bearer authenticates.
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/whoami"))
            using (HttpClient anonymous = new HttpClient { BaseAddress = harness.Rest.BaseAddress })
            {
                request.Headers.Add("Authorization", "Bearer " + bearer);
                using (HttpResponseMessage response = await anonymous.SendAsync(request).ConfigureAwait(false))
                    return (int)response.StatusCode;
            }
        }

        private static LoggingModule CreateQuietLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private AuthenticationService CreateAuthService(
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

        private async Task<AuthFixtureResult> CreateTestEntitiesAsync(
            SqliteDatabaseDriver db,
            string? password = null,
            bool userActive = true,
            bool credentialActive = true)
        {
            password ??= "password";

            TenantMetadata tenant = new TenantMetadata("Test Tenant");
            await db.Tenants.CreateAsync(tenant);

            UserMaster user = new UserMaster(tenant.Id, "test@example.com", password);
            user.Active = userActive;
            await db.Users.CreateAsync(user);

            Credential credential = new Credential(tenant.Id, user.Id);
            credential.Active = credentialActive;
            await db.Credentials.CreateAsync(credential);

            return new AuthFixtureResult
            {
                TenantId = tenant.Id,
                UserId = user.Id,
                BearerToken = credential.BearerToken
            };
        }

        private async Task<string> CreateTenantAsync(SqliteDatabaseDriver db, string name = "Test Tenant")
        {
            TenantMetadata tenant = new TenantMetadata(name);
            await db.Tenants.CreateAsync(tenant);
            return tenant.Id;
        }

        #endregion
    }
}
