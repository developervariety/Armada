namespace Armada.Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="SessionTokenService"/>: creation and validation of
    /// self-contained AES-256-CBC session tokens plus key management. Positive cases assert
    /// successful issue/validate round-trips and key handling; negative cases assert rejection
    /// of null/empty identities, and null/empty/tampered/garbage/wrong-key tokens. The audit
    /// adds the invalid-base64-key constructor failure path.
    /// </summary>
    public sealed class SessionTokenServiceSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the SessionTokenService suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("create_token_returns_success_with_token", "CreateToken ReturnsSuccessWithToken", TestTags.Positive, () =>
            {
                SessionTokenService svc = new SessionTokenService();
                AuthenticateResult result = svc.CreateToken("ten_abc", "usr_xyz");

                AssertTrue(result.Success);
                AssertNotNull(result.Token);
                AssertTrue(result.Token!.Length > 0, "Token should not be empty");
                AssertNotNull(result.ExpiresUtc);
            }));

            cases.Add(Case("create_token_expires_utc_is_in_future", "CreateToken ExpiresUtc IsInFuture", TestTags.Positive, () =>
            {
                SessionTokenService svc = new SessionTokenService();
                AuthenticateResult result = svc.CreateToken("ten_abc", "usr_xyz");

                AssertTrue(result.ExpiresUtc > DateTime.UtcNow, "ExpiresUtc should be in the future");
                // Should be approximately SessionTokenLifetimeHours from now
                double hoursUntilExpiry = (result.ExpiresUtc!.Value - DateTime.UtcNow).TotalHours;
                AssertTrue(hoursUntilExpiry > Constants.SessionTokenLifetimeHours - 1, "Expiry should be close to lifetime");
                AssertTrue(hoursUntilExpiry <= Constants.SessionTokenLifetimeHours, "Expiry should not exceed lifetime");
            }));

            cases.Add(Case("create_token_null_tenant_id_throws", "CreateToken NullTenantId Throws", TestTags.Negative, () =>
            {
                SessionTokenService svc = new SessionTokenService();
                AssertThrows<ArgumentNullException>(() => svc.CreateToken(null!, "usr_xyz"));
            }));

            cases.Add(Case("create_token_empty_tenant_id_throws", "CreateToken EmptyTenantId Throws", TestTags.Negative, () =>
            {
                SessionTokenService svc = new SessionTokenService();
                AssertThrows<ArgumentNullException>(() => svc.CreateToken("", "usr_xyz"));
            }));

            cases.Add(Case("create_token_null_user_id_throws", "CreateToken NullUserId Throws", TestTags.Negative, () =>
            {
                SessionTokenService svc = new SessionTokenService();
                AssertThrows<ArgumentNullException>(() => svc.CreateToken("ten_abc", null!));
            }));

            cases.Add(Case("create_token_empty_user_id_throws", "CreateToken EmptyUserId Throws", TestTags.Negative, () =>
            {
                SessionTokenService svc = new SessionTokenService();
                AssertThrows<ArgumentNullException>(() => svc.CreateToken("ten_abc", ""));
            }));

            cases.Add(Case("validate_token_valid_token_returns_auth_context", "ValidateToken ValidToken ReturnsAuthContext", TestTags.Positive, () =>
            {
                SessionTokenService svc = new SessionTokenService();
                AuthenticateResult result = svc.CreateToken("ten_abc", "usr_xyz");

                AuthContext? ctx = svc.ValidateToken(result.Token!);
                AssertNotNull(ctx);
                AssertTrue(ctx!.IsAuthenticated);
                AssertEqual("ten_abc", ctx.TenantId);
                AssertEqual("usr_xyz", ctx.UserId);
                AssertEqual("Session", ctx.AuthMethod);
            }));

            cases.Add(Case("validate_token_null_token_returns_null", "ValidateToken NullToken ReturnsNull", TestTags.Negative, () =>
            {
                SessionTokenService svc = new SessionTokenService();
                AuthContext? ctx = svc.ValidateToken(null!);
                AssertNull(ctx);
            }));

            cases.Add(Case("validate_token_empty_token_returns_null", "ValidateToken EmptyToken ReturnsNull", TestTags.Negative, () =>
            {
                SessionTokenService svc = new SessionTokenService();
                AuthContext? ctx = svc.ValidateToken("");
                AssertNull(ctx);
            }));

            cases.Add(Case("validate_token_tampered_token_returns_null", "ValidateToken TamperedToken ReturnsNull", TestTags.Negative, () =>
            {
                SessionTokenService svc = new SessionTokenService();
                AuthenticateResult result = svc.CreateToken("ten_abc", "usr_xyz");

                // Tamper with the token
                string tampered = result.Token! + "TAMPERED";
                AuthContext? ctx = svc.ValidateToken(tampered);
                AssertNull(ctx);
            }));

            cases.Add(Case("validate_token_garbage_string_returns_null", "ValidateToken GarbageString ReturnsNull", TestTags.Negative, () =>
            {
                SessionTokenService svc = new SessionTokenService();
                AuthContext? ctx = svc.ValidateToken("not-a-valid-token-at-all");
                AssertNull(ctx);
            }));

            cases.Add(Case("validate_token_different_key_returns_null", "ValidateToken DifferentKey ReturnsNull", TestTags.Negative, () =>
            {
                SessionTokenService svc1 = new SessionTokenService();
                SessionTokenService svc2 = new SessionTokenService();

                AuthenticateResult result = svc1.CreateToken("ten_abc", "usr_xyz");

                // Validate with a different key
                AuthContext? ctx = svc2.ValidateToken(result.Token!);
                AssertNull(ctx);
            }));

            cases.Add(Case("constructor_with_explicit_key_uses_provided_key", "Constructor WithExplicitKey UsesProvidedKey", TestTags.Positive, () =>
            {
                byte[] key = new byte[32];
                RandomNumberGenerator.Fill(key);
                string keyBase64 = Convert.ToBase64String(key);

                SessionTokenService svc = new SessionTokenService(keyBase64);
                AssertEqual(keyBase64, svc.GetKeyBase64());
            }));

            cases.Add(Case("constructor_without_key_generates_key", "Constructor WithoutKey GeneratesKey", TestTags.Positive, () =>
            {
                SessionTokenService svc = new SessionTokenService();
                string keyBase64 = svc.GetKeyBase64();
                AssertNotNull(keyBase64);
                byte[] key = Convert.FromBase64String(keyBase64);
                AssertEqual(32, key.Length, "AES-256 key should be 32 bytes");
            }));

            cases.Add(Case("create_token_then_validate_with_explicit_key_round_trips", "CreateToken ThenValidate WithExplicitKey RoundTrips", TestTags.Positive, () =>
            {
                byte[] key = new byte[32];
                RandomNumberGenerator.Fill(key);
                string keyBase64 = Convert.ToBase64String(key);

                SessionTokenService svc1 = new SessionTokenService(keyBase64);
                AuthenticateResult result = svc1.CreateToken("ten_test", "usr_test");

                // Validate with same key, different instance
                SessionTokenService svc2 = new SessionTokenService(keyBase64);
                AuthContext? ctx = svc2.ValidateToken(result.Token!);

                AssertNotNull(ctx);
                AssertEqual("ten_test", ctx!.TenantId);
                AssertEqual("usr_test", ctx.UserId);
            }));

            cases.Add(Case("create_token_unique_tokens_per_call", "CreateToken UniqueTokensPerCall", TestTags.Positive, () =>
            {
                SessionTokenService svc = new SessionTokenService();
                AuthenticateResult r1 = svc.CreateToken("ten_abc", "usr_xyz");
                AuthenticateResult r2 = svc.CreateToken("ten_abc", "usr_xyz");

                // Tokens should differ because of random IV
                AssertNotEqual(r1.Token, r2.Token);
            }));

            // Audit addition: invalid base64 key rejected by the constructor (confirmed against source)

            cases.Add(Case("constructor_with_invalid_base64_key_throws", "Constructor WithInvalidBase64Key Throws", TestTags.Negative, () =>
            {
                AssertThrows<FormatException>(() => new SessionTokenService("not-valid-base64!!!"));
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.SessionTokenService",
                displayName: "SessionTokenService",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.SessionTokenService",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.SessionTokenService",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
