namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Authorization;
    using Armada.Test.Common;

    public class AuthorizationConfigTests : TestSuite
    {
        public override string Name => "AuthorizationConfig";

        protected override async Task RunTestsAsync()
        {
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

            // Enumerate routes read through POST bodies, so they keep the level of the GET list they mirror.
            await RunTest("SharedAsset Enumerate POST IsAuthenticated", () =>
            {
                AssertEqual(PermissionLevel.Authenticated, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/personas/enumerate"));
                AssertEqual(PermissionLevel.Authenticated, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/pipelines/enumerate"));
                AssertEqual(PermissionLevel.Authenticated, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/prompt-templates/enumerate"));
            });

            await RunTest("Enumerate POST Takes The Level Of Its GET List", () =>
            {
                foreach (string collection in new[] { "playbooks", "workflow-profiles", "environments", "objectives", "backlog", "fleets", "vessels", "missions", "voyages", "incidents", "signals" })
                {
                    string list = "/api/v1/" + collection;
                    AssertEqual(AuthorizationConfig.GetPermissionLevel("GET", list), AuthorizationConfig.GetPermissionLevel("POST", list + "/enumerate"), collection + " enumerate");
                    AssertEqual(PermissionLevel.Authenticated, AuthorizationConfig.GetPermissionLevel("POST", list + "/enumerate"), collection + " enumerate is a read");
                    AssertEqual(PermissionLevel.TenantAdmin, AuthorizationConfig.GetPermissionLevel("POST", list), collection + " create stays a tenant-administrator write");
                }
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/jobs/enumerate"), "an enumerate over an admin-only list stays admin-only");
            });

            // --- Server-wide configuration, working-tree writes and accounting deletes ---

            await RunTest("Mux Runtime Routes AreAdminOnly", () =>
            {
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/runtimes/mux/endpoints"));
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/runtimes/mux/endpoints/example"));
            });

            await RunTest("Workspace Writes And TokenUsage Deletes AreTenantAdmin", () =>
            {
                AssertEqual(PermissionLevel.TenantAdmin, AuthorizationConfig.GetPermissionLevel("PUT", "/api/v1/workspace/vessels/vsl_abc/file"));
                AssertEqual(PermissionLevel.TenantAdmin, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/workspace/vessels/vsl_abc/directory"));
                AssertEqual(PermissionLevel.TenantAdmin, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/workspace/vessels/vsl_abc/rename"));
                AssertEqual(PermissionLevel.TenantAdmin, AuthorizationConfig.GetPermissionLevel("DELETE", "/api/v1/workspace/vessels/vsl_abc/entry"));
                AssertEqual(PermissionLevel.Authenticated, AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/workspace/vessels/vsl_abc/file"));
                AssertEqual(PermissionLevel.TenantAdmin, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/token-usage/delete/by-filter"));
                AssertEqual(PermissionLevel.Authenticated, AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/token-usage"));
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

            // --- Host command execution and gate evidence ---

            await RunTest("CheckRun Writes AreTenantAdmin And Reads StayAuthenticated", () =>
            {
                AssertEqual(PermissionLevel.TenantAdmin, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/check-runs"));
                AssertEqual(PermissionLevel.TenantAdmin, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/check-runs/import"));
                AssertEqual(PermissionLevel.TenantAdmin, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/check-runs/chk_abc/retry"));
                AssertEqual(PermissionLevel.TenantAdmin, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/check-runs/sync/github-actions"));
                AssertEqual(PermissionLevel.TenantAdmin, AuthorizationConfig.GetPermissionLevel("DELETE", "/api/v1/check-runs/chk_abc"));
                AssertEqual(PermissionLevel.Authenticated, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/check-runs/enumerate"));
                AssertEqual(PermissionLevel.Authenticated, AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/check-runs/chk_abc"));
            });

            await RunTest("WorkspaceExec POST IsAdminOnly", () =>
            {
                AssertEqual(PermissionLevel.AdminOnly, AuthorizationConfig.GetPermissionLevel("POST", "/api/v1/workspace/vessels/vsl_abc/exec"));
                AssertEqual(PermissionLevel.Authenticated, AuthorizationConfig.GetPermissionLevel("GET", "/api/v1/workspace/vessels/vsl_abc/tree"));
            });
        }
    }
}
