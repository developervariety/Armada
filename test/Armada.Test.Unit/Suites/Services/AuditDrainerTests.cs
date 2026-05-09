namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for McpAuditTools: armada_drain_audit_queue and armada_record_audit_verdict.
    /// </summary>
    public class AuditDrainerTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Audit Drainer";

        /// <summary>Run all audit drainer tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("DrainAuditQueue_ReturnsOnlyPendingDeepPickedEntries", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("drain-1", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    MergeEntry e1 = new MergeEntry("branch-1", "main") { VesselId = vessel.Id, Status = MergeStatusEnum.Landed, AuditDeepPicked = true, AuditDeepVerdict = "Pending" };
                    MergeEntry e2 = new MergeEntry("branch-2", "main") { VesselId = vessel.Id, Status = MergeStatusEnum.Landed, AuditDeepPicked = true, AuditDeepVerdict = "Pass", AuditDeepCompletedUtc = DateTime.UtcNow };
                    MergeEntry e3 = new MergeEntry("branch-3", "main") { VesselId = vessel.Id, Status = MergeStatusEnum.Landed, AuditDeepPicked = false };
                    await testDb.Driver.MergeEntries.CreateAsync(e1).ConfigureAwait(false);
                    await testDb.Driver.MergeEntries.CreateAsync(e2).ConfigureAwait(false);
                    await testDb.Driver.MergeEntries.CreateAsync(e3).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? drainHandler = null;
                    McpAuditTools.Register((name, _, _, handler) => { if (name == "armada_drain_audit_queue") drainHandler = handler; }, testDb.Driver);
                    AssertNotNull(drainHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id, limit = 10 });
                    object result = await drainHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    JsonNode? root = JsonNode.Parse(resultJson);
                    JsonArray? entries = root?["entries"]?.AsArray();
                    string entriesJson = entries?.ToJsonString() ?? "";

                    AssertContains("branch-1", entriesJson);
                    AssertFalse(entriesJson.Contains("branch-2"), "Picked+Pass entry should be excluded");
                    AssertFalse(entriesJson.Contains("branch-3"), "Not-Picked entry should be excluded");
                    JsonArray? reflections = root?["reflectionsDispatched"]?.AsArray();
                    AssertNotNull(reflections, "Drain response should include reflectionsDispatched");
                    AssertEqual(0, reflections!.Count, "Without reflection dispatcher nothing auto-dispatches");
                }
            });

            await RunTest("RecordAuditVerdict_PassPath_StoresVerdictAndNotes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("drain-rec-1", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    MergeEntry entry = new MergeEntry("branch-rec-1", "main") { VesselId = vessel.Id, Status = MergeStatusEnum.Landed, AuditDeepPicked = true, AuditDeepVerdict = "Pending" };
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? recordHandler = null;
                    McpAuditTools.Register((name, _, _, handler) => { if (name == "armada_record_audit_verdict") recordHandler = handler; }, testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new { entryId = entry.Id, verdict = "Pass", notes = "All good." });
                    await recordHandler!(args).ConfigureAwait(false);

                    MergeEntry? read = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual("Pass", read!.AuditDeepVerdict);
                    AssertEqual("All good.", read.AuditDeepNotes);
                    AssertNotNull(read.AuditDeepCompletedUtc);
                    AssertNull(read.AuditDeepRecommendedAction);
                }
            });

            await RunTest("RecordAuditVerdict_CriticalRequiresRecommendedAction", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("drain-rec-2", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    MergeEntry entry = new MergeEntry("branch-rec-2", "main") { VesselId = vessel.Id, Status = MergeStatusEnum.Landed, AuditDeepPicked = true, AuditDeepVerdict = "Pending" };
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? recordHandler = null;
                    McpAuditTools.Register((name, _, _, handler) => { if (name == "armada_record_audit_verdict") recordHandler = handler; }, testDb.Driver);

                    JsonElement bad = JsonSerializer.SerializeToElement(new { entryId = entry.Id, verdict = "Critical", notes = "Concerning." });
                    object errResult = await recordHandler!(bad).ConfigureAwait(false);
                    AssertContains("recommendedAction required", JsonSerializer.Serialize(errResult));

                    JsonElement good = JsonSerializer.SerializeToElement(new { entryId = entry.Id, verdict = "Critical", notes = "Concerning.", recommendedAction = "Roll back manually." });
                    await recordHandler!(good).ConfigureAwait(false);

                    MergeEntry? read = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual("Critical", read!.AuditDeepVerdict);
                    AssertEqual("Roll back manually.", read.AuditDeepRecommendedAction);
                }
            });

            await RunTest("RecordAuditVerdict_InvalidVerdict_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("drain-rec-3", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    MergeEntry entry = new MergeEntry("branch-rec-3", "main") { VesselId = vessel.Id, Status = MergeStatusEnum.Landed, AuditDeepPicked = true, AuditDeepVerdict = "Pending" };
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? recordHandler = null;
                    McpAuditTools.Register((name, _, _, handler) => { if (name == "armada_record_audit_verdict") recordHandler = handler; }, testDb.Driver);

                    JsonElement args = JsonSerializer.SerializeToElement(new { entryId = entry.Id, verdict = "Bogus", notes = "x" });
                    object result = await recordHandler!(args).ConfigureAwait(false);
                    AssertContains("verdict must be", JsonSerializer.Serialize(result));
                }
            });
        }
    }
}
