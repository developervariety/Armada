namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;

    /// <summary>
    /// Tests the operator change_quality gate tool: it is registered and operator-scoped (a mission
    /// captain cannot reach it), and a violating diff files exactly one Triaged row and reports it.
    /// </summary>
    public class McpChangeQualityToolsTests : TestSuite
    {
        public override string Name => "MCP Change Quality Tools";

        protected override async Task RunTestsAsync()
        {
            await RunTest("The gate tool is registered and operator-scoped", () =>
            {
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = Register(new RecordingRouter());
                AssertTrue(handlers.ContainsKey("armada_change_quality_gate"), "the gate tool is registered");
                AuthContext captain = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer");
                AssertFalse(McpToolAccessPolicy.IsAllowed(captain, "armada_change_quality_gate"), "the gate tool is operator-scoped, not for a mission captain");
            });

            await RunTest("A violating diff files one Triaged row and reports it", async () =>
            {
                RecordingRouter router = new RecordingRouter();
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = Register(router);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_x",
                    diff = "diff --git a/src/A.csproj b/src/A.csproj\n--- a/src/A.csproj\n+++ b/src/A.csproj\n@@ -1,1 +1,2 @@\n <Project>\n+  <PropertyGroup><NoWarn>CS1591</NoWarn></PropertyGroup>\n"
                });

                object result = await handlers["armada_change_quality_gate"](args).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result);

                AssertEqual(1, router.CreateCount, "exactly one Triaged row is filed");
                AssertContains("\"FiledObjectiveId\"", json);
                AssertContains("obj_fake_1", json);
                AssertContains("core_rule", json);
            });
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> Register(IFollowUpRouter router)
        {
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
            McpChangeQualityTools.Register((name, _, _, handler) => { handlers[name] = handler; }, adapter: null, router: router, logging: null);
            return handlers;
        }

        private sealed class RecordingRouter : IFollowUpRouter
        {
            public int CreateCount { get; private set; }
            public Task<string?> CreateTriagedObjectiveAsync(FollowUpRouteRequest request, CancellationToken token)
            {
                CreateCount++;
                return Task.FromResult<string?>("obj_fake_" + CreateCount);
            }
            public Task<IReadOnlyList<FollowUpDuplicateCandidate>> GetDuplicateCandidatesAsync(string? vesselId, int limit, CancellationToken token)
                => Task.FromResult<IReadOnlyList<FollowUpDuplicateCandidate>>(new List<FollowUpDuplicateCandidate>());
            public Task AppendEvidenceNoteAsync(FollowUpRouteRequest request, CancellationToken token) => Task.CompletedTask;
            public Task LinkDuplicateAsync(FollowUpRouteRequest request, string existingObjectiveId, CancellationToken token) => Task.CompletedTask;
            public Task FlagBlockingForOperatorAsync(FollowUpRouteRequest request, CancellationToken token) => Task.CompletedTask;
        }
    }
}
