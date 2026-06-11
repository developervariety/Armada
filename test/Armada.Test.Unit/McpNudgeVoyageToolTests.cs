namespace Armada.Test.Unit
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for the armada_nudge_voyage MCP tool handler: resuming a WaitingForInput
    /// mission with orchestrator notes injection and clean state, queueing replies to
    /// missions in other statuses, and argument/lookup validation errors.
    /// </summary>
    public class McpNudgeVoyageToolTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "MCP Nudge Voyage Tool";

        #region Doubles

        private sealed class NudgeResult
        {
            public string? Status { get; set; }
            public string? MissionId { get; set; }
            public string? MissionStatus { get; set; }
            public string? SignalId { get; set; }
            public string? Error { get; set; }
        }

        #endregion

        private static Func<JsonElement?, Task<object>> CaptureNudgeHandler(TestDatabase testDb)
        {
            Func<JsonElement?, Task<object>>? handler = null;
            McpSignalTools.Register(
                (name, description, schema, h) =>
                {
                    if (String.Equals(name, "armada_nudge_voyage", StringComparison.Ordinal)) handler = h;
                },
                testDb.Driver);
            if (handler == null) throw new InvalidOperationException("armada_nudge_voyage was not registered");
            return handler;
        }

        private static async Task<NudgeResult> InvokeAsync(Func<JsonElement?, Task<object>> handler, string voyageId, string missionId, string message)
        {
            JsonElement args = JsonSerializer.SerializeToElement(new
            {
                voyageId = voyageId,
                missionId = missionId,
                message = message
            });
            object result = await handler(args).ConfigureAwait(false);
            string json = JsonSerializer.Serialize(result);
            return JsonSerializer.Deserialize<NudgeResult>(json)!;
        }

        private static async Task<Mission> CreateMissionAsync(TestDatabase testDb, MissionStatusEnum status)
        {
            Voyage voyage = new Voyage("nudge-voyage", "");
            voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Captain captain = new Captain("nudge-captain");
            captain.Runtime = AgentRuntimeEnum.ClaudeCode;
            captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

            Mission mission = new Mission("Nudge target", "Original mission description body");
            mission.VoyageId = voyage.Id;
            mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

            mission.Status = status;
            mission.CaptainId = captain.Id;
            mission.ProcessId = 1234;
            mission.DockId = "dck_stale";
            mission.AgentOutput = "[ARMADA:NEEDS-INPUT block] Which option?";
            mission.Persona = "Worker";
            await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
            return mission;
        }

        /// <summary>Run nudge voyage tool tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("NudgeVoyage_WaitingForInput_ResumesWithNotesAndCleanState", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureNudgeHandler(testDb);
                    Mission mission = await CreateMissionAsync(testDb, MissionStatusEnum.WaitingForInput).ConfigureAwait(false);

                    NudgeResult result = await InvokeAsync(handler, mission.VoyageId!, mission.Id, "Use the blue configuration.").ConfigureAwait(false);

                    AssertEqual("resumed", result.Status, "Reply to a WaitingForInput mission should report resumed");
                    AssertEqual(mission.Id, result.MissionId, "Result should echo the mission id");

                    Mission? resumed = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, resumed!.Status, "Resumed mission should be Pending");
                    AssertStartsWith("[ORCHESTRATOR NOTES]\nUse the blue configuration.\n\n", resumed.Description!, "Notes should be prepended verbatim above the description");
                    AssertContains("Original mission description body", resumed.Description!, "Original description should be preserved below the notes");
                    AssertNull(resumed.CaptainId, "Stale captain id should be cleared");
                    AssertNull(resumed.ProcessId, "Stale process id should be cleared");
                    AssertNull(resumed.DockId, "Stale dock id should be cleared");
                    AssertNull(resumed.AgentOutput, "Stale agent output should be cleared");
                    AssertEqual("Worker", resumed.Persona, "Persona should remain intact for reassignment");

                    AssertNotNull(result.SignalId, "Result should include the reply signal id");
                    Signal? signal = await testDb.Driver.Signals.ReadAsync(result.SignalId!).ConfigureAwait(false);
                    AssertNotNull(signal, "Reply signal should be persisted");
                    AssertEqual(SignalTypeEnum.Mail, signal!.Type, "Reply signal should be a Mail signal");
                    AssertTrue(signal.Read, "Reply signal consumed by the resume should be marked read");
                }
            });

            await RunTest("NudgeVoyage_NonWaitingMission_QueuesUnreadMailSignal", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureNudgeHandler(testDb);
                    Mission mission = await CreateMissionAsync(testDb, MissionStatusEnum.InProgress).ConfigureAwait(false);

                    NudgeResult result = await InvokeAsync(handler, mission.VoyageId!, mission.Id, "FYI: deadline moved.").ConfigureAwait(false);

                    AssertEqual("queued", result.Status, "Reply to a non-waiting mission should report queued");

                    Mission? unchanged = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.InProgress, unchanged!.Status, "Mission status should be unchanged");
                    AssertEqual("Original mission description body", unchanged.Description, "Description should not gain orchestrator notes");
                    AssertNotNull(unchanged.AgentOutput, "Agent output should not be cleared for queued replies");

                    Signal? signal = await testDb.Driver.Signals.ReadAsync(result.SignalId!).ConfigureAwait(false);
                    AssertNotNull(signal, "Reply signal should be persisted");
                    AssertFalse(signal!.Read, "Queued reply signal should remain unread");
                    AssertEqual(mission.CaptainId, signal.ToCaptainId, "Queued reply should be addressed to the mission's captain");
                }
            });

            await RunTest("NudgeVoyage_MissionNotFound_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureNudgeHandler(testDb);

                    NudgeResult result = await InvokeAsync(handler, "vyg_missing", "msn_missing", "hello").ConfigureAwait(false);

                    AssertNotNull(result.Error, "Unknown mission should return an error");
                    AssertContains("mission not found", result.Error!, "Error should identify the missing mission");
                }
            });

            await RunTest("NudgeVoyage_VoyageMismatch_ReturnsError_MissionUntouched", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureNudgeHandler(testDb);
                    Mission mission = await CreateMissionAsync(testDb, MissionStatusEnum.WaitingForInput).ConfigureAwait(false);

                    NudgeResult result = await InvokeAsync(handler, "vyg_other_voyage", mission.Id, "wrong voyage").ConfigureAwait(false);

                    AssertNotNull(result.Error, "Voyage mismatch should return an error");
                    AssertContains("does not belong", result.Error!, "Error should describe the mismatch");

                    Mission? untouched = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.WaitingForInput, untouched!.Status, "Mismatched reply must not resume the mission");
                    AssertEqual("Original mission description body", untouched.Description, "Mismatched reply must not inject notes");
                }
            });

            await RunTest("NudgeVoyage_MissingArguments_ReturnErrors", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Func<JsonElement?, Task<object>> handler = CaptureNudgeHandler(testDb);

                    NudgeResult noVoyage = await InvokeAsync(handler, "", "msn_x", "msg").ConfigureAwait(false);
                    AssertNotNull(noVoyage.Error, "Empty voyageId should return an error");
                    AssertContains("voyageId", noVoyage.Error!, "Error should name voyageId");

                    NudgeResult noMission = await InvokeAsync(handler, "vyg_x", "", "msg").ConfigureAwait(false);
                    AssertNotNull(noMission.Error, "Empty missionId should return an error");
                    AssertContains("missionId", noMission.Error!, "Error should name missionId");

                    NudgeResult noMessage = await InvokeAsync(handler, "vyg_x", "msn_x", "").ConfigureAwait(false);
                    AssertNotNull(noMessage.Error, "Empty message should return an error");
                    AssertContains("message", noMessage.Error!, "Error should name message");
                }
            });
        }
    }
}
