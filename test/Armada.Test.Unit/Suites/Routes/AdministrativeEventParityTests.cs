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
    /// An operator's dock, event-log, merge-purge and captain batch actions write the same event on REST and MCP, so
    /// the audit trail does not depend on which surface the operator used.
    /// </summary>
    public class AdministrativeEventParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Administrative Event Parity";

        private static readonly string[] Surfaces = new[] { "REST", "MCP" };

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("DockActions_WriteTheSameEventOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("dock-events-" + surface.ToLowerInvariant(), "https://github.com/test/dock-events.git")).ConfigureAwait(false);
                        Dock deleteDock = await CreateDockAsync(harness, vessel, surface + "-delete").ConfigureAwait(false);
                        Dock purgeDock = await CreateDockAsync(harness, vessel, surface + "-purge").ConfigureAwait(false);
                        Dock repairDock = await CreateDockAsync(harness, vessel, surface + "-repair").ConfigureAwait(false);
                        Dock unstickDock = await CreateDockAsync(harness, vessel, surface + "-unstick").ConfigureAwait(false);
                        Dock batchDock = await CreateDockAsync(harness, vessel, surface + "-batch").ConfigureAwait(false);
                        harness.ResetRecordings();

                        if (surface == "REST")
                        {
                            await harness.RestAsync(HttpMethod.Delete, "/api/v1/docks/" + deleteDock.Id).ConfigureAwait(false);
                            await harness.RestAsync(HttpMethod.Delete, "/api/v1/docks/" + purgeDock.Id + "/purge").ConfigureAwait(false);
                            await harness.RestAsync(HttpMethod.Post, "/api/v1/docks/" + repairDock.Id + "/repair", new { }).ConfigureAwait(false);
                            await harness.RestAsync(HttpMethod.Post, "/api/v1/docks/" + unstickDock.Id + "/unstick", new { }).ConfigureAwait(false);
                            await harness.RestAsync(HttpMethod.Post, "/api/v1/docks/delete/multiple", new { Ids = new[] { batchDock.Id } }).ConfigureAwait(false);
                        }
                        else
                        {
                            await harness.McpAsync("armada_delete_dock", new { dockId = deleteDock.Id }).ConfigureAwait(false);
                            await harness.McpAsync("armada_purge_dock", new { dockId = purgeDock.Id }).ConfigureAwait(false);
                            await harness.McpAsync("armada_repair_dock", new { dockId = repairDock.Id }).ConfigureAwait(false);
                            await harness.McpAsync("armada_unstick_dock", new { dockId = unstickDock.Id }).ConfigureAwait(false);
                            await harness.McpAsync("armada_delete_docks", new { ids = new[] { batchDock.Id } }).ConfigureAwait(false);
                        }

                        List<string> events = harness.EventTypes();
                        foreach (string expected in new[]
                        {
                            "dock.deleted:" + deleteDock.Id,
                            "dock.purged:" + purgeDock.Id,
                            "dock.repaired:" + repairDock.Id,
                            "dock.unstuck:" + unstickDock.Id,
                            "dock.batch_deleted:"
                        })
                        {
                            AssertTrue(events.Contains(expected), surface + ": writes " + expected + ": " + String.Join(",", events));
                        }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("EventAndMergeAndCaptainBatchActions_WriteTheSameEventOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        ArmadaEvent single = await harness.Driver.Events.CreateAsync(new ArmadaEvent("test.single", "single " + surface)).ConfigureAwait(false);
                        ArmadaEvent batch = await harness.Driver.Events.CreateAsync(new ArmadaEvent("test.batch", "batch " + surface)).ConfigureAwait(false);
                        MergeEntry landed = await harness.Driver.MergeEntries.CreateAsync(new MergeEntry("armada/purge-" + surface) { Status = MergeStatusEnum.Landed }).ConfigureAwait(false);
                        MergeEntry landedBatch = await harness.Driver.MergeEntries.CreateAsync(new MergeEntry("armada/purge-batch-" + surface) { Status = MergeStatusEnum.Landed }).ConfigureAwait(false);
                        Captain idle = await harness.Driver.Captains.CreateAsync(new Captain("batch-delete-" + surface)).ConfigureAwait(false);
                        harness.ResetRecordings();

                        if (surface == "REST")
                        {
                            await harness.RestAsync(HttpMethod.Delete, "/api/v1/events/" + single.Id).ConfigureAwait(false);
                            await harness.RestAsync(HttpMethod.Post, "/api/v1/events/delete/multiple", new { Ids = new[] { batch.Id } }).ConfigureAwait(false);
                            await harness.RestAsync(HttpMethod.Delete, "/api/v1/merge-queue/" + landed.Id + "/purge").ConfigureAwait(false);
                            await harness.RestAsync(HttpMethod.Post, "/api/v1/merge-queue/purge", new { EntryIds = new[] { landedBatch.Id } }).ConfigureAwait(false);
                            await harness.RestAsync(HttpMethod.Post, "/api/v1/captains/delete/multiple", new { Ids = new[] { idle.Id } }).ConfigureAwait(false);
                        }
                        else
                        {
                            await harness.McpAsync("armada_delete_event", new { eventId = single.Id }).ConfigureAwait(false);
                            await harness.McpAsync("armada_delete_events", new { ids = new[] { batch.Id } }).ConfigureAwait(false);
                            await harness.McpAsync("armada_purge_merge_entry", new { entryId = landed.Id }).ConfigureAwait(false);
                            await harness.McpAsync("armada_purge_merge_entries", new { entryIds = new[] { landedBatch.Id } }).ConfigureAwait(false);
                            await harness.McpAsync("armada_delete_captains", new { ids = new[] { idle.Id } }).ConfigureAwait(false);
                        }

                        List<string> events = harness.EventTypes();
                        foreach (string expected in new[]
                        {
                            "event.deleted:" + single.Id,
                            "event.batch_deleted:",
                            "merge.purged:" + landed.Id,
                            "merge.batch_purged:",
                            "captain.batch_deleted:"
                        })
                        {
                            AssertTrue(events.Contains(expected), surface + ": writes " + expected + ": " + String.Join(",", events));
                        }
                    }
                }
            }).ConfigureAwait(false);
        }

        private static async Task<Dock> CreateDockAsync(SurfaceParityHarness harness, Vessel vessel, string label)
        {
            string worktree = harness.MakeDirectory(Path.Combine("event-docks", label));
            return await harness.Driver.Docks.CreateAsync(new Dock(vessel.Id) { WorktreePath = worktree, Active = false }).ConfigureAwait(false);
        }
    }
}
