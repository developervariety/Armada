namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Test.Common;

    /// <summary>
    /// One objective payload sent through REST and through the MCP objective and backlog-item tools must store
    /// the same record and be refused the same way. MCP calls go through the HTTP transport, so the argument
    /// normalizer runs as it does for a real client.
    /// </summary>
    public class ObjectiveParityTests : TestSuite
    {
        #region Public-Members

        /// <inheritdoc />
        public override string Name => "Objective Surface Parity";

        #endregion

        #region Private-Members

        private static readonly string[] _ClearableFields = new[]
        {
            "description", "category", "owner", "targetVersion", "parentObjectiveId", "refinementSummary", "suggestedPipelineId", "startFromRef"
        };

        private readonly ConfigurationSurfaces _Surfaces;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ObjectiveParityTests(ConfigurationSurfaces surfaces)
        {
            _Surfaces = surfaces ?? throw new ArgumentNullException(nameof(surfaces));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Objective update sets autoDispatchEnabled the same way on REST, update_objective and update_backlog_item", async () =>
            {
                Dictionary<string, string> ids = new Dictionary<string, string>();
                foreach (string surface in new[] { "rest", "update_objective", "update_backlog_item" })
                    ids[surface] = await CreateViaRestAsync(new { title = "Parity auto dispatch " + surface }).ConfigureAwait(false);

                SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/objectives/" + ids["rest"], ConfigurationSurfaces.Json(new { autoDispatchEnabled = true })).ConfigureAwait(false);
                SurfaceReply objective = await _Surfaces.McpAsync("update_objective", ConfigurationSurfaces.Json(new { objectiveId = ids["update_objective"], autoDispatchEnabled = true })).ConfigureAwait(false);
                SurfaceReply backlog = await _Surfaces.McpAsync("update_backlog_item", ConfigurationSurfaces.Json(new { backlogItemId = ids["update_backlog_item"], autoDispatchEnabled = true })).ConfigureAwait(false);
                AssertFalse(rest.IsError, "REST update: " + rest);
                AssertFalse(objective.IsError, "update_objective: " + objective);
                AssertFalse(backlog.IsError, "update_backlog_item: " + backlog);

                List<string> failures = new List<string>();
                foreach (KeyValuePair<string, string> entry in ids)
                {
                    string stored = ConfigurationSurfaces.Text(await ReadAsync(entry.Value).ConfigureAwait(false), "autoDispatchEnabled");
                    if (stored != "True") failures.Add(entry.Key + " stored autoDispatchEnabled=" + stored);
                }

                AssertEqual(0, failures.Count, String.Join("; ", failures));
                foreach (string id in ids.Values) await DeleteAsync(id).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("An empty string clears the same objective text fields on REST and MCP", async () =>
            {
                string parentId = await CreateViaRestAsync(new { title = "Parity parent" }).ConfigureAwait(false);
                SurfaceReply pipeline = await _Surfaces.RestAsync(HttpMethod.Get, "/api/v1/pipelines/WorkerOnly", null).ConfigureAwait(false);
                AssertFalse(pipeline.IsError, "the built-in pipeline is readable: " + pipeline);
                string pipelineId = ConfigurationSurfaces.Text(pipeline.Json(), "id");

                object populated = new
                {
                    title = "Parity clearable",
                    description = "desc",
                    category = "Backend",
                    owner = "someone",
                    targetVersion = "1.2.3",
                    parentObjectiveId = parentId,
                    refinementSummary = "summary",
                    suggestedPipelineId = pipelineId,
                    startFromRef = "main"
                };

                Dictionary<string, string> ids = new Dictionary<string, string>();
                foreach (string surface in new[] { "rest", "update_objective", "update_backlog_item" })
                {
                    ids[surface] = await CreateViaRestAsync(populated).ConfigureAwait(false);
                    JsonElement seeded = await ReadAsync(ids[surface]).ConfigureAwait(false);
                    foreach (string field in _ClearableFields)
                        AssertFalse(String.IsNullOrEmpty(ConfigurationSurfaces.Text(seeded, field)), surface + " seeded " + field);
                }

                Dictionary<string, object> clear = new Dictionary<string, object>();
                foreach (string field in _ClearableFields) clear[field] = "";

                SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/objectives/" + ids["rest"], ConfigurationSurfaces.Json(clear)).ConfigureAwait(false);
                AssertFalse(rest.IsError, "REST clear: " + rest);

                Dictionary<string, object> objectiveArgs = new Dictionary<string, object>(clear);
                objectiveArgs["objectiveId"] = ids["update_objective"];
                SurfaceReply objective = await _Surfaces.McpAsync("update_objective", ConfigurationSurfaces.Json(objectiveArgs)).ConfigureAwait(false);
                AssertFalse(objective.IsError, "update_objective clear: " + objective);

                Dictionary<string, object> backlogArgs = new Dictionary<string, object>(clear);
                backlogArgs["backlogItemId"] = ids["update_backlog_item"];
                SurfaceReply backlog = await _Surfaces.McpAsync("update_backlog_item", ConfigurationSurfaces.Json(backlogArgs)).ConfigureAwait(false);
                AssertFalse(backlog.IsError, "update_backlog_item clear: " + backlog);

                List<string> failures = new List<string>();
                foreach (KeyValuePair<string, string> entry in ids)
                {
                    JsonElement stored = await ReadAsync(entry.Value).ConfigureAwait(false);
                    foreach (string field in _ClearableFields)
                    {
                        string value = ConfigurationSurfaces.Text(stored, field);
                        if (!String.IsNullOrEmpty(value)) failures.Add(entry.Key + " kept " + field + "='" + value + "'");
                    }
                }

                AssertEqual(0, failures.Count, String.Join("; ", failures));
                foreach (string id in ids.Values) await DeleteAsync(id).ConfigureAwait(false);
                await DeleteAsync(parentId).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Objective writes refuse a missing title, an unknown enum, an unknown id and an out-of-scope link the same way on REST and MCP", async () =>
            {
                List<string> failures = new List<string>();

                SurfaceReply restNoTitle = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/objectives", ConfigurationSurfaces.Json(new { description = "untitled" })).ConfigureAwait(false);
                if (restNoTitle.Status != 400) failures.Add("REST create without title returned " + restNoTitle);
                SurfaceReply mcpNoTitle = await _Surfaces.McpAsync("create_objective", ConfigurationSurfaces.Json(new { description = "untitled" })).ConfigureAwait(false);
                if (!IsToolRefusal(mcpNoTitle, "Invalid")) failures.Add("MCP create without title returned " + mcpNoTitle);

                SurfaceReply restBadEnum = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/objectives", ConfigurationSurfaces.Json(new { title = "bad enum", status = "NotAStatus" })).ConfigureAwait(false);
                if (restBadEnum.Status != 400) failures.Add("REST create with an unknown status returned " + restBadEnum);
                SurfaceReply mcpBadEnum = await _Surfaces.McpAsync("create_backlog_item", ConfigurationSurfaces.Json(new { title = "bad enum", status = "NotAStatus" })).ConfigureAwait(false);
                if (!IsToolRefusal(mcpBadEnum, "Invalid")) failures.Add("MCP create with an unknown status returned " + mcpBadEnum);

                string missing = "obj_missing" + Guid.NewGuid().ToString("N").Substring(0, 12);
                SurfaceReply restUpdateMissing = await _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/objectives/" + missing, ConfigurationSurfaces.Json(new { title = "x" })).ConfigureAwait(false);
                if (restUpdateMissing.Status != 404) failures.Add("REST update of an unknown id returned " + restUpdateMissing);
                SurfaceReply mcpUpdateMissing = await _Surfaces.McpAsync("update_objective", ConfigurationSurfaces.Json(new { objectiveId = missing, title = "x" })).ConfigureAwait(false);
                if (!IsToolRefusal(mcpUpdateMissing, "NotFound")) failures.Add("MCP update of an unknown id returned " + mcpUpdateMissing);

                SurfaceReply restDeleteMissing = await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/objectives/" + missing, null).ConfigureAwait(false);
                if (restDeleteMissing.Status != 404) failures.Add("REST delete of an unknown id returned " + restDeleteMissing);
                SurfaceReply mcpDeleteMissing = await _Surfaces.McpAsync("delete_objective", ConfigurationSurfaces.Json(new { objectiveId = missing })).ConfigureAwait(false);
                if (!IsToolRefusal(mcpDeleteMissing, "NotFound")) failures.Add("MCP delete of an unknown id returned " + mcpDeleteMissing);

                // A link to a record the caller cannot read is an invalid field of an objective that exists: 400, not 404.
                string target = await CreateViaRestAsync(new { title = "Parity link target" }).ConfigureAwait(false);
                string foreignVessel = "vsl_missing" + Guid.NewGuid().ToString("N").Substring(0, 12);
                SurfaceReply restLink = await _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/objectives/" + target, ConfigurationSurfaces.Json(new { vesselIds = new[] { foreignVessel } })).ConfigureAwait(false);
                if (restLink.Status != 400) failures.Add("REST update with an unknown vessel link returned " + restLink);
                SurfaceReply mcpLink = await _Surfaces.McpAsync("update_objective", ConfigurationSurfaces.Json(new { objectiveId = target, vesselIds = new[] { foreignVessel } })).ConfigureAwait(false);
                if (!IsToolRefusal(mcpLink, "Invalid")) failures.Add("MCP update with an unknown vessel link returned " + mcpLink);
                await DeleteAsync(target).ConfigureAwait(false);

                AssertEqual(0, failures.Count, String.Join("; ", failures));
            }).ConfigureAwait(false);

            await RunTest("A refinement session delete is available on MCP as on REST and refuses an unknown session the same way", async () =>
            {
                string missing = "ors_missing" + Guid.NewGuid().ToString("N").Substring(0, 12);
                SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/objective-refinement-sessions/" + missing, null).ConfigureAwait(false);
                AssertEqual(404, rest.Status, "REST delete of an unknown session: " + rest);
                SurfaceReply mcp = await _Surfaces.McpAsync("delete_backlog_refinement_session", ConfigurationSurfaces.Json(new { sessionId = missing })).ConfigureAwait(false);
                AssertTrue(mcp.Status == 200 && mcp.IsError && mcp.Text.Contains("not_found", StringComparison.Ordinal), "MCP delete of an unknown session is a not_found tool error: " + mcp);
            }).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private static bool IsToolRefusal(SurfaceReply reply, string outcome)
        {
            // A refusal is a tool result carrying the outcome, not a protocol error.
            return reply.Status == 200 && reply.IsError && reply.Text.Contains("\"" + outcome + "\"", StringComparison.Ordinal);
        }

        private async Task<string> CreateViaRestAsync(object body)
        {
            SurfaceReply reply = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/objectives", ConfigurationSurfaces.Json(body)).ConfigureAwait(false);
            if (reply.IsError) throw new InvalidOperationException("Objective create failed: " + reply);
            return ConfigurationSurfaces.Text(reply.Json(), "id");
        }

        private async Task<JsonElement> ReadAsync(string id)
        {
            SurfaceReply reply = await _Surfaces.RestAsync(HttpMethod.Get, "/api/v1/objectives/" + id, null).ConfigureAwait(false);
            if (reply.IsError) throw new InvalidOperationException("Objective " + id + " could not be read: " + reply);
            return reply.Json();
        }

        private async Task DeleteAsync(string id)
        {
            await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/objectives/" + id, null).ConfigureAwait(false);
        }

        #endregion
    }
}
