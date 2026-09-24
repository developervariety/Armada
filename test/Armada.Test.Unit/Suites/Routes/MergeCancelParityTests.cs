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
    /// Cancelling a merge entry is one operation on REST, WebSocket and MCP. A finished entry (Landed, Failed,
    /// Cancelled) is refused and keeps its recorded outcome, an unknown entry is refused as not found, and a queued
    /// entry is cancelled with the same event on every surface.
    /// </summary>
    public class MergeCancelParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Merge Cancel Parity";

        private static readonly string[] Surfaces = new[] { "REST", "WebSocket", "MCP" };

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("CancelMerge_LandedEntry_IsRefusedAndKeepsItsOutcomeOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        DateTime landedAt = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
                        MergeEntry entry = await harness.Driver.MergeEntries.CreateAsync(new MergeEntry("armada/landed-" + surface)
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            Status = MergeStatusEnum.Landed,
                            CompletedUtc = landedAt
                        }).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = await CancelAsync(harness, surface, entry.Id).ConfigureAwait(false);

                        AssertTrue(reply.Refused, "a Landed entry cannot be cancelled: " + reply);
                        MergeEntry? stored = await harness.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(stored, surface + ": the entry is kept");
                        AssertEqual(MergeStatusEnum.Landed, stored!.Status, surface + ": the entry still reads Landed");
                        AssertEqual(landedAt, stored.CompletedUtc!.Value.ToUniversalTime(), surface + ": the landing time is not rewritten");
                        AssertEqual(0, harness.Events.Count, surface + ": a refused cancel writes no event");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("CancelMerge_UnknownEntry_IsRefusedAsNotFoundOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        SurfaceReply reply = await CancelAsync(harness, surface, "mrg_missing_" + surface).ConfigureAwait(false);
                        AssertTrue(reply.Refused, "an unknown entry is not reported cancelled: " + reply);
                        AssertContains("not found", reply.Body.ToLowerInvariant(), surface + ": the refusal says not found: " + reply);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("CancelMerge_QueuedEntry_IsCancelledWithOneEventOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        MergeEntry entry = await harness.Driver.MergeEntries.CreateAsync(new MergeEntry("armada/queued-" + surface)
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            Status = MergeStatusEnum.Queued
                        }).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = await CancelAsync(harness, surface, entry.Id).ConfigureAwait(false);

                        AssertFalse(reply.Refused, "a queued entry is cancelled: " + reply);
                        MergeEntry? stored = await harness.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertEqual(MergeStatusEnum.Cancelled, stored!.Status, surface + ": the entry reads Cancelled");
                        AssertEqual(1, harness.EventTypes().Count(type => type == "merge.cancelled:" + entry.Id),
                            surface + ": one merge.cancelled event: " + String.Join(",", harness.EventTypes()));
                    }
                }
            }).ConfigureAwait(false);
        }

        private static Task<SurfaceReply> CancelAsync(SurfaceParityHarness harness, string surface, string entryId)
        {
            switch (surface)
            {
                case "REST": return harness.RestAsync(HttpMethod.Post, "/api/v1/merge-queue/" + entryId + "/cancel", new { });
                case "WebSocket": return harness.WebSocketAsync("cancel_merge", entryId);
                default: return harness.McpAsync("armada_cancel_merge", new { entryId = entryId });
            }
        }
    }
}
