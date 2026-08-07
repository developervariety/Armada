namespace Armada.Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Proxy.Services;
    using Armada.Proxy.Settings;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="ProxyAuthService"/>: browser challenge/login, session validation,
    /// and selected-instance state. Positive cases cover the full login-and-select flow; negative
    /// cases cover invalid proofs, missing/unknown/reused challenges, and unknown or expired sessions.
    /// </summary>
    public sealed class ProxyAuthServiceSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Proxy Auth Service suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("try_login_creates_session_and_supports_instance_selection", "TryLogin CreatesSessionAndSupportsInstanceSelection", TestTags.Positive, () =>
            {
                ProxySettings settings = new ProxySettings
                {
                    Password = "proxy-password"
                };
                ProxyAuthService service = new ProxyAuthService(settings);

                ProxyAuthService.ProxyAuthChallenge challenge = service.CreateChallenge();
                string proof = RemoteTunnelAuth.ComputeBrowserLoginProof(settings.Password, challenge.Nonce);

                AssertTrue(service.TryLogin(challenge.Nonce, proof, out ProxyAuthService.ProxyBrowserSession? session, out string? error), error ?? "Proxy login should succeed");
                AssertNotNull(session);
                AssertTrue(service.TryValidateSession(session!.Token, out DateTime? expiresUtc), "Created proxy session should validate");
                AssertNotNull(expiresUtc);

                AssertTrue(service.TrySetSelectedInstance(session.Token, "armada-123", out ProxyAuthService.ProxyBrowserSession? selectedSession, out string? selectionError), selectionError ?? "Setting selected instance should succeed");
                AssertEqual("armada-123", selectedSession!.SelectedInstanceId);

                AssertTrue(service.TryGetSession(session.Token, out ProxyAuthService.ProxyBrowserSession? fetchedSession), "Selected proxy session should be readable");
                AssertEqual("armada-123", fetchedSession!.SelectedInstanceId);

                AssertTrue(service.TrySetSelectedInstance(session.Token, null, out ProxyAuthService.ProxyBrowserSession? clearedSession, out string? clearError), clearError ?? "Clearing selected instance should succeed");
                AssertTrue(String.IsNullOrWhiteSpace(clearedSession!.SelectedInstanceId), "Selected instance should be cleared");
            }));

            cases.Add(Case("try_login_rejects_invalid_proof", "TryLogin RejectsInvalidProof", TestTags.Negative, () =>
            {
                ProxySettings settings = new ProxySettings
                {
                    Password = "proxy-password"
                };
                ProxyAuthService service = new ProxyAuthService(settings);

                ProxyAuthService.ProxyAuthChallenge challenge = service.CreateChallenge();
                AssertFalse(service.TryLogin(challenge.Nonce, "not-a-valid-proof", out ProxyAuthService.ProxyBrowserSession? _, out string? error));
                AssertContains("invalid", error ?? String.Empty, "Invalid login proof should explain the failure");
            }));

            cases.Add(Case("try_login_requires_nonce_and_proof", "TryLogin RequiresNonceAndProof", TestTags.Negative, () =>
            {
                ProxySettings settings = new ProxySettings
                {
                    Password = "proxy-password"
                };
                ProxyAuthService service = new ProxyAuthService(settings);

                AssertFalse(service.TryLogin(null, null, out ProxyAuthService.ProxyBrowserSession? _, out string? error));
                AssertContains("required", error ?? String.Empty, "Missing nonce and proof should be rejected");
            }));

            cases.Add(Case("try_login_rejects_unknown_nonce", "TryLogin RejectsUnknownNonce", TestTags.Negative, () =>
            {
                ProxySettings settings = new ProxySettings
                {
                    Password = "proxy-password"
                };
                ProxyAuthService service = new ProxyAuthService(settings);

                string unknownNonce = RemoteTunnelAuth.CreateNonce();
                string proof = RemoteTunnelAuth.ComputeBrowserLoginProof(settings.Password, unknownNonce);
                AssertFalse(service.TryLogin(unknownNonce, proof, out ProxyAuthService.ProxyBrowserSession? _, out string? error), "Login against a challenge that was never issued should fail");
                AssertNotNull(error);
            }));

            cases.Add(Case("try_login_rejects_reused_challenge", "TryLogin RejectsReusedChallenge", TestTags.Negative, () =>
            {
                ProxySettings settings = new ProxySettings
                {
                    Password = "proxy-password"
                };
                ProxyAuthService service = new ProxyAuthService(settings);

                ProxyAuthService.ProxyAuthChallenge challenge = service.CreateChallenge();
                string proof = RemoteTunnelAuth.ComputeBrowserLoginProof(settings.Password, challenge.Nonce);

                AssertTrue(service.TryLogin(challenge.Nonce, proof, out ProxyAuthService.ProxyBrowserSession? _, out string? firstError), firstError ?? "First login should succeed");
                AssertFalse(service.TryLogin(challenge.Nonce, proof, out ProxyAuthService.ProxyBrowserSession? _, out string? secondError), "A single-use challenge should not be redeemable twice");
                AssertNotNull(secondError);
            }));

            cases.Add(Case("try_validate_session_rejects_unknown_token", "TryValidateSession RejectsUnknownToken", TestTags.Negative, () =>
            {
                ProxySettings settings = new ProxySettings
                {
                    Password = "proxy-password"
                };
                ProxyAuthService service = new ProxyAuthService(settings);

                AssertFalse(service.TryValidateSession("not-a-real-token", out DateTime? expiresUtc), "An unknown session token should not validate");
                AssertNull(expiresUtc);
            }));

            cases.Add(Case("try_set_selected_instance_rejects_invalid_token", "TrySetSelectedInstance RejectsInvalidToken", TestTags.Negative, () =>
            {
                ProxySettings settings = new ProxySettings
                {
                    Password = "proxy-password"
                };
                ProxyAuthService service = new ProxyAuthService(settings);

                AssertFalse(service.TrySetSelectedInstance("not-a-real-token", "armada-123", out ProxyAuthService.ProxyBrowserSession? session, out string? error), "Selecting an instance on an unknown session should fail");
                AssertNull(session);
                AssertContains("invalid", error ?? String.Empty, "Invalid session should explain the failure");
            }));

            cases.Add(Case("try_validate_session_rejects_expired_session", "TryValidateSession RejectsExpiredSession", TestTags.Negative, () =>
            {
                DateTime nowUtc = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc);
                ProxySettings settings = new ProxySettings
                {
                    Password = "proxy-password"
                };
                ProxyAuthService service = new ProxyAuthService(settings, () => nowUtc);

                ProxyAuthService.ProxyAuthChallenge challenge = service.CreateChallenge();
                string proof = RemoteTunnelAuth.ComputeBrowserLoginProof(settings.Password, challenge.Nonce);
                AssertTrue(service.TryLogin(challenge.Nonce, proof, out ProxyAuthService.ProxyBrowserSession? session, out string? error), error ?? "Proxy login should succeed");
                AssertNotNull(session);

                nowUtc = nowUtc.AddHours(Constants.SessionTokenLifetimeHours + 1);
                AssertFalse(service.TryValidateSession(session!.Token, out DateTime? expiresUtc), "A session past its lifetime should no longer validate");
                AssertNull(expiresUtc);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.ProxyAuthService",
                displayName: "Proxy Auth Service",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.ProxyAuthService",
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
                suiteId: "Services.ProxyAuthService",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
