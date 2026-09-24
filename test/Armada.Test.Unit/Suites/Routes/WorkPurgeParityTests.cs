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
    /// Purging a mission or a voyage is one operation on REST, WebSocket and MCP. A mission a captain is working is
    /// refused and keeps its worktree. A finished mission loses its row, its dock record and worktree, its log and its
    /// diff, and every surface writes the same event. A voyage purge applies the same rule to each of its missions.
    /// </summary>
    public class WorkPurgeParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Work Purge Parity";

        private static readonly string[] Surfaces = new[] { "REST", "WebSocket", "MCP" };

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("PurgeMission_InProgressMission_IsRefusedAndKeepsItsWorktreeOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Seeded seeded = await SeedMissionWithDockAsync(harness, surface, MissionStatusEnum.InProgress, true).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = await PurgeMissionAsync(harness, surface, seeded.Mission.Id).ConfigureAwait(false);

                        AssertTrue(reply.Refused, "a mission a captain is working cannot be purged: " + reply);
                        AssertNotNull(await harness.Driver.Missions.ReadAsync(seeded.Mission.Id).ConfigureAwait(false), surface + ": the mission row stays");
                        AssertNotNull(await harness.Driver.Docks.ReadAsync(seeded.Dock.Id).ConfigureAwait(false), surface + ": the dock record stays");
                        AssertTrue(Directory.Exists(seeded.Dock.WorktreePath!), surface + ": the running captain's worktree stays");
                        AssertTrue(File.Exists(seeded.LogPath), surface + ": the log stays");
                        AssertEqual(0, harness.Events.Count, surface + ": a refused purge writes no event");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("PurgeMission_FailedMission_RemovesRowDockWorktreeLogAndDiffOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Seeded seeded = await SeedMissionWithDockAsync(harness, surface, MissionStatusEnum.Failed, false).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = await PurgeMissionAsync(harness, surface, seeded.Mission.Id).ConfigureAwait(false);

                        AssertFalse(reply.Refused, "a finished mission can be purged: " + reply);
                        AssertNull(await harness.Driver.Missions.ReadAsync(seeded.Mission.Id).ConfigureAwait(false), surface + ": the mission row is deleted");
                        AssertNull(await harness.Driver.Docks.ReadAsync(seeded.Dock.Id).ConfigureAwait(false), surface + ": the dock record is deleted");
                        AssertFalse(Directory.Exists(seeded.Dock.WorktreePath!), surface + ": the worktree is removed");
                        AssertFalse(File.Exists(seeded.LogPath), surface + ": the log is removed");
                        AssertFalse(File.Exists(seeded.DiffPath), surface + ": the diff is removed");
                        AssertEqual(1, harness.EventTypes().Count(type => type == "mission.deleted:" + seeded.Mission.Id),
                            surface + ": one mission.deleted event: " + String.Join(",", harness.EventTypes()));
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("DeleteMissions_Batch_SkipsAMissionAtWorkAndPurgesTheRestOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in new[] { "REST", "MCP" })
                    {
                        Seeded running = await SeedMissionWithDockAsync(harness, surface + "-running", MissionStatusEnum.InProgress, true).ConfigureAwait(false);
                        Seeded failed = await SeedMissionWithDockAsync(harness, surface + "-failed", MissionStatusEnum.Failed, false).ConfigureAwait(false);
                        harness.ResetRecordings();

                        List<string> ids = new List<string> { running.Mission.Id, failed.Mission.Id };
                        SurfaceReply reply = surface == "REST"
                            ? await harness.RestAsync(HttpMethod.Post, "/api/v1/missions/delete/multiple", new { Ids = ids }).ConfigureAwait(false)
                            : await harness.McpAsync("armada_delete_missions", new { ids = ids }).ConfigureAwait(false);

                        AssertNotNull(await harness.Driver.Missions.ReadAsync(running.Mission.Id).ConfigureAwait(false), surface + ": the running mission is skipped: " + reply);
                        AssertTrue(Directory.Exists(running.Dock.WorktreePath!), surface + ": the running captain's worktree stays");
                        AssertNull(await harness.Driver.Missions.ReadAsync(failed.Mission.Id).ConfigureAwait(false), surface + ": the finished mission is deleted");
                        AssertNull(await harness.Driver.Docks.ReadAsync(failed.Dock.Id).ConfigureAwait(false), surface + ": the finished mission's dock record is deleted");
                        AssertFalse(File.Exists(failed.LogPath), surface + ": the finished mission's log is removed");
                        AssertEqual(1, harness.EventTypes().Count(type => type.StartsWith("mission.batch_deleted", StringComparison.Ordinal)),
                            surface + ": one mission.batch_deleted event: " + String.Join(",", harness.EventTypes()));
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("PurgeVoyage_FinishedVoyage_RemovesEachMissionsDockAndLogOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Voyage voyage = await harness.Driver.Voyages.CreateAsync(new Voyage("purge " + surface)
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            Status = VoyageStatusEnum.Cancelled
                        }).ConfigureAwait(false);
                        Seeded seeded = await SeedMissionWithDockAsync(harness, surface + "-voyage", MissionStatusEnum.Failed, false, voyage.Id).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = surface switch
                        {
                            "REST" => await harness.RestAsync(HttpMethod.Delete, "/api/v1/voyages/" + voyage.Id + "/purge").ConfigureAwait(false),
                            "WebSocket" => await harness.WebSocketAsync("purge_voyage", voyage.Id).ConfigureAwait(false),
                            _ => await harness.McpAsync("armada_purge_voyage", new { voyageId = voyage.Id }).ConfigureAwait(false)
                        };

                        AssertFalse(reply.Refused, "a finished voyage can be purged: " + reply);
                        AssertNull(await harness.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false), surface + ": the voyage row is deleted");
                        AssertNull(await harness.Driver.Missions.ReadAsync(seeded.Mission.Id).ConfigureAwait(false), surface + ": the mission row is deleted");
                        AssertNull(await harness.Driver.Docks.ReadAsync(seeded.Dock.Id).ConfigureAwait(false), surface + ": the mission's dock record is deleted");
                        AssertFalse(Directory.Exists(seeded.Dock.WorktreePath!), surface + ": the mission's worktree is removed");
                        AssertFalse(File.Exists(seeded.LogPath), surface + ": the mission's log is removed");
                        AssertEqual(1, harness.EventTypes().Count(type => type == "voyage.deleted:" + voyage.Id),
                            surface + ": one voyage.deleted event: " + String.Join(",", harness.EventTypes()));
                    }
                }
            }).ConfigureAwait(false);
        }

        private static Task<SurfaceReply> PurgeMissionAsync(SurfaceParityHarness harness, string surface, string missionId)
        {
            switch (surface)
            {
                case "REST": return harness.RestAsync(HttpMethod.Delete, "/api/v1/missions/" + missionId + "/purge");
                case "WebSocket": return harness.WebSocketAsync("purge_mission", missionId);
                default: return harness.McpAsync("armada_purge_mission", new { missionId = missionId });
            }
        }

        private static async Task<Seeded> SeedMissionWithDockAsync(SurfaceParityHarness harness, string label, MissionStatusEnum status, bool running, string? voyageId = null)
        {
            Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("purge-vessel-" + label, "https://github.com/test/purge.git")
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId
            }).ConfigureAwait(false);
            Captain? captain = null;
            if (running)
            {
                captain = await harness.Driver.Captains.CreateAsync(new Captain("purge-captain-" + label) { State = CaptainStateEnum.Working }).ConfigureAwait(false);
            }

            Mission mission = await harness.Driver.Missions.CreateAsync(new Mission("purge " + label)
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId,
                VesselId = vessel.Id,
                VoyageId = voyageId,
                CaptainId = captain?.Id,
                Status = status
            }).ConfigureAwait(false);

            string worktree = harness.MakeDirectory(Path.Combine("docks", mission.Id));
            File.WriteAllText(Path.Combine(worktree, "work.txt"), "work in progress");
            Dock dock = await harness.Driver.Docks.CreateAsync(new Dock(vessel.Id)
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId,
                CaptainId = captain?.Id,
                WorktreePath = worktree,
                Active = running
            }).ConfigureAwait(false);
            mission.DockId = dock.Id;
            mission = await harness.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
            if (captain != null)
            {
                captain.CurrentMissionId = mission.Id;
                captain.CurrentDockId = dock.Id;
                await harness.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
            }

            string logPath = harness.WriteLogFile(Path.Combine("missions", mission.Id + ".log"), "mission log\n");
            string diffPath = harness.WriteLogFile(Path.Combine("diffs", mission.Id + ".diff"), "diff --git a/x b/x\n");
            return new Seeded(mission, dock, logPath, diffPath);
        }

        private sealed class Seeded
        {
            public Seeded(Mission mission, Dock dock, string logPath, string diffPath)
            {
                Mission = mission;
                Dock = dock;
                LogPath = logPath;
                DiffPath = diffPath;
            }

            public Mission Mission { get; }
            public Dock Dock { get; }
            public string LogPath { get; }
            public string DiffPath { get; }
        }
    }
}
