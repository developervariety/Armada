namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Deleting a vessel is one operation on REST, WebSocket and MCP: the captain of every mission still running on it
    /// is recalled, so no agent keeps writing into a deleted vessel's dock; every mission of the vessel is deleted,
    /// including one waiting for review; its docks are purged through the dock service; and every surface writes the
    /// same event.
    /// </summary>
    public class VesselDeleteParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Vessel Delete Parity";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("DeleteVessel_RecallsRunningCaptainsAndDeletesEveryMissionOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in new[] { "REST", "WebSocket", "MCP" })
                    {
                        Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("vessel-delete-" + surface.ToLowerInvariant(), "https://github.com/test/vessel-delete.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);
                        Captain captain = await harness.Driver.Captains.CreateAsync(new Captain("vessel-delete-captain-" + surface) { State = CaptainStateEnum.Working, ProcessId = 7001 }).ConfigureAwait(false);
                        Mission running = await harness.Driver.Missions.CreateAsync(new Mission("running " + surface)
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            VesselId = vessel.Id,
                            CaptainId = captain.Id,
                            Status = MissionStatusEnum.InProgress
                        }).ConfigureAwait(false);
                        captain.CurrentMissionId = running.Id;
                        await harness.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
                        Mission review = await harness.Driver.Missions.CreateAsync(new Mission("review " + surface)
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            VesselId = vessel.Id,
                            Status = MissionStatusEnum.Review
                        }).ConfigureAwait(false);
                        string worktree = harness.MakeDirectory(Path.Combine("vessel-docks", surface));
                        Dock dock = await harness.Driver.Docks.CreateAsync(new Dock(vessel.Id) { WorktreePath = worktree, Active = false }).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = surface switch
                        {
                            "REST" => await harness.RestAsync(HttpMethod.Delete, "/api/v1/vessels/" + vessel.Id).ConfigureAwait(false),
                            "WebSocket" => await harness.WebSocketAsync("delete_vessel", vessel.Id).ConfigureAwait(false),
                            _ => await harness.McpAsync("armada_delete_vessel", new { vesselId = vessel.Id }).ConfigureAwait(false)
                        };

                        AssertFalse(reply.Refused, surface + ": the vessel is deleted: " + reply);
                        AssertNull(await harness.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false), surface + ": the vessel row is deleted");
                        AssertNull(await harness.Driver.Missions.ReadAsync(running.Id).ConfigureAwait(false), surface + ": the running mission is deleted");
                        AssertNull(await harness.Driver.Missions.ReadAsync(review.Id).ConfigureAwait(false), surface + ": the mission waiting for review is deleted");
                        List<string> recalled = harness.Admiral.Recalled.ToList();
                        AssertTrue(recalled.Contains(captain.Id), surface + ": the running mission's captain is recalled, which stops its process: " + String.Join(",", recalled));
                        AssertNull(await harness.Driver.Docks.ReadAsync(dock.Id).ConfigureAwait(false), surface + ": the dock record is purged");
                        AssertFalse(Directory.Exists(worktree), surface + ": the dock worktree is removed");
                        AssertEqual(1, harness.EventTypes().Count(type => type == "vessel.deleted:" + vessel.Id),
                            surface + ": one vessel.deleted event: " + String.Join(",", harness.EventTypes()));
                    }
                }
            }).ConfigureAwait(false);
        }
    }
}
