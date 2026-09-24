namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Linq;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Cancelling a voyage reports the same change on REST, WebSocket and MCP: the running captain is recalled, one
    /// voyage.cancelled event is written, and the voyage and each cancelled mission are broadcast.
    /// </summary>
    public class VoyageCancelParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Voyage Cancel Parity";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("CancelVoyage_WritesOneEventAndBroadcastsOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in new[] { "REST", "WebSocket", "MCP" })
                    {
                        Voyage voyage = await harness.Driver.Voyages.CreateAsync(new Voyage("cancel " + surface)
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            Status = VoyageStatusEnum.InProgress
                        }).ConfigureAwait(false);
                        Captain captain = await harness.Driver.Captains.CreateAsync(new Captain("voyage-cancel-" + surface) { State = CaptainStateEnum.Working }).ConfigureAwait(false);
                        Mission running = await harness.Driver.Missions.CreateAsync(new Mission("running " + surface)
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            VoyageId = voyage.Id,
                            CaptainId = captain.Id,
                            Status = MissionStatusEnum.InProgress
                        }).ConfigureAwait(false);
                        Mission pending = await harness.Driver.Missions.CreateAsync(new Mission("pending " + surface)
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            VoyageId = voyage.Id,
                            Status = MissionStatusEnum.Pending
                        }).ConfigureAwait(false);
                        captain.CurrentMissionId = running.Id;
                        await harness.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = surface switch
                        {
                            "REST" => await harness.RestAsync(HttpMethod.Delete, "/api/v1/voyages/" + voyage.Id).ConfigureAwait(false),
                            "WebSocket" => await harness.WebSocketAsync("cancel_voyage", voyage.Id).ConfigureAwait(false),
                            _ => await harness.McpAsync("armada_cancel_voyage", new { voyageId = voyage.Id }).ConfigureAwait(false)
                        };

                        AssertFalse(reply.Refused, surface + ": the voyage is cancelled: " + reply);
                        Voyage? stored = await harness.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                        AssertEqual(VoyageStatusEnum.Cancelled, stored!.Status, surface + ": the voyage is Cancelled");
                        AssertTrue(harness.Admiral.Recalled.Contains(captain.Id), surface + ": the running captain is recalled");
                        AssertEqual(1, harness.EventTypes().Count(type => type == "voyage.cancelled:" + voyage.Id),
                            surface + ": one voyage.cancelled event: " + String.Join(",", harness.EventTypes()));
                        AssertTrue(harness.Broadcasts.Contains("voyage:" + voyage.Id + ":Cancelled"),
                            surface + ": the voyage change is broadcast: " + String.Join(",", harness.Broadcasts));
                        AssertTrue(harness.Broadcasts.Contains("mission:" + pending.Id + ":Cancelled"),
                            surface + ": each cancelled mission is broadcast: " + String.Join(",", harness.Broadcasts));
                    }
                }
            }).ConfigureAwait(false);
        }
    }
}
