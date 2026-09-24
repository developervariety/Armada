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

                using JsonDocument updateDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\",\"status\":\"Closed\",\"recoveryNotes\":\"resolved\"}");
                object updateResult = await handlers["armada_update_incident"](updateDoc.RootElement).ConfigureAwait(false);
                Incident updated = (Incident)updateResult;
                AssertEqual(IncidentStatusEnum.Closed, updated.Status);
                AssertEqual("resolved", updated.RecoveryNotes);

                using JsonDocument reopenDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\",\"status\":\"Open\"}");
                await handlers["armada_update_incident"](reopenDoc.RootElement).ConfigureAwait(false);

                using JsonDocument closeDoc = JsonDocument.Parse("{\"incidentId\":\"" + created.Id + "\",\"recoveryNotes\":\"closed by MCP\"}");
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
