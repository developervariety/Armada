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
            await RunTest("Mission credentials cannot enter captain chat even for administrator owners", () =>
            {
                AuthorizationService service = new AuthorizationService();
                foreach (bool isAdmin in new[] { false, true })
                {
                    AuthContext caller = AuthContext.Authenticated("tenant-chat", "user-chat", isAdmin, true, "Session");
                    AssertTrue(service.IsAuthorized(caller, "POST", "/api/v1/captains/cpt_chat/chat"));
                    caller.MissionId = "msn_chat_scope";
                    AssertFalse(service.IsAuthorized(caller, "POST", "/api/v1/captains/cpt_chat/chat"));
                    AssertFalse(service.IsAuthorized(caller, "POST", "/api/v1/captains/cpt_chat/chat/?mode=stream"));
                    AssertTrue(service.IsAuthorized(caller, "GET", "/api/v1/captains/cpt_chat"), "captain reads stay available");
                }
            });

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
