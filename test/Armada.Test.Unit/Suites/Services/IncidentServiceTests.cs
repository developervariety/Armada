namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Unit coverage for incident context preservation across create, update, and query flows.
    /// </summary>
    public class IncidentServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Incident Service";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Regression links round-trip through incident tools and require a purpose", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterIncidentHandlers(incidents);

                using JsonDocument createDoc = JsonDocument.Parse("{\"title\":\"Consumer broke\",\"regressionPurpose\":\"Consumer\",\"regressionObjectiveId\":\"obj_origin\",\"regressionLandedCommit\":\"ABCDEF1234\"}");
                Incident created = (Incident)await handlers["armada_create_incident"](createDoc.RootElement).ConfigureAwait(false);
                AssertEqual(RegressionPurposeEnum.Consumer, created.RegressionPurpose);
                AssertEqual(RegressionCauseEnum.Unclassified, created.RegressionCause);
                AssertEqual("abcdef1234", created.RegressionLandedCommit, "commit is normalized");

                using JsonDocument updateDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\",\"regressionCause\":\"LandedChange\",\"regressionLandedCommit\":\"\"}");
                Incident updated = (Incident)await handlers["armada_update_incident"](updateDoc.RootElement).ConfigureAwait(false);
                AssertEqual(RegressionCauseEnum.LandedChange, updated.RegressionCause);
                AssertEqual("obj_origin", updated.RegressionObjectiveId, "an omitted link is preserved");
                AssertNull(updated.RegressionLandedCommit, "a blank link is cleared");

                using JsonDocument getDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\"}");
                Incident read = (Incident)await handlers["armada_get_incident"](getDoc.RootElement).ConfigureAwait(false);
                AssertEqual(RegressionCauseEnum.LandedChange, read.RegressionCause, "the snapshot persists the cause");

                using JsonDocument badCommitDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\",\"regressionLandedCommit\":\"not-a-commit\"}");
                object badCommit = await handlers["armada_update_incident"](badCommitDoc.RootElement).ConfigureAwait(false);
                AssertContains("regressionLandedCommit", JsonSerializer.Serialize(badCommit), "an invalid commit is rejected with a reason");

                using JsonDocument noPurposeDoc = JsonDocument.Parse("{\"title\":\"Link without purpose\",\"regressionObjectiveId\":\"obj_origin\"}");
                await AssertThrowsAsync<InvalidOperationException>(async () =>
                    await handlers["armada_create_incident"](noPurposeDoc.RootElement).ConfigureAwait(false),
                    "a regression link without a purpose is rejected").ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("MCP incident tools expose list get create update close and delete", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterIncidentHandlers(incidents);

                AssertTrue(handlers.ContainsKey("armada_list_incidents"));
                AssertTrue(handlers.ContainsKey("armada_get_incident"));
                AssertTrue(handlers.ContainsKey("armada_create_incident"));
                AssertTrue(handlers.ContainsKey("armada_update_incident"));
                AssertTrue(handlers.ContainsKey("armada_close_incident"));
                AssertTrue(handlers.ContainsKey("armada_delete_incident"));

                using JsonDocument createDoc = JsonDocument.Parse("{\"title\":\"MCP incident\",\"severity\":\"High\",\"status\":\"Open\",\"vesselId\":\"vsl_mcp\"}");
                object createResult = await handlers["armada_create_incident"](createDoc.RootElement).ConfigureAwait(false);
                Incident created = (Incident)createResult;
                AssertEqual("MCP incident", created.Title);

                using JsonDocument listDoc = JsonDocument.Parse("{\"vesselId\":\"vsl_mcp\"}");
                object listResult = await handlers["armada_list_incidents"](listDoc.RootElement).ConfigureAwait(false);
                EnumerationResult<Incident> listed = (EnumerationResult<Incident>)listResult;
                AssertEqual(1, listed.Objects.Count);

                using JsonDocument getDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\"}");
                object getResult = await handlers["armada_get_incident"](getDoc.RootElement).ConfigureAwait(false);
                AssertEqual(created.Id, ((Incident)getResult).Id);

                using JsonDocument updateDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\",\"status\":\"Closed\",\"recoveryNotes\":\"resolved\",\"rootCause\":\"Stale lock file\"}");
                object updateResult = await handlers["armada_update_incident"](updateDoc.RootElement).ConfigureAwait(false);
                Incident updated = (Incident)updateResult;
                AssertEqual(IncidentStatusEnum.Closed, updated.Status);
                AssertEqual("resolved", updated.RecoveryNotes);

                using JsonDocument reopenDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\",\"status\":\"Open\"}");
                await handlers["armada_update_incident"](reopenDoc.RootElement).ConfigureAwait(false);

                using JsonDocument closeDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\",\"recoveryNotes\":\"closed by MCP\",\"rootCause\":\"Stale lock file, removed\"}");
                object closeResult = await handlers["armada_close_incident"](closeDoc.RootElement).ConfigureAwait(false);
                Incident closed = (Incident)closeResult;
                AssertEqual(IncidentStatusEnum.Closed, closed.Status);
                AssertEqual("closed by MCP", closed.RecoveryNotes);
                AssertTrue(closed.ClosedUtc.HasValue, "Expected close tool to stamp ClosedUtc.");

                using JsonDocument deleteDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\"}");
                object deleteResult = await handlers["armada_delete_incident"](deleteDoc.RootElement).ConfigureAwait(false);
                string deleteJson = JsonSerializer.Serialize(deleteResult);
                AssertContains("\"Deleted\":true", deleteJson);
            }).ConfigureAwait(false);

            await RunTest("MCP close and update-to-Closed refuse an empty or unchanged root cause", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterIncidentHandlers(incidents);
                Incident created = await OpenAutomaticIncidentAsync(incidents).ConfigureAwait(false);

                string[] rootCauseFields =
                {
                    String.Empty,
                    ",\"rootCause\":\"   \"",
                    ",\"rootCause\":\"  " + AutomaticReason + "  \""
                };
                string[] expectedCodes =
                {
                    IncidentRootCauseRule.RequiredCode,
                    IncidentRootCauseRule.RequiredCode,
                    IncidentRootCauseRule.UnchangedCode
                };
                for (int i = 0; i < rootCauseFields.Length; i++)
                {
                    using JsonDocument closeDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\"" + rootCauseFields[i] + "}");
                    string closeJson = JsonSerializer.Serialize(await handlers["armada_close_incident"](closeDoc.RootElement).ConfigureAwait(false));
                    AssertContains("\"Code\":\"" + expectedCodes[i] + "\"", closeJson, "close case " + i + " names its refusal");

                    using JsonDocument updateDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\",\"status\":\"Closed\"" + rootCauseFields[i] + "}");
                    string updateJson = JsonSerializer.Serialize(await handlers["armada_update_incident"](updateDoc.RootElement).ConfigureAwait(false));
                    AssertContains("\"Code\":\"" + expectedCodes[i] + "\"", updateJson, "update case " + i + " names its refusal");
                }

                Incident? stillOpen = await incidents.ReadAsync(McpTestCaller.Operator, created.Id).ConfigureAwait(false);
                AssertNotNull(stillOpen);
                AssertEqual(IncidentStatusEnum.Open, stillOpen!.Status, "a refused close changes nothing");
                AssertEqual(AutomaticReason, stillOpen.RootCause);
                AssertEqual(AutomaticReason, stillOpen.OpenedReason);
                AssertNull(stillOpen.RootCauseWrittenBy, "the opened reason is not a person-written cause");
            }).ConfigureAwait(false);

            await RunTest("MCP close stores a written root cause with its author and time and keeps the opened reason", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterIncidentHandlers(incidents);
                Incident created = await OpenAutomaticIncidentAsync(incidents).ConfigureAwait(false);
                DateTime before = DateTime.UtcNow.AddSeconds(-1);

                using JsonDocument closeDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\",\"rootCause\":\"  Test host ran out of disk  \"}");
                Incident closed = (Incident)await handlers["armada_close_incident"](closeDoc.RootElement).ConfigureAwait(false);
                AssertEqual(IncidentStatusEnum.Closed, closed.Status);
                AssertEqual("Test host ran out of disk", closed.RootCause);
                AssertEqual(AutomaticReason, closed.OpenedReason, "the automatic text is kept separately");
                AssertEqual(McpTestCaller.Operator.UserId, closed.RootCauseWrittenBy);
                AssertTrue(closed.RootCauseWrittenUtc.HasValue && closed.RootCauseWrittenUtc.Value >= before, "the write time is stamped");
                AssertFalse(closed.ClosedAutomatically, "a person closed it");

                Incident? read = await incidents.ReadAsync(McpTestCaller.Operator, created.Id).ConfigureAwait(false);
                AssertNotNull(read);
                AssertEqual(McpTestCaller.Operator.UserId, read!.RootCauseWrittenBy, "authorship persists in the snapshot");

                using JsonDocument revertDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\",\"rootCause\":\"" + AutomaticReason + "\"}");
                string revertJson = JsonSerializer.Serialize(await handlers["armada_update_incident"](revertDoc.RootElement).ConfigureAwait(false));
                AssertContains("\"Code\":\"" + IncidentRootCauseRule.UnchangedCode + "\"", revertJson, "a closed incident cannot fall back to the opened reason");

                using JsonDocument notesDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\",\"postmortem\":\"Disk alert added\"}");
                Incident annotated = (Incident)await handlers["armada_update_incident"](notesDoc.RootElement).ConfigureAwait(false);
                AssertEqual("Disk alert added", annotated.Postmortem, "editing other fields of a closed incident is allowed");
            }).ConfigureAwait(false);

            await RunTest("An automatic close is allowed without a written cause and is marked automatic", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                Incident created = await OpenAutomaticIncidentAsync(incidents).ConfigureAwait(false);

                Incident closed = await incidents.UpdateAutomaticallyAsync(McpTestCaller.Operator, created.Id, new IncidentUpsertRequest
                {
                    Status = IncidentStatusEnum.Closed,
                    RootCause = "Automatic reading written by a sweep"
                }).ConfigureAwait(false);
                AssertEqual(IncidentStatusEnum.Closed, closed.Status);
                AssertTrue(closed.ClosedAutomatically, "a system close is recorded as automatic");
                AssertNull(closed.RootCauseWrittenBy, "a cause a system path supplies never counts as person-written");
                AssertNull(closed.RootCauseWrittenUtc);

                Incident reopened = await incidents.UpdateAsync(McpTestCaller.Operator, created.Id, new IncidentUpsertRequest
                {
                    Status = IncidentStatusEnum.Open
                }).ConfigureAwait(false);
                AssertFalse(reopened.ClosedAutomatically, "leaving a terminal status clears the automatic mark");
                string? code = await RefusalCodeAsync(() => incidents.UpdateAsync(McpTestCaller.Operator, created.Id, new IncidentUpsertRequest
                {
                    Status = IncidentStatusEnum.Closed
                })).ConfigureAwait(false);
                AssertEqual(IncidentRootCauseRule.RequiredCode, code, "a person cannot close on the system-supplied cause");
            }).ConfigureAwait(false);

            await RunTest("A snapshot stored before opened reasons existed treats its root cause as the opened reason", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                Incident legacy = new Incident
                {
                    TenantId = McpTestCaller.Operator.TenantId,
                    UserId = McpTestCaller.Operator.UserId,
                    Title = "Stored before the field",
                    RootCause = AutomaticReason
                };
                string payload = JsonSerializer.Serialize(legacy)
                    .Replace("\"OpenedReason\":null,", String.Empty)
                    .Replace("\"RootCauseWrittenBy\":null,", String.Empty);
                AssertFalse(payload.Contains("OpenedReason", StringComparison.Ordinal), "the stored payload has no opened reason");
                await testDb.Driver.Events.CreateAsync(new ArmadaEvent(IncidentService.SnapshotEventType, legacy.Title)
                {
                    TenantId = legacy.TenantId,
                    UserId = legacy.UserId,
                    EntityType = IncidentService.IncidentEntityType,
                    EntityId = legacy.Id,
                    Payload = payload
                }).ConfigureAwait(false);

                string? code = await RefusalCodeAsync(() => incidents.UpdateAsync(McpTestCaller.Operator, legacy.Id, new IncidentUpsertRequest
                {
                    Status = IncidentStatusEnum.Closed,
                    RootCause = AutomaticReason
                })).ConfigureAwait(false);
                AssertEqual(IncidentRootCauseRule.UnchangedCode, code);
            }).ConfigureAwait(false);
        }

        private const string AutomaticReason = "DoD gate failed: classification=TestFail";

        private static Task<Incident> OpenAutomaticIncidentAsync(IncidentService incidents)
        {
            return incidents.CreateAsync(McpTestCaller.Operator, new IncidentUpsertRequest
            {
                Title = "Mission failed: example",
                Status = IncidentStatusEnum.Open,
                RootCause = AutomaticReason
            });
        }

        private static async Task<string?> RefusalCodeAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                return null;
            }
            catch (IncidentRootCauseRefusedException refused)
            {
                return refused.Code;
            }
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> RegisterIncidentHandlers(IncidentService incidents)
        {
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
            McpIncidentTools.Register(
                (name, description, schema, handler) => handlers[name] = McpTestCaller.Wrap(handler),
                incidents);
            return handlers;
        }
    }
}
