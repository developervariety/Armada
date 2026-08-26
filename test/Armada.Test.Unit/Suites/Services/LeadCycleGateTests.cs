namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests the two gates that produced duplicate lead handoffs: the board room alias
    /// (a "default" key must reach the shared room) and the lead-cycle completion gate
    /// (it posts the handoff itself instead of refusing), plus the operator-presence gate
    /// that keeps the unattended lead quiet while a session is active.
    /// </summary>
    public class LeadCycleGateTests : TestSuite
    {
        private static readonly JsonSerializerOptions _JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <inheritdoc />
        public override string Name => "LeadCycleGate";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Room key alias resolves default, casing and blanks to the shared room", () =>
            {
                AssertEqual(CoordinationRoom.DefaultKey, CoordinationRoom.NormalizeKey(null), "null is the shared room");
                AssertEqual(CoordinationRoom.DefaultKey, CoordinationRoom.NormalizeKey("  "), "blank is the shared room");
                AssertEqual(CoordinationRoom.DefaultKey, CoordinationRoom.NormalizeKey("default"), "default is the shared room");
                AssertEqual(CoordinationRoom.DefaultKey, CoordinationRoom.NormalizeKey(" DEFAULT "), "default in any casing is the shared room");
                AssertEqual(CoordinationRoom.DefaultKey, CoordinationRoom.NormalizeKey("Fleet"), "the shared key in any casing is the shared room");
                AssertEqual("ops", CoordinationRoom.NormalizeKey(" ops "), "another key is trimmed and kept");
                return Task.CompletedTask;
            });

            await RunTest("A note posted to room default lands on the shared board and creates no second room", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CoordinationService coordination = new CoordinationService(new LoggingModule { Settings = { EnableConsole = false } }, testDb.Driver);
                    await coordination.PostMessageAsync("default", CoordinationAuthorTypeEnum.Operator, "example-operator", "example-operator", "hello from default").ConfigureAwait(false);
                    await coordination.PostMessageAsync("fleet", CoordinationAuthorTypeEnum.Operator, "example-operator", "example-operator", "hello from fleet").ConfigureAwait(false);

                    List<CoordinationMessage> messages = await coordination.ReadMessagesAsync(CoordinationService.DefaultRoomKey).ConfigureAwait(false);
                    AssertEqual(2, messages.Count, "both notes are on the one shared board");
                    List<CoordinationRoom> rooms = await coordination.EnumerateRoomsAsync().ConfigureAwait(false);
                    AssertEqual(1, rooms.Count, "no second room was created for the alias");
                    AssertEqual(CoordinationRoom.DefaultKey, rooms[0].Key, "the one room is the shared room");
                }
            });

            await RunTest("HandoffMatches tolerates whitespace and the cycle prefix and rejects other text", () =>
            {
                string handoff = "LEAD HANDOFF — roster: 9 idle, 1 working.\nRemaining: none.";
                AssertTrue(McpLeadCycleTools.HandoffMatches(handoff, handoff), "identical text matches");
                AssertTrue(McpLeadCycleTools.HandoffMatches("LEAD HANDOFF — roster: 9 idle,  1 working. Remaining:   none.", handoff), "whitespace runs do not matter");
                AssertTrue(McpLeadCycleTools.HandoffMatches("[ARMADA:LEAD-HANDOFF] Cycle lcy_example: " + handoff, handoff), "the cycle prefix is accepted");
                AssertFalse(McpLeadCycleTools.HandoffMatches("LEAD HANDOFF — roster: 8 idle, 2 working.", handoff), "different text does not match");
                AssertFalse(McpLeadCycleTools.HandoffMatches("", handoff), "an empty note does not match");
                AssertFalse(McpLeadCycleTools.HandoffMatches(handoff, ""), "an empty handoff matches nothing");
                return Task.CompletedTask;
            });

            await RunTest("PresentOperatorKeys ignores the lead, its helpers, stale sessions and a disabled window", () =>
            {
                DateTime now = new DateTime(2026, 8, 26, 1, 0, 0, DateTimeKind.Utc);
                List<CoordinationParticipant> participants = new List<CoordinationParticipant>
                {
                    new CoordinationParticipant { ParticipantKey = "example-lead", LastSeenUtc = now },
                    new CoordinationParticipant { ParticipantKey = "helper-abc123", LastSeenUtc = now },
                    new CoordinationParticipant { ParticipantKey = "example-operator", LastSeenUtc = now.AddMinutes(-5) },
                    new CoordinationParticipant { ParticipantKey = "stale-operator", LastSeenUtc = now.AddHours(-3) },
                    new CoordinationParticipant { ParticipantKey = "dashboard-xyz", LastSeenUtc = now.AddMinutes(-29) }
                };
                List<string> present = LeadCycleCoordinator.PresentOperatorKeys(participants, "example-lead", now, 30);
                AssertEqual(2, present.Count, "two live operators");
                AssertEqual("dashboard-xyz", present[0], "sorted, dashboard first");
                AssertEqual("example-operator", present[1], "sorted, operator second");
                AssertEqual(0, LeadCycleCoordinator.PresentOperatorKeys(participants, "example-lead", now, 0).Count, "a zero window selects nobody");
                AssertEqual(0, LeadCycleCoordinator.PresentOperatorKeys(participants, "example-lead", now, 3).Count, "a short window sees only the lead and its helper");
                return Task.CompletedTask;
            });

            await RunTest("Begin refuses operator-present while a session heartbeats and acquires once the window is off", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CoordinationService coordination = new CoordinationService(new LoggingModule { Settings = { EnableConsole = false } }, testDb.Driver);
                    await coordination.HeartbeatAsync(CoordinationService.DefaultRoomKey, "example-operator", "Example operator").ConfigureAwait(false);
                    await coordination.HeartbeatAsync(CoordinationService.DefaultRoomKey, "helper-123", "Helper").ConfigureAwait(false);
                    await coordination.HeartbeatAsync(CoordinationService.DefaultRoomKey, "armada-lead", "Lead").ConfigureAwait(false);

                    GrokLeadSettings settings = new GrokLeadSettings();
                    LeadCycleCoordinator coordinator = new LeadCycleCoordinator(testDb.Driver, settings);
                    LeadCycleStartResult refused = await coordinator.TryBeginAsync(LeadRunnerTypeEnum.Legacy, "armada-lead").ConfigureAwait(false);
                    AssertFalse(refused.Acquired, "an operator heartbeat within the window refuses the cycle");
                    AssertTrue(refused.RefusalReason != null && refused.RefusalReason.StartsWith(LeadCycleCoordinator.OperatorPresentRefusalPrefix), "the reason names operator-present: " + refused.RefusalReason);
                    AssertTrue(refused.RefusalReason!.Contains("example-operator"), "the reason names the operator");
                    AssertFalse(refused.RefusalReason!.Contains("helper-123"), "the helper is not an operator");

                    settings.OperatorPresenceMinutes = 0;
                    LeadCycleStartResult acquired = await coordinator.TryBeginAsync(LeadRunnerTypeEnum.Legacy, "armada-lead").ConfigureAwait(false);
                    AssertTrue(acquired.Acquired, "with the gate off the cycle starts: " + acquired.RefusalReason);
                }
            });

            await RunTest("Begin acquires when only the lead and its helper are present", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CoordinationService coordination = new CoordinationService(new LoggingModule { Settings = { EnableConsole = false } }, testDb.Driver);
                    await coordination.HeartbeatAsync(CoordinationService.DefaultRoomKey, "armada-lead", "Lead").ConfigureAwait(false);
                    await coordination.HeartbeatAsync(CoordinationService.DefaultRoomKey, "helper-456", "Helper").ConfigureAwait(false);
                    LeadCycleCoordinator coordinator = new LeadCycleCoordinator(testDb.Driver, new GrokLeadSettings());
                    LeadCycleStartResult result = await coordinator.TryBeginAsync(LeadRunnerTypeEnum.Legacy, "armada-lead").ConfigureAwait(false);
                    AssertTrue(result.Acquired, "the lead's own presence never blocks it: " + result.RefusalReason);
                }
            });

            await RunTest("Complete posts the handoff itself once and accepts an already-posted one", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CoordinationService coordination = new CoordinationService(new LoggingModule { Settings = { EnableConsole = false } }, testDb.Driver);
                    GrokLeadSettings settings = new GrokLeadSettings { OperatorPresenceMinutes = 0 };
                    LeadCycleCoordinator coordinator = new LeadCycleCoordinator(testDb.Driver, settings);

                    Func<JsonElement?, Task<object>>? complete = null;
                    McpLeadCycleTools.Register(
                        (name, _, _, h) => { if (name == "armada_lead_cycle_complete") complete = h; },
                        testDb.Driver,
                        coordination,
                        coordinator,
                        LeadRunnerTypeEnum.Legacy,
                        "example-lead");
                    AssertNotNull(complete, "armada_lead_cycle_complete handler must be registered");

                    // Cycle one: nothing posted; the gate posts and completes.
                    LeadCycleStartResult first = await coordinator.TryBeginAsync(LeadRunnerTypeEnum.Legacy, "example-lead").ConfigureAwait(false);
                    AssertTrue(first.Acquired, "first cycle starts: " + first.RefusalReason);
                    string handoff = "LEAD HANDOFF — roster: 9 idle. Remaining: none.";
                    string firstResult = JsonSerializer.Serialize(await complete!(JsonSerializer.SerializeToElement(new { cycleId = first.CycleId, handoff }, _JsonOpts)).ConfigureAwait(false));
                    AssertContains("\"Completed\":true", firstResult);
                    AssertContains("posted_by_gate", firstResult);
                    List<CoordinationMessage> board = await coordination.ReadMessagesAsync(CoordinationService.DefaultRoomKey).ConfigureAwait(false);
                    AssertEqual(1, board.Count(m => m.AuthorId == "example-lead"), "exactly one handoff on the board");
                    AssertEqual(handoff, board.First(m => m.AuthorId == "example-lead").Content, "the gate posted the handoff verbatim");

                    // Cycle two: the lead posted it already (with different whitespace); the gate does not post again.
                    LeadCycleStartResult second = await coordinator.TryBeginAsync(LeadRunnerTypeEnum.Legacy, "example-lead").ConfigureAwait(false);
                    AssertTrue(second.Acquired, "second cycle starts: " + second.RefusalReason);
                    string secondHandoff = "LEAD HANDOFF — roster: 8 idle.\nRemaining: one voyage.";
                    await coordination.PostMessageAsync(CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.Operator, "example-lead", "example-lead", "LEAD HANDOFF — roster: 8 idle.   Remaining: one voyage.").ConfigureAwait(false);
                    string secondResult = JsonSerializer.Serialize(await complete!(JsonSerializer.SerializeToElement(new { cycleId = second.CycleId, handoff = secondHandoff }, _JsonOpts)).ConfigureAwait(false));
                    AssertContains("\"Completed\":true", secondResult);
                    AssertContains("already_on_board", secondResult);
                    board = await coordination.ReadMessagesAsync(CoordinationService.DefaultRoomKey).ConfigureAwait(false);
                    AssertEqual(2, board.Count(m => m.AuthorId == "example-lead"), "no duplicate was posted");

                    // An empty handoff is refused, and nothing is posted.
                    LeadCycleStartResult third = await coordinator.TryBeginAsync(LeadRunnerTypeEnum.Legacy, "example-lead").ConfigureAwait(false);
                    string emptyResult = JsonSerializer.Serialize(await complete!(JsonSerializer.SerializeToElement(new { cycleId = third.CycleId, handoff = "  " }, _JsonOpts)).ConfigureAwait(false));
                    AssertContains("A handoff is required", emptyResult);
                }
            });
        }
    }
}
