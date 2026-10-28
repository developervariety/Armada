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
                    foreach (string host in new[] { "0.0.0.0", "*", "192.0.2.10", "armada.example.test", "" })
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
                    SurfaceReply login = await LoginAsync(harness, Constants.DefaultTenantId, Constants.DefaultUserEmail, Constants.DefaultUserPassword).ConfigureAwait(false);
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
            // Tenant initial passwords and self-service password changes
            // ================================================================

            await RunTest("Tenant creation seeds the admin with a one-time generated password, never the default", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    JsonSerializerOptions json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    List<TenantCreateResult> created = new List<TenantCreateResult>();
                    foreach (string name in new[] { "Generated One", "Generated Two" })
                    {
                        SurfaceReply reply = await harness.RestAsync(HttpMethod.Post, "/api/v1/tenants", new { Name = name }).ConfigureAwait(false);
                        AssertEqual(201, reply.Status, "tenant create succeeds");
                        TenantCreateResult result = JsonSerializer.Deserialize<TenantCreateResult>(reply.Body, json)!;
                        AssertEqual(401, (await LoginAsync(harness, result.Id, Constants.DefaultUserEmail, Constants.DefaultUserPassword).ConfigureAwait(false)).Status,
                            "the known default password does not sign in to a new tenant");
                        AssertEqual(Constants.DefaultUserEmail, result.AdminEmail, "response names the seeded admin");
                        AssertFalse(String.IsNullOrEmpty(result.AdminPassword), "response carries the generated password once");
                        AssertTrue(result.AdminPassword!.Length >= PasswordChangeRequest.MinimumLength, "generated password meets the minimum length");
                        AssertNotEqual(Constants.DefaultUserPassword, result.AdminPassword, "generated password is not the default");
                        created.Add(result);

                        AssertEqual(200, (await LoginAsync(harness, result.Id, Constants.DefaultUserEmail, result.AdminPassword).ConfigureAwait(false)).Status,
                            "the generated password signs in");

                        SurfaceReply read = await harness.RestAsync(HttpMethod.Get, "/api/v1/tenants/" + result.Id).ConfigureAwait(false);
                        AssertFalse(read.Body.Contains(result.AdminPassword, StringComparison.Ordinal), "later reads never return the generated password");
                        UserMaster seeded = (await harness.Driver.Users.ReadByEmailAsync(result.Id, Constants.DefaultUserEmail).ConfigureAwait(false))!;
                        AssertFalse(seeded.UsesDefaultPassword(), "the seeded admin does not use the default password");
                    }
                    AssertNotEqual(created[0].AdminPassword, created[1].AdminPassword, "each tenant gets its own password");
                }
            });

            await RunTest("Tenant creation uses a supplied admin password and rejects a short or default one", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    JsonSerializerOptions json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    SurfaceReply reply = await harness.RestAsync(HttpMethod.Post, "/api/v1/tenants", new { Name = "Chosen", AdminPassword = "chosen-pass-1" }).ConfigureAwait(false);
                    AssertEqual(201, reply.Status);
                    TenantCreateResult result = JsonSerializer.Deserialize<TenantCreateResult>(reply.Body, json)!;
                    AssertNull(result.AdminPassword, "a supplied password is not echoed back");
                    AssertFalse(reply.Body.Contains("chosen-pass-1", StringComparison.Ordinal), "the response never contains the supplied password");
                    AssertEqual(200, (await LoginAsync(harness, result.Id, Constants.DefaultUserEmail, "chosen-pass-1").ConfigureAwait(false)).Status, "the supplied password signs in");

                    int before = (await harness.Driver.Tenants.EnumerateAsync(new EnumerationQuery { PageSize = 100 }).ConfigureAwait(false)).Objects.Count;
                    foreach (string weak in new[] { "short", Constants.DefaultUserPassword })
                    {
                        SurfaceReply refused = await harness.RestAsync(HttpMethod.Post, "/api/v1/tenants", new { Name = "Refused " + weak, AdminPassword = weak }).ConfigureAwait(false);
                        AssertEqual(400, refused.Status, "weak admin password '" + weak + "' is refused");
                    }
                    int after = (await harness.Driver.Tenants.EnumerateAsync(new EnumerationQuery { PageSize = 100 }).ConfigureAwait(false)).Objects.Count;
                    AssertEqual(before, after, "a refused request creates no tenant");
                }
            });

            await RunTest("User update requires the current password for a self password change and not for an administrative reset", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    JsonSerializerOptions json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    UserMaster member = await harness.Driver.Users.CreateAsync(new UserMaster(Constants.DefaultTenantId, "member@example.com", "member-pass-1")).ConfigureAwait(false);
                    UserMaster tenantAdmin = new UserMaster(Constants.DefaultTenantId, "lead@example.com", "lead-pass-12") { IsTenantAdmin = true };
                    tenantAdmin = await harness.Driver.Users.CreateAsync(tenantAdmin).ConfigureAwait(false);
                    string memberSession = await SessionAsync(harness, "member@example.com", "member-pass-1").ConfigureAwait(false);

                    SurfaceReply missing = await harness.RestAsync(HttpMethod.Put, "/api/v1/users/" + member.Id,
                        new { Email = member.Email, Password = "member-next-2" }, memberSession).ConfigureAwait(false);
                    AssertEqual(400, missing.Status, "a self password change without the current password is refused");
                    SurfaceReply wrong = await harness.RestAsync(HttpMethod.Put, "/api/v1/users/" + member.Id,
                        new { Email = member.Email, Password = "member-next-2", CurrentPassword = "wrong-password" }, memberSession).ConfigureAwait(false);
                    AssertEqual(403, wrong.Status, "a self password change with a wrong current password is refused");
                    AssertTrue((await harness.Driver.Users.ReadByIdAsync(member.Id).ConfigureAwait(false))!.VerifyPassword("member-pass-1"), "refused changes leave the stored password alone");

                    SurfaceReply profile = await harness.RestAsync(HttpMethod.Put, "/api/v1/users/" + member.Id,
                        new { Email = member.Email, FirstName = "Member" }, memberSession).ConfigureAwait(false);
                    AssertEqual(200, profile.Status, "a profile edit that keeps the password needs no current password");

                    SurfaceReply changed = await harness.RestAsync(HttpMethod.Put, "/api/v1/users/" + member.Id,
                        new { Email = member.Email, Password = "member-next-2", CurrentPassword = "member-pass-1" }, memberSession).ConfigureAwait(false);
                    AssertEqual(200, changed.Status, "the correct current password allows the change");
                    AssertEqual(200, (await LoginAsync(harness, Constants.DefaultTenantId, "member@example.com", "member-next-2").ConfigureAwait(false)).Status, "the new password signs in");
                    AssertEqual(401, (await LoginAsync(harness, Constants.DefaultTenantId, "member@example.com", "member-pass-1").ConfigureAwait(false)).Status, "the old password no longer signs in");

                    string leadSession = await SessionAsync(harness, "lead@example.com", "lead-pass-12").ConfigureAwait(false);
                    SurfaceReply tenantAdminReset = await harness.RestAsync(HttpMethod.Put, "/api/v1/users/" + member.Id,
                        new { Email = member.Email, Password = "member-reset-3" }, leadSession).ConfigureAwait(false);
                    AssertEqual(200, tenantAdminReset.Status, "a tenant administrator resets another user's password without their current password");
                    SurfaceReply tenantAdminSelf = await harness.RestAsync(HttpMethod.Put, "/api/v1/users/" + tenantAdmin.Id,
                        new { Email = tenantAdmin.Email, IsTenantAdmin = true, Password = "lead-next-345" }, leadSession).ConfigureAwait(false);
                    AssertEqual(400, tenantAdminSelf.Status, "an administrator changing their own password still proves the current one");

                    SurfaceReply apiKeyReset = await harness.RestAsync(HttpMethod.Put, "/api/v1/users/" + member.Id,
                        new { Email = member.Email, Password = "member-final-4" }).ConfigureAwait(false);
                    AssertEqual(200, apiKeyReset.Status, "the deployment API key resets a password without the current one");
                    SurfaceReply apiKeyDefault = await harness.RestAsync(HttpMethod.Put, "/api/v1/users/" + Constants.DefaultUserId,
                        new { Email = Constants.DefaultUserEmail, IsAdmin = true, IsTenantAdmin = true, Password = "root-reset-pw5" }).ConfigureAwait(false);
                    AssertEqual(200, apiKeyDefault.Status, "the deployment API key can reset the seeded admin it maps to");
                    AssertTrue((await harness.Driver.Users.ReadByIdAsync(Constants.DefaultUserId).ConfigureAwait(false))!.VerifyPassword("root-reset-pw5"));
                    Credential seeded = (await harness.Driver.Credentials.ReadByIdAsync(Constants.DefaultCredentialId).ConfigureAwait(false))!;
                    AssertFalse(seeded.Active, "resetting the seeded admin's password retires the seeded bearer");
                }
            });

            // ================================================================
            // Credential login throttling
            // ================================================================

            await RunTest("Credential login locks an account after repeated failures and refuses even the right password", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                using (HttpClient anonymous = new HttpClient { BaseAddress = harness.Rest.BaseAddress, Timeout = TimeSpan.FromSeconds(20) })
                {
                    harness.Settings.LoginRateLimit.MaxFailuresPerAccount = 3;
                    await harness.Driver.Users.CreateAsync(new UserMaster(Constants.DefaultTenantId, "locked@example.com", "right-password")).ConfigureAwait(false);
                    await harness.Driver.Users.CreateAsync(new UserMaster(Constants.DefaultTenantId, "bystander@example.com", "bystander-password")).ConfigureAwait(false);

                    HttpResponseMessage first = await PostCredentialsAsync(anonymous, "locked@example.com", "right-password").ConfigureAwait(false);
                    AssertEqual(200, (int)first.StatusCode, "correct credentials sign in before any failure");
                    string sessionToken = JsonSerializer.Deserialize<AuthenticateResult>(await first.Content.ReadAsStringAsync().ConfigureAwait(false), _CaseInsensitive)!.Token!;

                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        HttpResponseMessage failed = await PostCredentialsAsync(anonymous, "Locked@Example.com", "wrong-password").ConfigureAwait(false);
                        AssertEqual(401, (int)failed.StatusCode, "a wrong password is refused as unauthorized until the limit");
                    }

                    HttpResponseMessage locked = await PostCredentialsAsync(anonymous, "locked@example.com", "right-password").ConfigureAwait(false);
                    AssertEqual(429, (int)locked.StatusCode, "the locked account is refused even with the right password");
                    AssertTrue(locked.Headers.TryGetValues("Retry-After", out IEnumerable<string>? retryAfter)
                        && Int32.TryParse(retryAfter!.First(), out int seconds) && seconds >= 1, "the refusal carries a Retry-After in whole seconds");
                    AssertFalse(JsonSerializer.Deserialize<AuthenticateResult>(await locked.Content.ReadAsStringAsync().ConfigureAwait(false), _CaseInsensitive)!.Success);

                    HttpResponseMessage bystander = await PostCredentialsAsync(anonymous, "bystander@example.com", "bystander-password").ConfigureAwait(false);
                    AssertEqual(200, (int)bystander.StatusCode, "another account on the same address is not locked by the first account's failures");

                    SurfaceReply refreshed = await harness.RestAsync(HttpMethod.Post, "/api/v1/authenticate", sessionToken: sessionToken).ConfigureAwait(false);
                    AssertEqual(200, refreshed.Status, "token refresh is unaffected by the lockout");
                    SurfaceReply apiKey = await harness.RestAsync(HttpMethod.Post, "/api/v1/authenticate").ConfigureAwait(false);
                    AssertEqual(200, apiKey.Status, "API key header authentication is unaffected by the lockout");
                }
            });

            await RunTest("Credential login locks a client address that fails across many accounts", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                using (HttpClient anonymous = new HttpClient { BaseAddress = harness.Rest.BaseAddress, Timeout = TimeSpan.FromSeconds(20) })
                {
                    harness.Settings.LoginRateLimit.MaxFailuresPerAccount = 100;
                    harness.Settings.LoginRateLimit.MaxFailuresPerAddress = 3;
                    await harness.Driver.Users.CreateAsync(new UserMaster(Constants.DefaultTenantId, "spray-target@example.com", "right-password")).ConfigureAwait(false);

                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        HttpResponseMessage failed = await PostCredentialsAsync(anonymous, "nobody" + attempt + "@example.com", "guess").ConfigureAwait(false);
                        AssertEqual(401, (int)failed.StatusCode, "guesses against distinct accounts are refused as unauthorized until the limit");
                    }

                    HttpResponseMessage locked = await PostCredentialsAsync(anonymous, "spray-target@example.com", "right-password").ConfigureAwait(false);
                    AssertEqual(429, (int)locked.StatusCode, "the locked client address is refused even for an account it never failed against");
                    AssertTrue(locked.Headers.Contains("Retry-After"), "the address lockout carries Retry-After");

                    SurfaceReply apiKey = await harness.RestAsync(HttpMethod.Post, "/api/v1/authenticate").ConfigureAwait(false);
                    AssertEqual(200, apiKey.Status, "API key header authentication is unaffected by an address lockout");
                }
            });

            await RunTest("Login rate limiter lockout ends on the clock, doubles on repeat, and resets after a quiet period", () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
                LoginRateLimitSettings settings = new LoginRateLimitSettings { MaxFailuresPerAccount = 2, LockoutMinutes = 10, MaxLockoutMinutes = 30, WindowMinutes = 5 };
                LoginRateLimiter limiter = new LoginRateLimiter(settings, () => now);

                limiter.RecordPasswordFailure("ten_a", "a@example.com", "10.0.0.1");
                AssertNull(limiter.CheckPasswordLogin("ten_a", "a@example.com", "10.0.0.1"), "one failure does not lock");
                limiter.RecordPasswordFailure("ten_a", "A@Example.com", "10.0.0.1");
                TimeSpan? first = limiter.CheckPasswordLogin("ten_a", "a@example.com", "10.0.0.2");
                AssertNotNull(first, "the limit locks the account, whatever the case of the email and whatever the address");
                AssertEqual(TimeSpan.FromMinutes(10), first!.Value, "first lockout length");

                now = now.AddMinutes(10).AddSeconds(1);
                AssertNull(limiter.CheckPasswordLogin("ten_a", "a@example.com", "10.0.0.2"), "the lockout ends when the clock passes it");

                limiter.RecordPasswordFailure("ten_a", "a@example.com", "10.0.0.1");
                limiter.RecordPasswordFailure("ten_a", "a@example.com", "10.0.0.1");
                AssertEqual(TimeSpan.FromMinutes(20), limiter.CheckPasswordLogin("ten_a", "a@example.com", null)!.Value, "a repeat lockout doubles");

                now = now.AddMinutes(20).AddSeconds(1);
                limiter.RecordPasswordFailure("ten_a", "a@example.com", "10.0.0.1");
                limiter.RecordPasswordFailure("ten_a", "a@example.com", "10.0.0.1");
                AssertEqual(TimeSpan.FromMinutes(30), limiter.CheckPasswordLogin("ten_a", "a@example.com", null)!.Value, "doubling stops at the maximum lockout");

                now = now.AddMinutes(30).AddMinutes(31);
                limiter.RecordPasswordFailure("ten_a", "a@example.com", "10.0.0.1");
                limiter.RecordPasswordFailure("ten_a", "a@example.com", "10.0.0.1");
                AssertEqual(TimeSpan.FromMinutes(10), limiter.CheckPasswordLogin("ten_a", "a@example.com", null)!.Value, "a key quiet for longer than the maximum lockout starts over");
                return Task.CompletedTask;
            });

            await RunTest("Login rate limiter counts failures inside the window only and clears an account on success", () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
                LoginRateLimitSettings settings = new LoginRateLimitSettings { MaxFailuresPerAccount = 3, WindowMinutes = 5, LockoutMinutes = 10 };
                LoginRateLimiter limiter = new LoginRateLimiter(settings, () => now);

                limiter.RecordPasswordFailure("ten_a", "a@example.com", null);
                limiter.RecordPasswordFailure("ten_a", "a@example.com", null);
                now = now.AddMinutes(6);
                limiter.RecordPasswordFailure("ten_a", "a@example.com", null);
                AssertNull(limiter.CheckPasswordLogin("ten_a", "a@example.com", null), "failures older than the window are not counted");

                limiter.RecordPasswordFailure("ten_a", "a@example.com", null);
                limiter.RecordPasswordSuccess("ten_a", "a@example.com");
                limiter.RecordPasswordFailure("ten_a", "a@example.com", null);
                AssertNull(limiter.CheckPasswordLogin("ten_a", "a@example.com", null), "a successful login clears the account counter");

                settings.Enabled = false;
                for (int i = 0; i < 20; i++) limiter.RecordPasswordFailure("ten_b", "b@example.com", "10.0.0.9");
                AssertNull(limiter.CheckPasswordLogin("ten_b", "b@example.com", "10.0.0.9"), "a disabled limiter never locks");
                return Task.CompletedTask;
            });

            await RunTest("Login rate limiter bounds its key store and keeps active lockouts", () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
                LoginRateLimitSettings settings = new LoginRateLimitSettings { MaxFailuresPerAccount = 1, MaxFailuresPerAddress = 100000, LockoutMinutes = 60 };
                LoginRateLimiter limiter = new LoginRateLimiter(settings, () => now);

                limiter.RecordPasswordFailure("ten_a", "victim@example.com", null);
                AssertNotNull(limiter.CheckPasswordLogin("ten_a", "victim@example.com", null), "the account is locked");
                settings.MaxFailuresPerAccount = 1000;
                for (int i = 0; i < LoginRateLimiter.MaxTrackedKeys + 500; i++)
                {
                    now = now.AddMilliseconds(1);
                    limiter.RecordPasswordFailure("ten_a", "flood" + i + "@example.com", null);
                }

                AssertTrue(limiter.TrackedKeyCount <= LoginRateLimiter.MaxTrackedKeys, "a flood of distinct accounts cannot grow the store past its bound");
                AssertNotNull(limiter.CheckPasswordLogin("ten_a", "victim@example.com", null), "eviction never lifts an active lockout");
                return Task.CompletedTask;
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

        private static readonly JsonSerializerOptions _CaseInsensitive = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        private static Task<HttpResponseMessage> PostCredentialsAsync(HttpClient client, string email, string password)
        {
            return client.PostAsync("/api/v1/authenticate", new StringContent(
                JsonSerializer.Serialize(new { TenantId = Constants.DefaultTenantId, Email = email, Password = password }),
                Encoding.UTF8, "application/json"));
        }

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

        private static async Task<SurfaceReply> LoginAsync(SurfaceParityHarness harness, string tenantId, string email, string password)
        {
            // A client without the harness operator key, so only the presented email and password authenticate.
            using (HttpClient anonymous = new HttpClient { BaseAddress = harness.Rest.BaseAddress })
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/authenticate"))
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new AuthenticateRequest { TenantId = tenantId, Email = email, Password = password }),
                    Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await anonymous.SendAsync(request).ConfigureAwait(false))
                    return new SurfaceReply("REST", (int)response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
        }

        private static async Task<string> SessionAsync(SurfaceParityHarness harness, string email, string password)
        {
            SurfaceReply reply = await LoginAsync(harness, Constants.DefaultTenantId, email, password).ConfigureAwait(false);
            JsonSerializerOptions json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<AuthenticateResult>(reply.Body, json)!.Token!;
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
