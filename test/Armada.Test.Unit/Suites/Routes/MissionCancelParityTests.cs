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
    /// Cancelling one mission is one operation on REST, WebSocket and MCP: a finished mission is refused and keeps its
    /// outcome; a running mission's captain is recalled once, so its agent process stops and the captain is released;
    /// a stage waiting on the cancelled mission is cancelled with it; and every surface writes the same event and
    /// broadcasts the same change.
    /// </summary>
    public class MissionCancelParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Cancel Parity";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("CancelMission_CompleteMission_IsRefusedAndKeepsItsOutcomeOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        DateTime finished = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                        Mission mission = await harness.Driver.Missions.CreateAsync(new Mission("finished " + surface)
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            Status = MissionStatusEnum.Complete,
                            CompletedUtc = finished
                        }).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = await CancelAsync(harness, surface, mission.Id).ConfigureAwait(false);

                        AssertTrue(reply.Refused, "a Complete mission cannot be cancelled: " + reply);
                        Mission? stored = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Complete, stored!.Status, surface + ": the mission keeps its outcome");
                        AssertEqual(finished, stored.CompletedUtc!.Value.ToUniversalTime(), surface + ": the completion time is not rewritten");
                        AssertEqual(0, harness.Events.Count, surface + ": a refused cancel writes no event: " + String.Join(",", harness.EventTypes()));
                        AssertEqual(0, harness.Broadcasts.Count, surface + ": a refused cancel broadcasts nothing");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("CancelMission_RunningMission_RecallsItsCaptainOnceOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Captain captain = await harness.Driver.Captains.CreateAsync(new Captain("cancel-" + surface)
                        {
                            State = CaptainStateEnum.Working,
                            ProcessId = 4242
                        }).ConfigureAwait(false);
                        Mission mission = await harness.Driver.Missions.CreateAsync(new Mission("running " + surface)
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            Status = MissionStatusEnum.InProgress,
                            CaptainId = captain.Id,
                            ProcessId = 4242
                        }).ConfigureAwait(false);
                        captain.CurrentMissionId = mission.Id;
                        await harness.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = await CancelAsync(harness, surface, mission.Id).ConfigureAwait(false);

                        AssertFalse(reply.Refused, "a running mission can be cancelled: " + reply);
                        Mission? stored = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Cancelled, stored!.Status, surface + ": the mission is Cancelled");
                        AssertNull(stored.ProcessId, surface + ": the mission no longer names a process");
                        List<string> recalled = harness.Admiral.Recalled.ToList();
                        AssertEqual(1, recalled.Count, surface + ": the captain is recalled exactly once, which stops its process: " + String.Join(",", recalled));
                        AssertEqual(captain.Id, recalled[0], surface + ": the mission's own captain is recalled");
                        Captain? storedCaptain = await harness.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                        AssertEqual(CaptainStateEnum.Idle, storedCaptain!.State, surface + ": the captain is released");
                        AssertEqual(1, harness.EventTypes().Count(type => type == "mission.cancelled:" + mission.Id),
                            surface + ": one mission.cancelled event: " + String.Join(",", harness.EventTypes()));
                        AssertTrue(harness.Broadcasts.Contains("mission:" + mission.Id + ":Cancelled"),
                            surface + ": the change is broadcast: " + String.Join(",", harness.Broadcasts));
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("CancelMission_WithAWaitingDependentStage_CancelsTheStageOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Voyage voyage = await harness.Driver.Voyages.CreateAsync(new Voyage("chain " + surface)
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            Status = VoyageStatusEnum.InProgress
                        }).ConfigureAwait(false);
                        Mission upstream = await harness.Driver.Missions.CreateAsync(new Mission("upstream " + surface)
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            VoyageId = voyage.Id,
                            Status = MissionStatusEnum.Pending
                        }).ConfigureAwait(false);
                        Mission downstream = await harness.Driver.Missions.CreateAsync(new Mission("downstream " + surface)
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            VoyageId = voyage.Id,
                            DependsOnMissionId = upstream.Id,
                            Status = MissionStatusEnum.Pending
                        }).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = await CancelAsync(harness, surface, upstream.Id).ConfigureAwait(false);

                        AssertFalse(reply.Refused, "a Pending mission can be cancelled: " + reply);
                        Mission? storedDownstream = await harness.Driver.Missions.ReadAsync(downstream.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Cancelled, storedDownstream!.Status,
                            surface + ": a stage waiting on the cancelled mission can never run, so it is cancelled with it");
                        AssertTrue(harness.Broadcasts.Contains("mission:" + downstream.Id + ":Cancelled"),
                            surface + ": the dependent change is broadcast: " + String.Join(",", harness.Broadcasts));
                    }
                }
            }).ConfigureAwait(false);
        }

        private static readonly string[] Surfaces = new[] { "REST", "WebSocket", "MCP" };

        private static Task<SurfaceReply> CancelAsync(SurfaceParityHarness harness, string surface, string missionId)
        {
            switch (surface)
            {
                case "REST": return harness.RestAsync(HttpMethod.Delete, "/api/v1/missions/" + missionId);
                case "WebSocket": return harness.WebSocketAsync("cancel_mission", missionId);
                default: return harness.McpAsync("armada_cancel_mission", new { missionId = missionId });
            }
        }
    }
}
