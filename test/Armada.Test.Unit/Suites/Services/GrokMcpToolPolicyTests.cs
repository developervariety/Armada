namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
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
        }
    }
}
