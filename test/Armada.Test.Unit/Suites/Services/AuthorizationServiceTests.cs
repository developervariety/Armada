namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    public class AuthorizationServiceTests : TestSuite
    {
        public override string Name => "AuthorizationService";

        protected override async Task RunTestsAsync()
        {
            // --- IsAuthorized tests ---

            await RunTest("IsAuthorized Status Matrix Admits Only Global Administrators", () =>
            {
                AuthorizationService svc = new AuthorizationService();
                AuthContext anonymous = new AuthContext();
                AuthContext user = AuthContext.Authenticated("ten_abc", "usr_xyz", false, false, "Session");
                AuthContext tenantAdmin = AuthContext.Authenticated("ten_abc", "usr_admin", false, true, "Session");
                AuthContext globalAdmin = AuthContext.Authenticated("default", "default", true, true, "ApiKey");

                AssertFalse(svc.IsAuthorized(anonymous, "GET", "/api/v1/status"), "anonymous caller");
                AssertFalse(svc.IsAuthorized(user, "GET", "/api/v1/status"), "tenant user");
                AssertFalse(svc.IsAuthorized(tenantAdmin, "GET", "/api/v1/status"), "tenant administrator");
                AssertTrue(svc.IsAuthorized(globalAdmin, "GET", "/api/v1/status"), "global administrator");
                AssertTrue(svc.IsAuthorized(anonymous, "GET", "/api/v1/status/health"), "health stays open");
            });
        }
    }
}
