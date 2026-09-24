namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// A mission diff reads the same on REST, WebSocket and MCP: the saved diff file first, then the diff snapshot
    /// stored on the mission, then the live worktree. A mission whose worktree was reclaimed still shows the diff it
    /// stored at completion on every surface.
    /// </summary>
    public class MissionDiffParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Diff Parity";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("MissionDiff_OnlyAStoredSnapshot_IsReturnedOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Mission mission = await harness.Driver.Missions.CreateAsync(new Mission("snapshot diff")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Status = MissionStatusEnum.Complete,
                        BranchName = "armada/snapshot",
                        DiffSnapshot = "diff --git a/snapshot.txt b/snapshot.txt\n+SNAPSHOT-DIFF-MARKER\n"
                    }).ConfigureAwait(false);

                    foreach (SurfaceReply reply in await ReadDiffAsync(harness, mission.Id).ConfigureAwait(false))
                    {
                        AssertFalse(reply.Refused, "the stored snapshot is readable: " + reply);
                        AssertContains("SNAPSHOT-DIFF-MARKER", reply.Body, "the stored diff snapshot is returned: " + reply);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("MissionDiff_SavedDiffFile_WinsOverTheSnapshotOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Mission mission = await harness.Driver.Missions.CreateAsync(new Mission("saved diff")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Status = MissionStatusEnum.Complete,
                        DiffSnapshot = "SNAPSHOT-ONLY\n"
                    }).ConfigureAwait(false);
                    harness.WriteLogFile(Path.Combine("diffs", mission.Id + ".diff"), "SAVED-FILE-DIFF\n");

                    foreach (SurfaceReply reply in await ReadDiffAsync(harness, mission.Id).ConfigureAwait(false))
                    {
                        AssertContains("SAVED-FILE-DIFF", reply.Body, "the saved diff file is read first: " + reply);
                        AssertFalse(reply.Body.Contains("SNAPSHOT-ONLY", StringComparison.Ordinal), "the snapshot is not used when a file exists: " + reply);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("MissionDiff_NoDiffAnywhere_IsRefusedOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Mission mission = await harness.Driver.Missions.CreateAsync(new Mission("no diff")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);

                    foreach (SurfaceReply reply in await ReadDiffAsync(harness, mission.Id).ConfigureAwait(false))
                    {
                        AssertTrue(reply.Refused, "a mission with no saved diff, snapshot or worktree has no diff: " + reply);
                        AssertContains("No diff available", reply.Body, "the refusal says why: " + reply);
                    }
                }
            }).ConfigureAwait(false);
        }

        private static async Task<List<SurfaceReply>> ReadDiffAsync(SurfaceParityHarness harness, string missionId)
        {
            return new List<SurfaceReply>
            {
                await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + missionId + "/diff").ConfigureAwait(false),
                await harness.WebSocketAsync("get_mission_diff", missionId).ConfigureAwait(false),
                await harness.McpAsync("armada_get_mission_diff", new { missionId = missionId }).ConfigureAwait(false)
            };
        }
    }
}
