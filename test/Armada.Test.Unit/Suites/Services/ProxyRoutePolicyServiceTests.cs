namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Models;
    using Armada.Proxy.Services;
    using Armada.Test.Common;

    public class ProxyRoutePolicyServiceTests : TestSuite
    {
        public override string Name => "Proxy Route Policy";

        protected override async Task RunTestsAsync()
        {
            await RunTest("TryAuthorize AllowsRegularDashboardReadRoutes", () =>
            {
                ProxyRoutePolicyService service = new ProxyRoutePolicyService();
                bool allowed = service.TryAuthorize(new RemoteTunnelHttpRelayRequest
                {
                    Method = "GET",
                    Path = "/api/v1/fleets"
                }, out int statusCode, out string? message);

                AssertTrue(allowed, message ?? "Fleet list relay should be allowed");
                AssertEqual(200, statusCode);
            });

            await RunTest("TryAuthorize DeniesHighRiskAdministrativeWrites", () =>
            {
                ProxyRoutePolicyService service = new ProxyRoutePolicyService();

                bool settingsAllowed = service.TryAuthorize(new RemoteTunnelHttpRelayRequest
                {
                    Method = "PUT",
                    Path = "/api/v1/settings"
                }, out int settingsStatusCode, out string? settingsMessage);
                AssertFalse(settingsAllowed);
                AssertEqual(403, settingsStatusCode);
                AssertContains("blocked", settingsMessage ?? String.Empty, "Settings write should be blocked");

                bool restoreAllowed = service.TryAuthorize(new RemoteTunnelHttpRelayRequest
                {
                    Method = "POST",
                    Path = "/api/v1/restore"
                }, out int restoreStatusCode, out string? restoreMessage);
                AssertFalse(restoreAllowed);
                AssertEqual(403, restoreStatusCode);
                AssertContains("blocked", restoreMessage ?? String.Empty, "Restore should be blocked");
            });

            await RunTest("TryAuthorize AllowsRemoteLoginBootstrapRoutes", () =>
            {
                ProxyRoutePolicyService service = new ProxyRoutePolicyService();

                bool tenantLookupAllowed = service.TryAuthorize(new RemoteTunnelHttpRelayRequest
                {
                    Method = "POST",
                    Path = "/api/v1/tenants/lookup"
                }, out int tenantLookupStatusCode, out string? tenantLookupMessage);
                AssertTrue(tenantLookupAllowed, tenantLookupMessage ?? "Tenant lookup should be allowed for remote login");
                AssertEqual(200, tenantLookupStatusCode);

                bool authenticateAllowed = service.TryAuthorize(new RemoteTunnelHttpRelayRequest
                {
                    Method = "POST",
                    Path = "/api/v1/authenticate"
                }, out int authenticateStatusCode, out string? authenticateMessage);
                AssertTrue(authenticateAllowed, authenticateMessage ?? "Authenticate should be allowed for remote login");
                AssertEqual(200, authenticateStatusCode);
            });
        }
    }
}
