namespace Armada.Test.Unit
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;

    /// <summary>
    /// Pins every mission-transition entry point to one table. The transition rules were copied into
    /// the MCP helper and the agent lifecycle handler beside the authoritative table in
    /// <see cref="MissionStateMachine"/>, and the copies drifted apart: one omitted every
    /// PullRequestOpen transition, another carried WaitingForInput transitions the table lacked. A
    /// caller's answer then depended on which copy it happened to reach, so these tests assert the
    /// specific pairs that drifted and then assert agreement across the whole enum.
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

            // === The WaitingForInput gap: absent from the authoritative table ===

            await RunTest("State machine allows InProgress to WaitingForInput", () =>
            {
                Assert(
                    MissionStateMachine.IsValidTransition(MissionStatusEnum.InProgress, MissionStatusEnum.WaitingForInput),
                    "A running mission must be able to block on input");
            });

            await RunTest("State machine treats WaitingForInput as non-terminal", () =>
            {
                Assert(
                    MissionStateMachine.IsValidTransition(MissionStatusEnum.WaitingForInput, MissionStatusEnum.Pending),
                    "A blocked mission returns to Pending to be dispatched again");
                Assert(
                    MissionStateMachine.IsValidTransition(MissionStatusEnum.WaitingForInput, MissionStatusEnum.Failed),
                    "A blocked mission may fail");
                Assert(
                    MissionStateMachine.IsValidTransition(MissionStatusEnum.WaitingForInput, MissionStatusEnum.Cancelled),
                    "A blocked mission may be cancelled");
            });

            await RunTest("State machine still rejects WaitingForInput to Complete", () =>
            {
                Assert(
                    !MissionStateMachine.IsValidTransition(MissionStatusEnum.WaitingForInput, MissionStatusEnum.Complete),
                    "A blocked mission must not complete without running again");
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
        }
    }
}
