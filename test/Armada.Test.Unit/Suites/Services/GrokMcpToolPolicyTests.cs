namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Server.Mcp;
    using Armada.Test.Common;

    /// <summary>
    /// Locks the restricted Grok Bot tool policy to an explicit reviewed catalog.
    /// </summary>
    public class GrokMcpToolPolicyTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "GrokMcpToolPolicy";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Restricted catalog contains required lead reads", () =>
            {
                IReadOnlyCollection<string> tools = GrokMcpToolRegistrar.ReadOnlyToolNames();
                AssertTrue(tools.Contains("armada_enumerate"));
                AssertTrue(tools.Contains("armada_coordination_read"));
                AssertTrue(tools.Contains("inbox"));
                AssertTrue(tools.Contains("armada_list_incidents"));
                AssertTrue(tools.Contains("armada_objective_scheduler_status"));
                AssertTrue(tools.Contains("armada_voyage_status"));
                AssertTrue(tools.Contains("armada_agentwake_status"));
                return Task.CompletedTask;
            });

            await RunTest("Restricted catalog omits owner and destructive tools", () =>
            {
                List<string> tools = GrokMcpToolRegistrar.ReadOnlyToolNames()
                    .Concat(GrokMcpToolRegistrar.ReversibleToolNames())
                    .ToList();
                string[] prohibitedFragments = new[]
                {
                    "delete",
                    "purge",
                    "cancel",
                    "stop_server",
                    "backup",
                    "restore",
                    "deploy",
                    "release_create",
                    "scheduler_set",
                    "dispatch_hold"
                };
                foreach (string fragment in prohibitedFragments)
                {
                    AssertFalse(
                        tools.Any(tool => tool.Contains(fragment, StringComparison.Ordinal)),
                        "Restricted catalog must omit tool fragment: " + fragment);
                }
                return Task.CompletedTask;
            });

            await RunTest("Read and reversible catalogs are disjoint", () =>
            {
                HashSet<string> readOnly = new HashSet<string>(
                    GrokMcpToolRegistrar.ReadOnlyToolNames(), StringComparer.Ordinal);
                readOnly.IntersectWith(GrokMcpToolRegistrar.ReversibleToolNames());
                AssertEqual(0, readOnly.Count, "A tool must have one policy class.");
                return Task.CompletedTask;
            });

            await RunTest("Read-only mode advertises no state-changing tools", () =>
            {
                IReadOnlyCollection<string> tools = GrokMcpToolRegistrar.AllowedToolNames(true);
                AssertTrue(tools.Contains("armada_status"));
                AssertTrue(tools.Contains("armada_lead_cycle_status"));
                AssertFalse(tools.Contains("armada_coordination_post"));
                AssertFalse(tools.Contains("armada_lead_cycle_begin"));
                AssertFalse(tools.Contains("armada_lead_cycle_complete"));
                return Task.CompletedTask;
            });

            await RunTest("Full mode includes reversible and lifecycle tools", () =>
            {
                IReadOnlyCollection<string> tools = GrokMcpToolRegistrar.AllowedToolNames(false);
                AssertTrue(tools.Contains("armada_coordination_post"));
                AssertTrue(tools.Contains("armada_lead_cycle_begin"));
                AssertTrue(tools.Contains("armada_lead_cycle_complete"));
                return Task.CompletedTask;
            });

            await RunTest("Controlled mode adds only the bounded dispatch tool", () =>
            {
                IReadOnlyCollection<string> tools = GrokMcpToolRegistrar.AllowedToolNames(false, true);
                AssertTrue(tools.Contains("armada_dispatch"));
                AssertFalse(tools.Contains("armada_cancel_voyage"));
                AssertFalse(tools.Contains("armada_purge_voyage"));
                AssertFalse(tools.Contains("armada_dispatch_hold"));
                return Task.CompletedTask;
            });

            await RunTest("Controlled dispatch requires an objective and strips code context", () =>
            {
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    title = "POC dispatch",
                    vesselId = "vsl_test",
                    objectiveId = "obj_test",
                    missions = new[] { new { title = "Read a file", description = "Run the harmless POC mission." } }
                });
                VoyageDispatchArgs request = GrokMcpToolRegistrar.ParseControlledDispatch(args, 3);
                AssertEqual("off", request.CodeContextMode);
                AssertEqual(1, request.Missions.Count);
                return Task.CompletedTask;
            });

            await RunTest("Controlled dispatch rejects staging and captain overrides", () =>
            {
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    title = "Unsafe dispatch",
                    vesselId = "vsl_test",
                    objectiveId = "obj_test",
                    captainAssignments = Array.Empty<object>(),
                    missions = new[] { new { title = "Write", description = "Do work." } }
                });
                bool rejected = false;
                try { GrokMcpToolRegistrar.ParseControlledDispatch(args, 3); }
                catch (ArgumentException) { rejected = true; }
                AssertTrue(rejected);
                return Task.CompletedTask;
            });
        }
    }
}
