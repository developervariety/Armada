namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Restarting a mission is one operation on REST, WebSocket and MCP. A LandingFailed mission keeps its produced
    /// work: every surface refuses to restart it and names retry-landing. A Failed mission returns to Pending, and every
    /// surface writes the same event, broadcasts the same change, and records a restart signal its owner can read.
    /// </summary>
    public class MissionRestartParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Restart Parity";

        private static readonly string[] Surfaces = new[] { "REST", "WebSocket", "MCP" };

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("RestartMission_LandingFailedMission_IsRefusedAndNamesRetryLandingOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Mission mission = await SeedAsync(harness, surface, MissionStatusEnum.LandingFailed).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = await RestartAsync(harness, surface, mission.Id).ConfigureAwait(false);

                        AssertTrue(reply.Refused, "a LandingFailed mission is not restarted: " + reply);
                        AssertContains("retry-landing", reply.Body, surface + ": the refusal names retry-landing: " + reply);
                        Mission? stored = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.LandingFailed, stored!.Status, surface + ": the mission keeps its status");
                        AssertEqual("armada/restart-" + surface, stored.BranchName, surface + ": the produced branch is kept");
                        AssertEqual(0, harness.Events.Count, surface + ": a refused restart writes no event");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("RestartMission_FailedMission_ReturnsToPendingWithOneEventBroadcastAndOwnedSignalOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Mission mission = await SeedAsync(harness, surface, MissionStatusEnum.Failed).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = await RestartAsync(harness, surface, mission.Id).ConfigureAwait(false);

                        AssertFalse(reply.Refused, "a Failed mission is restarted: " + reply);
                        Mission? stored = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Pending, stored!.Status, surface + ": the mission is Pending");
                        AssertEqual(1, harness.EventTypes().Count(type => type == "mission.restarted:" + mission.Id),
                            surface + ": one mission.restarted event: " + String.Join(",", harness.EventTypes()));
                        AssertTrue(harness.Broadcasts.Contains("mission:" + mission.Id + ":Pending"),
                            surface + ": the change is broadcast: " + String.Join(",", harness.Broadcasts));
                        List<Signal> signals = await harness.Driver.Signals.EnumerateRecentAsync(200).ConfigureAwait(false);
                        Signal? restartSignal = signals.FirstOrDefault(s => s.Payload == "Mission " + mission.Id + " restarted");
                        AssertNotNull(restartSignal, surface + ": a restart signal is recorded");
                        AssertEqual(mission.TenantId, restartSignal!.TenantId, surface + ": the signal belongs to the mission's tenant");
                        AssertEqual(mission.UserId, restartSignal.UserId, surface + ": the signal belongs to the mission's owner");
                    }
                }
            }).ConfigureAwait(false);
        }

        private static async Task<Mission> SeedAsync(SurfaceParityHarness harness, string surface, MissionStatusEnum status)
        {
            Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("restart-vessel-" + surface + "-" + status, "https://github.com/test/restart.git")
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId
            }).ConfigureAwait(false);
            return await harness.Driver.Missions.CreateAsync(new Mission("restart " + surface)
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId,
                VesselId = vessel.Id,
                BranchName = "armada/restart-" + surface,
                CommitHash = "0123456789abcdef0123456789abcdef01234567",
                Status = status,
                CompletedUtc = DateTime.UtcNow
            }).ConfigureAwait(false);
        }

        private static Task<SurfaceReply> RestartAsync(SurfaceParityHarness harness, string surface, string missionId)
        {
            switch (surface)
            {
                case "REST": return harness.RestAsync(HttpMethod.Post, "/api/v1/missions/" + missionId + "/restart", new { });
                case "WebSocket": return harness.WebSocketAsync("restart_mission", missionId, new { });
                default: return harness.McpAsync("armada_restart_mission", new { missionId = missionId });
            }
        }
    }
}
