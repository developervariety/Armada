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

            await RunTest("DrainAuditQueue includes and completes an unassociated Judge follow-up", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("follow-up-drain", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    JudgeFollowUp followUp = new JudgeFollowUp
                    {
                        JudgeMissionId = "msn_judge-unassociated",
                        ReviewedMissionId = "msn_reviewed-unassociated",
                        VesselId = vessel.Id,
                        JudgeVerdict = "PASS",
                        SuggestedFollowUps = "- Add the missing failure-path assertion.",
                        AuditVerdict = "Pending"
                    };
                    followUp = await testDb.Driver.JudgeFollowUps.UpsertAsync(followUp).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? drainHandler = null;
                    Func<JsonElement?, Task<object>>? recordHandler = null;
                    McpAuditTools.Register((name, _, _, handler) =>
                    {
                        if (name == "armada_drain_audit_queue") drainHandler = handler;
                        if (name == "armada_record_audit_verdict") recordHandler = handler;
                    }, testDb.Driver);

                    object drained = await drainHandler!(JsonSerializer.SerializeToElement(
                        new { vesselId = vessel.Id, limit = 10 })).ConfigureAwait(false);
                    string drainJson = JsonSerializer.Serialize(drained);
                    AssertContains("judgeFollowUp", drainJson);
                    AssertContains("missing failure-path assertion", drainJson);
                    AssertContains(followUp.Id, drainJson);

                    await recordHandler!(JsonSerializer.SerializeToElement(new
                    {
                        followUpId = followUp.Id,
                        verdict = "Concern",
                        notes = "Track this assertion in the next slice."
                    })).ConfigureAwait(false);

                    JudgeFollowUp? reloaded = await testDb.Driver.JudgeFollowUps.ReadAsync(followUp.Id).ConfigureAwait(false);
                    AssertEqual("Concern", reloaded!.AuditVerdict);
                    AssertNotNull(reloaded.AuditCompletedUtc);
                    List<JudgeFollowUp> pending = await testDb.Driver.JudgeFollowUps.EnumeratePendingAsync(vessel.Id).ConfigureAwait(false);
                    AssertEqual(0, pending.Count, "A recorded follow-up must leave the pending audit queue");
                }
            });

            await RunTest("Associated Judge follow-up drains once and mirrors its verdict", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("follow-up-associated", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    MergeEntry entry = new MergeEntry("follow-up-branch", "main")
                    {
                        MissionId = "msn_judge-associated",
                        VesselId = vessel.Id,
                        Status = MergeStatusEnum.Landed,
                        AuditDeepPicked = true,
                        AuditDeepVerdict = "Pending"
                    };
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);
                    JudgeFollowUp followUp = new JudgeFollowUp
                    {
                        JudgeMissionId = entry.MissionId!,
                        ReviewedMissionId = "msn_reviewed-associated",
                        VesselId = vessel.Id,
                        MergeEntryId = entry.Id,
                        JudgeVerdict = "PASS",
                        SuggestedFollowUps = "- Retain this single queue item.",
                        AuditVerdict = "Pending"
                    };
                    followUp = await testDb.Driver.JudgeFollowUps.UpsertAsync(followUp).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? drainHandler = null;
                    Func<JsonElement?, Task<object>>? recordHandler = null;
                    McpAuditTools.Register((name, _, _, handler) =>
                    {
                        if (name == "armada_drain_audit_queue") drainHandler = handler;
                        if (name == "armada_record_audit_verdict") recordHandler = handler;
                    }, testDb.Driver);

                    object drained = await drainHandler!(JsonSerializer.SerializeToElement(
                        new { vesselId = vessel.Id, limit = 10 })).ConfigureAwait(false);
                    JsonNode? root = JsonNode.Parse(JsonSerializer.Serialize(drained));
                    JsonArray? entries = root?["entries"]?.AsArray();
                    AssertEqual(1, entries!.Count, "A linked follow-up and merge entry are one audit item");
                    AssertContains("judgeFollowUp", entries[0]!.ToJsonString());

                    await recordHandler!(JsonSerializer.SerializeToElement(new
                    {
                        entryId = entry.Id,
                        verdict = "Pass",
                        notes = "Verified."
                    })).ConfigureAwait(false);

                    JudgeFollowUp? completedFollowUp = await testDb.Driver.JudgeFollowUps.ReadAsync(followUp.Id).ConfigureAwait(false);
                    AssertEqual("Pass", completedFollowUp!.AuditVerdict, "The legacy entryId path must complete the canonical follow-up");
                    AssertNotNull(completedFollowUp.AuditCompletedUtc);
                    MergeEntry? mirrored = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual("Pass", mirrored!.AuditDeepVerdict);
                    AssertEqual("Verified.", mirrored.AuditDeepNotes);
                    AssertNotNull(mirrored.AuditDeepCompletedUtc);

                    mirrored.AuditDeepVerdict = "Pending";
                    mirrored.AuditDeepCompletedUtc = null;
                    await testDb.Driver.MergeEntries.UpdateAsync(mirrored).ConfigureAwait(false);
                    object drainedStaleMirror = await drainHandler!(JsonSerializer.SerializeToElement(
                        new { vesselId = vessel.Id, limit = 10 })).ConfigureAwait(false);
                    JsonArray? staleEntries = JsonNode.Parse(JsonSerializer.Serialize(drainedStaleMirror))?["entries"]?.AsArray();
                    AssertEqual(0, staleEntries!.Count, "A completed canonical follow-up must suppress a stale linked merge mirror");
                }
            });

            await RunTest("Legacy entryId completes multiple linked follow-ups oldest first", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MergeEntry entry = await testDb.Driver.MergeEntries.CreateAsync(
                        new MergeEntry("shared-review", "main") { MissionId = "msn_shared-review" }).ConfigureAwait(false);
                    JudgeFollowUp older = new JudgeFollowUp
                    {
                        JudgeMissionId = "msn_judge-shared-old",
                        ReviewedMissionId = entry.MissionId!,
                        MergeEntryId = entry.Id,
                        JudgeVerdict = "PASS",
                        SuggestedFollowUps = "- Older action.",
                        CreatedUtc = DateTime.UtcNow.AddMinutes(-2)
                    };
                    JudgeFollowUp newer = new JudgeFollowUp
                    {
                        JudgeMissionId = "msn_judge-shared-new",
                        ReviewedMissionId = entry.MissionId!,
                        MergeEntryId = entry.Id,
                        JudgeVerdict = "PASS",
                        SuggestedFollowUps = "- Newer action.",
                        CreatedUtc = DateTime.UtcNow.AddMinutes(-1)
                    };
                    older = await testDb.Driver.JudgeFollowUps.UpsertAsync(older).ConfigureAwait(false);
                    newer = await testDb.Driver.JudgeFollowUps.UpsertAsync(newer).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? recordHandler = null;
                    McpAuditTools.Register((name, _, _, handler) =>
                    {
                        if (name == "armada_record_audit_verdict") recordHandler = handler;
                    }, testDb.Driver);

                    await recordHandler!(JsonSerializer.SerializeToElement(new
                    {
                        entryId = entry.Id,
                        verdict = "Pass",
                        notes = "First legacy audit."
                    })).ConfigureAwait(false);
                    JudgeFollowUp? completedOlder = await testDb.Driver.JudgeFollowUps.ReadAsync(older.Id).ConfigureAwait(false);
                    JudgeFollowUp? pendingNewer = await testDb.Driver.JudgeFollowUps.ReadAsync(newer.Id).ConfigureAwait(false);
                    AssertEqual("Pass", completedOlder!.AuditVerdict, "The first legacy call must complete the oldest linked item");
                    AssertEqual("Pending", pendingNewer!.AuditVerdict, "The newer linked item must remain for the next call");

                    await recordHandler(JsonSerializer.SerializeToElement(new
                    {
                        entryId = entry.Id,
                        verdict = "Concern",
                        notes = "Second legacy audit."
                    })).ConfigureAwait(false);
                    pendingNewer = await testDb.Driver.JudgeFollowUps.ReadAsync(newer.Id).ConfigureAwait(false);
                    AssertEqual("Concern", pendingNewer!.AuditVerdict, "The second legacy call must advance to the next linked item");
                }
            });
        }
    }
}
