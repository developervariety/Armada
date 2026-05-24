namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core;
    using Armada.Proxy.Services;
    using Armada.Proxy.Settings;
    using Armada.Test.Common;

    public class ProxyAuthServiceTests : TestSuite
    {
        public override string Name => "Proxy Auth Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("TryLogin CreatesSessionAndSupportsInstanceSelection", () =>
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
            });

            await RunTest("TryLogin RejectsInvalidProof", () =>
            {
                ProxySettings settings = new ProxySettings
                {
                    Password = "proxy-password"
                };
                ProxyAuthService service = new ProxyAuthService(settings);

                ProxyAuthService.ProxyAuthChallenge challenge = service.CreateChallenge();
                AssertFalse(service.TryLogin(challenge.Nonce, "not-a-valid-proof", out ProxyAuthService.ProxyBrowserSession? _, out string? error));
                AssertContains("invalid", error ?? String.Empty, "Invalid login proof should explain the failure");
            });
        }
    }
}
