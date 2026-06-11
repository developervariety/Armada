namespace Armada.Test.Unit
{
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for the WaitingForInput mission status foundation: enum identity,
    /// JSON round-trip, SQLite persistence, transition validation, and the
    /// MaxMissionInputBlocks hard-block cap setting.
    /// </summary>
    public sealed class MissionWaitingForInputStatusTests : TestSuite
    {
        public override string Name => "Mission WaitingForInput Status";

        protected override async Task RunTestsAsync()
        {
            // === Enum identity ===

            await RunTest("WaitingForInput enum value exists and is distinct from Review", () =>
            {
                MissionStatusEnum status = MissionStatusEnum.WaitingForInput;
                AssertEqual("WaitingForInput", status.ToString(), "WaitingForInput enum name");
                Assert(status != MissionStatusEnum.Review, "WaitingForInput is distinct from Review");
                Assert(status != MissionStatusEnum.Pending, "WaitingForInput is distinct from Pending");
            });

            await RunTest("MissionAwaitingInput escalation trigger exists", () =>
            {
                EscalationTriggerEnum trigger = EscalationTriggerEnum.MissionAwaitingInput;
                AssertEqual("MissionAwaitingInput", trigger.ToString(), "MissionAwaitingInput enum name");
            });

            // === JSON round-trip ===

            await RunTest("WaitingForInput SerializesAsString", () =>
            {
                string json = JsonSerializer.Serialize(MissionStatusEnum.WaitingForInput);
                AssertEqual("\"WaitingForInput\"", json, "Serialized JSON");
                MissionStatusEnum deserialized = JsonSerializer.Deserialize<MissionStatusEnum>(json);
                AssertEqual(MissionStatusEnum.WaitingForInput, deserialized, "Round-tripped value");
            });

            // === SQLite persistence round-trip ===

            await RunTest("SQLite mission round-trips WaitingForInput status", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Mission mission = new Mission("waiting-for-input-mission");
                    mission.Status = MissionStatusEnum.WaitingForInput;
                    await testDb.Driver.Missions.CreateAsync(mission);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id);
                    AssertNotNull(readBack, "Mission should exist after create");
                    AssertEqual(MissionStatusEnum.WaitingForInput, readBack!.Status, "Persisted status");
                }
            });

            // === Transition validation (McpToolHelpers shared validator) ===

            await RunTest("InProgress to WaitingForInput is valid", () =>
            {
                AssertTrue(
                    McpToolHelpers.IsValidTransition(MissionStatusEnum.InProgress, MissionStatusEnum.WaitingForInput),
                    "InProgress -> WaitingForInput");
            });

            await RunTest("WaitingForInput to Pending is valid", () =>
            {
                AssertTrue(
                    McpToolHelpers.IsValidTransition(MissionStatusEnum.WaitingForInput, MissionStatusEnum.Pending),
                    "WaitingForInput -> Pending");
            });

            await RunTest("WaitingForInput to Failed is valid", () =>
            {
                AssertTrue(
                    McpToolHelpers.IsValidTransition(MissionStatusEnum.WaitingForInput, MissionStatusEnum.Failed),
                    "WaitingForInput -> Failed");
            });

            await RunTest("WaitingForInput to Cancelled is valid", () =>
            {
                AssertTrue(
                    McpToolHelpers.IsValidTransition(MissionStatusEnum.WaitingForInput, MissionStatusEnum.Cancelled),
                    "WaitingForInput -> Cancelled");
            });

            await RunTest("WaitingForInput to Complete remains invalid", () =>
            {
                AssertFalse(
                    McpToolHelpers.IsValidTransition(MissionStatusEnum.WaitingForInput, MissionStatusEnum.Complete),
                    "WaitingForInput -> Complete must stay invalid");
            });

            await RunTest("Pending to WaitingForInput remains invalid", () =>
            {
                AssertFalse(
                    McpToolHelpers.IsValidTransition(MissionStatusEnum.Pending, MissionStatusEnum.WaitingForInput),
                    "Pending -> WaitingForInput must stay invalid");
            });

            // === MaxMissionInputBlocks setting ===

            await RunTest("MaxMissionInputBlocks defaults to 3", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertEqual(3, settings.MaxMissionInputBlocks, "Default cap");
            });

            await RunTest("MaxMissionInputBlocks accepts zero and positive values", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                settings.MaxMissionInputBlocks = 0;
                AssertEqual(0, settings.MaxMissionInputBlocks, "Zero disables input blocks");
                settings.MaxMissionInputBlocks = 10;
                AssertEqual(10, settings.MaxMissionInputBlocks, "Positive value accepted");
            });

            await RunTest("MaxMissionInputBlocks rejects negative values", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertThrows<ArgumentOutOfRangeException>(() =>
                {
                    settings.MaxMissionInputBlocks = -1;
                }, "Negative cap must be rejected");
            });
        }
    }
}
