namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Authorization;
    using Armada.Test.Common;

    public class AuthorizationConfigTests : TestSuite
    {
        public override string Name => "AuthorizationConfig";

        protected override async Task RunTestsAsync()
        {
            // --- NoAuthRequired endpoints ---

            await RunTest("Authenticate POST IsNoAuthRequired", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/authenticate");
                AssertEqual(PermissionLevel.NoAuthRequired, level);
            });

            await RunTest("TenantsLookup POST IsNoAuthRequired", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/tenants/lookup");
                AssertEqual(PermissionLevel.NoAuthRequired, level);
            });

            await RunTest("Onboarding POST IsNoAuthRequired", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/onboarding");
                AssertEqual(PermissionLevel.NoAuthRequired, level);
            });

            await RunTest("Dashboard IsNoAuthRequired", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/dashboard");
                AssertEqual(PermissionLevel.NoAuthRequired, level);
            });

            await RunTest("DashboardSubpath IsNoAuthRequired", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/dashboard/missions");
                AssertEqual(PermissionLevel.NoAuthRequired, level);
            });

            await RunTest("Root IsNoAuthRequired", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/");
                AssertEqual(PermissionLevel.NoAuthRequired, level);
            });

            // --- AdminOnly endpoints ---

            await RunTest("Status GET IsAdminOnly", () =>
            {
                // Fleet status aggregates every tenant, so it is a global-administrator read like the
                // WebSocket status snapshot. The unauthenticated health route stays open.
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/status"));
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/status/"));
                AssertEqual(PermissionLevel.NoAuthRequired, AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/status/health"));
            });

            await RunTest("StopAll And WholeMergeQueueProcess AreAdminOnly", () =>
            {
                // Both act on every tenant at once, so a tenant administrator may not run them. Processing one
                // merge entry stays a tenant-scoped tenant-administrator action.
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/captains/stop-all"));
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/captains/stop-all/"));
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/merge-queue/process"));
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/merge-queue/process/"));
                AssertEqual(PermissionLevel.TenantAdmin, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/merge-queue/mrg_example/process"));
                AssertEqual(PermissionLevel.TenantAdmin, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/captains/cpt_example/stop"));
            });

            await RunTest("Tenants GET IsAdminOnly", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/tenants");
                AssertEqual(PermissionLevel.AdminOnly, level);
            });

            await RunTest("Tenants POST IsAdminOnly", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/tenants");
                AssertEqual(PermissionLevel.AdminOnly, level);
            });

            await RunTest("Tenant PUT IsAdminOnly", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("PUT", "/api/v1/tenants/ten_abc");
                AssertEqual(PermissionLevel.AdminOnly, level);
            });

            await RunTest("Tenant DELETE IsAdminOnly", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("DELETE", "/api/v1/tenants/ten_abc");
                AssertEqual(PermissionLevel.AdminOnly, level);
            });

            await RunTest("Users GET IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/users");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            await RunTest("Users POST IsTenantAdmin", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/users");
                AssertEqual(PermissionLevel.TenantAdmin, level);
            });

            await RunTest("User PUT IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("PUT", "/api/v1/users/usr_abc");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            await RunTest("User DELETE IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("DELETE", "/api/v1/users/usr_abc");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            await RunTest("Credential PUT IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("PUT", "/api/v1/credentials/crd_abc");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            // --- Authenticated endpoints (everything else) ---

            await RunTest("Fleets GET IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/fleets");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            await RunTest("Missions GET IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/missions");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            await RunTest("PlanningSessions GET IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/planning-sessions");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            await RunTest("Captains GET IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/captains");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            await RunTest("Vessels GET IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/vessels");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            await RunTest("Credentials GET IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/credentials");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            await RunTest("Credentials POST IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/credentials");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            await RunTest("Credential DELETE IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("DELETE", "/api/v1/credentials/crd_abc");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            await RunTest("Fleets POST IsTenantAdmin", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/fleets");
                AssertEqual(PermissionLevel.TenantAdmin, level);
            });

            await RunTest("PlanningSessions POST IsTenantAdmin", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/planning-sessions");
                AssertEqual(PermissionLevel.TenantAdmin, level);
            });

            await RunTest("RequestHistory GET IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/request-history");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            await RunTest("RequestHistory DELETE IsTenantAdmin", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("DELETE", "/api/v1/request-history/req_abc");
                AssertEqual(PermissionLevel.TenantAdmin, level);
            });

            await RunTest("Releases GET IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/releases");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            await RunTest("Releases POST IsTenantAdmin", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/releases");
                AssertEqual(PermissionLevel.TenantAdmin, level);
            });

            await RunTest("Environments GET IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/environments");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            await RunTest("Environments POST IsTenantAdmin", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/environments");
                AssertEqual(PermissionLevel.TenantAdmin, level);
            });

            await RunTest("Server POST IsAdminOnly", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/server/stop");
                AssertEqual(PermissionLevel.AdminOnly, level);
            });

            // --- Shared assets: personas and pipelines are tenant-owned, prompt templates are global ---

            await RunTest("Personas GET IsAuthenticated", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/personas");
                AssertEqual(PermissionLevel.Authenticated, level);
            });

            await RunTest("Personas POST IsTenantAdmin", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/personas");
                AssertEqual(PermissionLevel.TenantAdmin, level);
            });

            await RunTest("Persona PUT IsTenantAdmin", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("PUT", "/api/v1/personas/Worker");
                AssertEqual(PermissionLevel.TenantAdmin, level);
            });

            await RunTest("Pipeline DELETE IsTenantAdmin", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("DELETE", "/api/v1/pipelines/FullPipeline");
                AssertEqual(PermissionLevel.TenantAdmin, level);
            });

            await RunTest("PromptTemplates POST IsAdminOnly", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/prompt-templates");
                AssertEqual(PermissionLevel.AdminOnly, level);
            });

            await RunTest("PromptTemplate PUT IsAdminOnly", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("PUT", "/api/v1/prompt-templates/mission.rules");
                AssertEqual(PermissionLevel.AdminOnly, level);
            });

            await RunTest("PromptTemplate Reset POST IsAdminOnly", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/prompt-templates/mission.rules/reset");
                AssertEqual(PermissionLevel.AdminOnly, level);
            });

            // Enumerate routes read through POST bodies, so they keep the read level.
            await RunTest("SharedAsset Enumerate POST IsAuthenticated", () =>
            {
                AssertEqual(PermissionLevel.Authenticated, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/personas/enumerate"));
                AssertEqual(PermissionLevel.Authenticated, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/pipelines/enumerate"));
                AssertEqual(PermissionLevel.Authenticated, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/prompt-templates/enumerate"));
            });

            // --- Fleet-wide aggregates read across every tenant with no caller scope ---

            await RunTest("Inbox GET IsAdminOnly", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/inbox");
                AssertEqual(PermissionLevel.AdminOnly, level);
            });

            await RunTest("Ask POST IsAdminOnly", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/ask");
                AssertEqual(PermissionLevel.AdminOnly, level);
            });

            await RunTest("CaptainChat POST IsTenantAdmin", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/captains/cpt_abc/chat");
                AssertEqual(PermissionLevel.TenantAdmin, level);
            });

            // --- Coordination board: rooms are found by key alone in every tenant ---

            await RunTest("Coordination Routes AreAdminOnly", () =>
            {
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/coordination/rooms"));
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/coordination/rooms"));
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/coordination/rooms/fleet/messages"));
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/coordination/rooms/fleet/messages"));
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/coordination/rooms/fleet/presence"));
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/coordination/claims"));
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/coordination/rooms/fleet/participants"));
            });

            // --- Case insensitivity ---

            await RunTest("MethodCaseInsensitive", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("get", "/api/v1/status/health");
                AssertEqual(PermissionLevel.NoAuthRequired, level);
            });

            await RunTest("PathCaseInsensitive", () =>
            {
                PermissionLevel level = AuthorizationConfig.GetPermissionLevel("GET", "/API/V1/Status/Health");
                AssertEqual(PermissionLevel.NoAuthRequired, level);
            });
        }
    }
}
