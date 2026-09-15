namespace Armada.Test.Unit
{
    using System;
    using System.IO;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;

    /// <summary>
    /// Pins every mission-transition entry point to one table. <see cref="MissionStateMachine"/> owns the
    /// transition rules; the MCP helper, the agent lifecycle handler and the operator transition service
    /// delegate to it, and the REST, WebSocket and MCP transitions delegate to the operator transition
    /// service. A second copy of the rules would give a caller an answer that depends on which copy it
    /// reaches, so these tests assert the PullRequestOpen pairs, agreement across the whole enum, and the
    /// delegation of every entry point.
    /// </summary>
    public sealed class MissionTransitionTableAgreementTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Mission Transition Table Agreement";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            // === The PullRequestOpen gap: absent from the MCP helper's copy ===

            await RunTest("MCP helper allows WorkProduced to PullRequestOpen", () =>
            {
                Assert(
                    McpToolHelpers.IsValidTransition(MissionStatusEnum.WorkProduced, MissionStatusEnum.PullRequestOpen),
                    "WorkProduced to PullRequestOpen is the PR-fallback flow and must be allowed");
            });

            await RunTest("MCP helper allows PullRequestOpen to Complete", () =>
            {
                Assert(
                    McpToolHelpers.IsValidTransition(MissionStatusEnum.PullRequestOpen, MissionStatusEnum.Complete),
                    "PullRequestOpen to Complete must be allowed");
            });

            await RunTest("MCP helper allows PullRequestOpen to LandingFailed", () =>
            {
                Assert(
                    McpToolHelpers.IsValidTransition(MissionStatusEnum.PullRequestOpen, MissionStatusEnum.LandingFailed),
                    "PullRequestOpen to LandingFailed must be allowed");
            });

            await RunTest("MCP helper allows PullRequestOpen to Cancelled", () =>
            {
                Assert(
                    McpToolHelpers.IsValidTransition(MissionStatusEnum.PullRequestOpen, MissionStatusEnum.Cancelled),
                    "PullRequestOpen to Cancelled must be allowed");
            });

            // === Whole-table agreement, so a future copy cannot drift unnoticed ===

            await RunTest("MCP helper agrees with the state machine for every status pair", () =>
            {
                MissionStatusEnum[] statuses = (MissionStatusEnum[])Enum.GetValues(typeof(MissionStatusEnum));
                int compared = 0;

                foreach (MissionStatusEnum current in statuses)
                {
                    foreach (MissionStatusEnum target in statuses)
                    {
                        bool expected = MissionStateMachine.IsValidTransition(current, target);
                        bool actual = McpToolHelpers.IsValidTransition(current, target);
                        Assert(
                            expected == actual,
                            "Transition " + current + " to " + target + ": state machine says " + expected +
                            " but the MCP helper says " + actual);
                        compared++;
                    }
                }

                Assert(compared == statuses.Length * statuses.Length, "Every status pair was compared");
            });

            // === Delegation: no entry point keeps its own transition rules ===
            // The validators below are private or live in projects this suite does not reference, so these
            // guards assert that each entry point calls the shared rule rather than restating it.

            await RunTest("AgentLifecycleHandler validator delegates to the shared state machine", () =>
            {
                string contents = ReadSource(Path.Combine("src", "Armada.Server", "AgentLifecycleHandler.cs"));
                AssertContains("return MissionStateMachine.IsValidTransition(current, target);", contents, "Delegates to MissionStateMachine");
            });

            await RunTest("MissionStatusTransitionService validator delegates to the shared state machine", () =>
            {
                string contents = ReadSource(Path.Combine("src", "Armada.Server", "MissionStatusTransitionService.cs"));
                AssertContains("MissionStateMachine.IsValidTransition(mission.Status, newStatus)", contents, "Delegates to MissionStateMachine");
            });

            await RunTest("WebSocketCommandHandler status transition delegates to the shared transition service", () =>
            {
                string contents = ReadSource(Path.Combine("src", "Armada.Server", "WebSocket", "WebSocketCommandHandler.cs"));
                AssertContains("_StatusTransitions.TransitionAsync(tmMission, tmNewStatus)", contents, "Delegates to MissionStatusTransitionService");
                Assert(!contents.Contains("IsValidTransition("), "No local transition validator remains");
                Assert(!contents.Contains("tmMission.Status = tmNewStatus"), "No local status write bypasses the completion gates");
            });

            await RunTest("MissionRoutes status transition delegates to the shared transition service", () =>
            {
                string contents = ReadSource(Path.Combine("src", "Armada.Server", "Routes", "MissionRoutes.cs"));
                AssertContains("_statusTransitions.TransitionAsync(", contents, "Delegates to MissionStatusTransitionService");
                Assert(!contents.Contains("IsValidTransition("), "No local transition validator remains");
                Assert(!contents.Contains("ManualCompletionProofService"), "No local copy of the completion gate remains");
            });

            await RunTest("McpMissionTools status transition delegates to the shared transition service", () =>
            {
                string contents = ReadSource(Path.Combine("src", "Armada.Server", "Mcp", "Tools", "McpMissionTools.cs"));
                AssertContains("statusTransitions.TransitionAsync(mission, newStatus)", contents, "Delegates to MissionStatusTransitionService");
                Assert(!contents.Contains("McpToolHelpers.IsValidTransition("), "No local transition validator remains");
                Assert(!contents.Contains("mission.Status = newStatus"), "No local status write bypasses the completion gates");
            });
        }

        private static string ReadSource(string relativePath)
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src"))
                    && Directory.Exists(Path.Combine(current.FullName, "test")))
                {
                    return File.ReadAllText(Path.Combine(current.FullName, relativePath));
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
        }
    }
}
