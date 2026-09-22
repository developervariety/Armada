namespace Armada.Test.Unit
{
    using System;
    using System.IO;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Pins every mission-transition entry point to one table. <see cref="MissionStateMachine"/> owns the
    /// transition rules; the agent lifecycle handler (through its progress-only agent rule) and the
    /// operator transition service delegate to it, and the REST, WebSocket and MCP transitions delegate to
    /// the operator transition service. A second copy of the rules would give a caller an answer that
    /// depends on which copy it reaches, so these tests assert the PullRequestOpen pairs, the transition
    /// service's agreement with the table across the whole enum, and the delegation of every entry point.
    /// </summary>
    public sealed class MissionTransitionTableAgreementTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Mission Transition Table Agreement";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            // === The PullRequestOpen pairs: the PR-fallback flow must stay legal ===

            await RunTest("State machine allows WorkProduced to PullRequestOpen", () =>
            {
                Assert(
                    MissionStateMachine.IsValidTransition(MissionStatusEnum.WorkProduced, MissionStatusEnum.PullRequestOpen),
                    "WorkProduced to PullRequestOpen is the PR-fallback flow and must be allowed");
            });

            await RunTest("State machine allows PullRequestOpen to Complete", () =>
            {
                Assert(
                    MissionStateMachine.IsValidTransition(MissionStatusEnum.PullRequestOpen, MissionStatusEnum.Complete),
                    "PullRequestOpen to Complete must be allowed");
            });

            await RunTest("State machine allows PullRequestOpen to LandingFailed", () =>
            {
                Assert(
                    MissionStateMachine.IsValidTransition(MissionStatusEnum.PullRequestOpen, MissionStatusEnum.LandingFailed),
                    "PullRequestOpen to LandingFailed must be allowed");
            });

            await RunTest("State machine allows PullRequestOpen to Cancelled", () =>
            {
                Assert(
                    MissionStateMachine.IsValidTransition(MissionStatusEnum.PullRequestOpen, MissionStatusEnum.Cancelled),
                    "PullRequestOpen to Cancelled must be allowed");
            });

            // === Whole-table agreement, so an entry point cannot drift unnoticed ===
            // REST, WebSocket and MCP transitions all reach the operator transition service, so this
            // drives that service across every status pair and compares its verdict with the table.

            await RunTest("Operator transition service agrees with the state machine for every status pair", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MissionStatusTransitionService service = CreateTransitionService(testDb.Driver);
                    MissionStatusEnum[] statuses = (MissionStatusEnum[])Enum.GetValues(typeof(MissionStatusEnum));
                    int compared = 0;

                    foreach (MissionStatusEnum current in statuses)
                    {
                        foreach (MissionStatusEnum target in statuses)
                        {
                            Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("transition " + current + " to " + target)
                            {
                                Status = current
                            }).ConfigureAwait(false);

                            MissionStatusTransitionResult result = await service.TransitionAsync(mission, target).ConfigureAwait(false);
                            bool expected = MissionStateMachine.IsValidTransition(current, target);
                            bool rejectedAsInvalid = result.Outcome == MissionStatusTransitionOutcomeEnum.InvalidTransition;
                            Assert(
                                expected != rejectedAsInvalid,
                                "Transition " + current + " to " + target + ": state machine says " + expected +
                                " but the transition service outcome is " + result.Outcome);

                            if (rejectedAsInvalid)
                            {
                                Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                                Assert(
                                    stored != null && stored.Status == current,
                                    "A rejected transition " + current + " to " + target + " must not change the stored status");
                            }

                            compared++;
                        }
                    }

                    Assert(compared == statuses.Length * statuses.Length, "Every status pair was compared");
                }
            });

            // === Agent status markers: progress targets only, within the shared table ===
            // The lifecycle handler's behaviour is covered by the Agent Lifecycle Handler suite; this pins
            // the rule it calls across the whole enum.

            await RunTest("Agent-reportable transitions are legal progress transitions for every status pair", () =>
            {
                MissionStatusEnum[] statuses = (MissionStatusEnum[])Enum.GetValues(typeof(MissionStatusEnum));
                int allowed = 0;

                foreach (MissionStatusEnum current in statuses)
                {
                    foreach (MissionStatusEnum target in statuses)
                    {
                        bool progressTarget = target == MissionStatusEnum.InProgress
                            || target == MissionStatusEnum.Testing
                            || target == MissionStatusEnum.Review;
                        bool expected = progressTarget && MissionStateMachine.IsValidTransition(current, target);
                        bool actual = MissionStateMachine.IsAgentReportableTransition(current, target);
                        Assert(
                            expected == actual,
                            "Agent transition " + current + " to " + target + ": expected " + expected + " but the rule says " + actual);
                        if (actual)
                        {
                            Assert(
                                !MissionStateMachine.IsTerminalOrPostWork(target),
                                "Agent output must never reach post-work or terminal status " + target);
                            allowed++;
                        }
                    }
                }

                Assert(allowed > 0, "Some progress transition remains reportable by agent output");
            });

            // === Delegation: no entry point keeps its own transition rules ===
            // The validators below are private or live in projects this suite does not reference, so these
            // guards assert that each entry point calls the shared rule rather than restating it.

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
                Assert(!contents.Contains("IsValidTransition("), "No local transition validator remains");
                Assert(!contents.Contains("mission.Status = newStatus"), "No local status write bypasses the completion gates");
            });
        }

        private static MissionStatusTransitionService CreateTransitionService(DatabaseDriver database)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new MissionStatusTransitionService(
                database,
                DispatchProxy.Create<IAdmiralService, UnusedServiceProxy>(),
                DispatchProxy.Create<IMissionService, UnusedServiceProxy>(),
                DispatchProxy.Create<IGitService, UnusedServiceProxy>(),
                // A live captain process refuses every Complete request before the landing and
                // ancestry proofs run, so a legal Complete pair ends as a refusal, never as an
                // invalid transition, without needing a repository.
                (Mission mission, CancellationToken token) => Task.FromResult(true),
                (Mission mission, Dock dock) => Task.CompletedTask,
                (string eventType, string message, string? entityType, string? entityId, string? captainId, string? missionId, string? vesselId, string? voyageId) => Task.CompletedTask,
                logging);
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

        /// <summary>
        /// Stand-in for a collaborator the transition paths under test never call. Any call fails the test.
        /// </summary>
        public class UnusedServiceProxy : DispatchProxy
        {
            /// <inheritdoc />
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                throw new NotSupportedException((targetMethod?.Name ?? "unknown") + " is not used by the transition table tests.");
            }
        }
    }
}
