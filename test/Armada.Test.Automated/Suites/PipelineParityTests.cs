namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Test.Common;

    /// <summary>
    /// One pipeline payload sent through REST, MCP and WebSocket must store the same record and be refused
    /// the same way. Each surface writes a record of its own name; the stored records are then read back
    /// through one path and compared field by field.
    /// </summary>
    public class PipelineParityTests : TestSuite
    {
        #region Public-Members

        /// <inheritdoc />
        public override string Name => "Pipeline Surface Parity";

        #endregion

        #region Private-Members

        private readonly ConfigurationSurfaces _Surfaces;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public PipelineParityTests(ConfigurationSurfaces surfaces)
        {
            _Surfaces = surfaces ?? throw new ArgumentNullException(nameof(surfaces));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Pipeline create and update store the same stages, review gate included, on every surface", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                object createStages = new object[]
                {
                    new { personaName = "Worker" },
                    new { personaName = "Judge", requiresReview = true, reviewDenyAction = "FailPipeline", preferredModel = "high", description = "review" }
                };

                Dictionary<string, string> names = new Dictionary<string, string>
                {
                    ["rest"] = "ParityRest-" + suffix,
                    ["mcp"] = "ParityMcp-" + suffix,
                    ["ws"] = "ParityWs-" + suffix
                };

                SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/pipelines", ConfigurationSurfaces.Json(new { name = names["rest"], description = "parity", stages = createStages })).ConfigureAwait(false);
                SurfaceReply mcp = await _Surfaces.McpAsync("create_pipeline", ConfigurationSurfaces.Json(new { name = names["mcp"], description = "parity", stages = createStages })).ConfigureAwait(false);
                SurfaceReply ws = await _Surfaces.WsAsync("create_pipeline", null, ConfigurationSurfaces.Json(new { name = names["ws"], description = "parity", stages = createStages })).ConfigureAwait(false);
                AssertFalse(rest.IsError, "REST create: " + rest);
                AssertFalse(mcp.IsError, "MCP create: " + mcp);
                AssertFalse(ws.IsError, "WebSocket create: " + ws);

                await AssertSameStoredAsync(names, "Worker#1 optional=False review=False deny=RetryStage model= desc=|Judge#2 optional=False review=True deny=FailPipeline model=high desc=review", "parity").ConfigureAwait(false);

                // The replacement list names no review field: the gate the Judge stage carries must survive.
                object updateStages = new object[] { new { personaName = "Worker" }, new { personaName = "Judge" } };
                rest = await _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/pipelines/" + names["rest"], ConfigurationSurfaces.Json(new { description = "changed", stages = updateStages })).ConfigureAwait(false);
                mcp = await _Surfaces.McpAsync("update_pipeline", ConfigurationSurfaces.Json(new { name = names["mcp"], description = "changed", stages = updateStages })).ConfigureAwait(false);
                ws = await _Surfaces.WsAsync("update_pipeline", names["ws"], ConfigurationSurfaces.Json(new { description = "changed", stages = updateStages })).ConfigureAwait(false);
                AssertFalse(rest.IsError, "REST update: " + rest);
                AssertFalse(mcp.IsError, "MCP update: " + mcp);
                AssertFalse(ws.IsError, "WebSocket update: " + ws);

                await AssertSameStoredAsync(names, "Worker#1 optional=False review=False deny=RetryStage model= desc=|Judge#2 optional=False review=True deny=FailPipeline model=high desc=review", "changed").ConfigureAwait(false);

                // An explicit value still turns the gate off, the same way on every surface.
                object gateOff = new object[] { new { personaName = "Worker" }, new { personaName = "Judge", requiresReview = false, preferredModel = (string?)null } };
                await _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/pipelines/" + names["rest"], ConfigurationSurfaces.Json(new { stages = gateOff })).ConfigureAwait(false);
                await _Surfaces.McpAsync("update_pipeline", ConfigurationSurfaces.Json(new { name = names["mcp"], stages = gateOff })).ConfigureAwait(false);
                await _Surfaces.WsAsync("update_pipeline", names["ws"], ConfigurationSurfaces.Json(new { stages = gateOff })).ConfigureAwait(false);
                await AssertSameStoredAsync(names, "Worker#1 optional=False review=False deny=RetryStage model= desc=|Judge#2 optional=False review=False deny=FailPipeline model= desc=review", "changed").ConfigureAwait(false);

                foreach (string name in names.Values)
                    await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/pipelines/" + name, null).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Pipeline stages that share an order are stored as parallel siblings on every surface", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                object stages = new object[]
                {
                    new { personaName = "Worker", order = 1 },
                    new { personaName = "TestEngineer", order = 2 },
                    new { personaName = "Linter", order = 2 },
                    new { personaName = "Judge", order = 3 }
                };

                Dictionary<string, string> names = new Dictionary<string, string>
                {
                    ["rest"] = "ParityParallelRest-" + suffix,
                    ["mcp"] = "ParityParallelMcp-" + suffix,
                    ["ws"] = "ParityParallelWs-" + suffix
                };

                AssertFalse((await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/pipelines", ConfigurationSurfaces.Json(new { name = names["rest"], description = "parallel", stages = stages })).ConfigureAwait(false)).IsError, "REST create");
                AssertFalse((await _Surfaces.McpAsync("create_pipeline", ConfigurationSurfaces.Json(new { name = names["mcp"], description = "parallel", stages = stages })).ConfigureAwait(false)).IsError, "MCP create");
                AssertFalse((await _Surfaces.WsAsync("create_pipeline", null, ConfigurationSurfaces.Json(new { name = names["ws"], description = "parallel", stages = stages })).ConfigureAwait(false)).IsError, "WebSocket create");

                await AssertSameStoredAsync(names, "Worker#1 optional=False review=False deny=RetryStage model= desc=|TestEngineer#2 optional=False review=False deny=RetryStage model= desc=|Linter#2 optional=False review=False deny=RetryStage model= desc=|Judge#3 optional=False review=False deny=RetryStage model= desc=", "parallel").ConfigureAwait(false);

                foreach (string name in names.Values)
                    await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/pipelines/" + name, null).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Pipeline writes refuse a missing name, empty stages, a nameless stage and a partly ordered list on every surface", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                List<string> failures = new List<string>();

                string noName = ConfigurationSurfaces.Json(new { description = "nameless", stages = new object[] { new { personaName = "Worker" } } });
                await ExpectRefusedAsync(failures, "create without name", noName, null).ConfigureAwait(false);

                string emptyStages = ConfigurationSurfaces.Json(new { name = "ParityEmpty-" + suffix, stages = new object[0] });
                await ExpectRefusedAsync(failures, "create with empty stages", emptyStages, "ParityEmpty-" + suffix).ConfigureAwait(false);

                string namelessStage = ConfigurationSurfaces.Json(new { name = "ParityNoPersona-" + suffix, stages = new object[] { new { description = "who" } } });
                await ExpectRefusedAsync(failures, "create with a stage that names no persona", namelessStage, "ParityNoPersona-" + suffix).ConfigureAwait(false);

                string mixedOrder = ConfigurationSurfaces.Json(new { name = "ParityMixedOrder-" + suffix, stages = new object[] { new { personaName = "Worker", order = 1 }, new { personaName = "Judge" } } });
                await ExpectRefusedAsync(failures, "create that orders only some stages", mixedOrder, "ParityMixedOrder-" + suffix).ConfigureAwait(false);

                // An empty replacement list is refused and the stored stages stay.
                string target = "ParityKeep-" + suffix;
                SurfaceReply created = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/pipelines", ConfigurationSurfaces.Json(new { name = target, stages = new object[] { new { personaName = "Worker" } } })).ConfigureAwait(false);
                AssertFalse(created.IsError, "seed create: " + created);
                SurfaceReply restEmpty = await _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/pipelines/" + target, ConfigurationSurfaces.Json(new { stages = new object[0] })).ConfigureAwait(false);
                SurfaceReply mcpEmpty = await _Surfaces.McpAsync("update_pipeline", ConfigurationSurfaces.Json(new { name = target, stages = new object[0] })).ConfigureAwait(false);
                SurfaceReply wsEmpty = await _Surfaces.WsAsync("update_pipeline", target, ConfigurationSurfaces.Json(new { stages = new object[0] })).ConfigureAwait(false);
                if (!restEmpty.IsError) failures.Add("REST accepted an empty stage list on update: " + restEmpty);
                if (!mcpEmpty.IsError) failures.Add("MCP accepted an empty stage list on update: " + mcpEmpty);
                if (!wsEmpty.IsError) failures.Add("WebSocket accepted an empty stage list on update: " + wsEmpty);
                string stored = StageSummary(await ReadStoredAsync(target).ConfigureAwait(false));
                if (!stored.StartsWith("Worker#1", StringComparison.Ordinal)) failures.Add("a refused update changed the stored stages to '" + stored + "'");
                await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/pipelines/" + target, null).ConfigureAwait(false);

                AssertEqual(0, failures.Count, String.Join("; ", failures));
            }).ConfigureAwait(false);

            await RunTest("Pipeline create takes identity, ownership and built-in status from the server, never the body", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                string forgedId = "ppl_forged" + suffix;
                string name = "ParityForged-" + suffix;
                string body = ConfigurationSurfaces.Json(new
                {
                    id = forgedId,
                    name = name,
                    isBuiltIn = true,
                    tenantId = "ten_forged",
                    userId = "usr_forged",
                    createdUtc = "2001-01-01T00:00:00Z",
                    stages = new object[] { new { personaName = "Worker", id = "pps_forged" + suffix, pipelineId = "ppl_other" } }
                });

                SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/pipelines", body).ConfigureAwait(false);
                AssertFalse(rest.IsError, "REST create: " + rest);
                JsonElement stored = await ReadStoredAsync(name).ConfigureAwait(false);
                AssertFalse(String.Equals(forgedId, ConfigurationSurfaces.Text(stored, "id"), StringComparison.Ordinal), "the stored id is generated, not taken from the body");
                AssertEqual("False", ConfigurationSurfaces.Text(stored, "isBuiltIn"), "a request never creates a built-in pipeline");
                AssertFalse(String.Equals("ten_forged", ConfigurationSurfaces.Text(stored, "tenantId"), StringComparison.Ordinal), "the tenant comes from the caller");
                AssertFalse(ConfigurationSurfaces.Text(stored, "createdUtc").StartsWith("2001", StringComparison.Ordinal), "the creation time comes from the server");
                await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/pipelines/" + name, null).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("A global administrator's pipeline update by name reaches its own tenant's record, not another tenant's", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                string name = "ParityShared-" + suffix;
                string tenantToken = await _Surfaces.CreateTenantAdministratorTokenAsync("pipeline-parity").ConfigureAwait(false);

                // The other tenant's record is created first, so an unscoped read by name finds it first.
                SurfaceReply tenantCreate = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/pipelines", ConfigurationSurfaces.Json(new { name = name, description = "tenant", stages = new object[] { new { personaName = "Worker" } } }), tenantToken).ConfigureAwait(false);
                AssertFalse(tenantCreate.IsError, "tenant create: " + tenantCreate);
                SurfaceReply adminCreate = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/pipelines", ConfigurationSurfaces.Json(new { name = name, description = "admin", stages = new object[] { new { personaName = "Worker" } } })).ConfigureAwait(false);
                AssertFalse(adminCreate.IsError, "admin create: " + adminCreate);

                SurfaceReply update = await _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/pipelines/" + name, ConfigurationSurfaces.Json(new { description = "admin-updated" })).ConfigureAwait(false);
                AssertFalse(update.IsError, "admin update: " + update);

                SurfaceReply tenantView = await _Surfaces.RestAsync(HttpMethod.Get, "/api/v1/pipelines/" + name, null, tenantToken).ConfigureAwait(false);
                AssertEqual("tenant", ConfigurationSurfaces.Text(tenantView.Json(), "description"), "the other tenant's record is untouched");
                AssertEqual("admin-updated", ConfigurationSurfaces.Text(await ReadStoredAsync(name).ConfigureAwait(false), "description"), "the administrator's own record changed");

                SurfaceReply delete = await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/pipelines/" + name, null).ConfigureAwait(false);
                AssertFalse(delete.IsError, "admin delete: " + delete);
                tenantView = await _Surfaces.RestAsync(HttpMethod.Get, "/api/v1/pipelines/" + name, null, tenantToken).ConfigureAwait(false);
                AssertFalse(tenantView.IsError, "a global administrator's delete by name leaves the other tenant's record: " + tenantView);
                await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/pipelines/" + name, null, tenantToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private async Task ExpectRefusedAsync(List<string> failures, string label, string body, string? name)
        {
            SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/pipelines", body).ConfigureAwait(false);
            if (!rest.IsError) failures.Add("REST accepted " + label + ": " + rest);
            if (rest.Status >= 500) failures.Add("REST failed " + label + " with a server error: " + rest);
            if (!rest.IsError && name != null) await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/pipelines/" + name, null).ConfigureAwait(false);

            SurfaceReply mcp = await _Surfaces.McpAsync("create_pipeline", body).ConfigureAwait(false);
            if (!mcp.IsError) failures.Add("MCP accepted " + label + ": " + mcp);
            if (!mcp.IsError && name != null) await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/pipelines/" + name, null).ConfigureAwait(false);

            SurfaceReply ws = await _Surfaces.WsAsync("create_pipeline", null, body).ConfigureAwait(false);
            if (!ws.IsError) failures.Add("WebSocket accepted " + label + ": " + ws);
            if (!ws.IsError && name != null) await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/pipelines/" + name, null).ConfigureAwait(false);
        }

        private async Task AssertSameStoredAsync(Dictionary<string, string> names, string expectedStages, string expectedDescription)
        {
            List<string> failures = new List<string>();
            foreach (KeyValuePair<string, string> entry in names)
            {
                JsonElement stored = await ReadStoredAsync(entry.Value).ConfigureAwait(false);
                string stages = StageSummary(stored);
                if (!String.Equals(expectedStages, stages, StringComparison.Ordinal))
                    failures.Add(entry.Key + " stored stages '" + stages + "'");
                string description = ConfigurationSurfaces.Text(stored, "description");
                if (!String.Equals(expectedDescription, description, StringComparison.Ordinal))
                    failures.Add(entry.Key + " stored description '" + description + "'");
            }

            AssertEqual(0, failures.Count, "expected stages '" + expectedStages + "', but " + String.Join("; ", failures));
        }

        private async Task<JsonElement> ReadStoredAsync(string name)
        {
            SurfaceReply reply = await _Surfaces.RestAsync(HttpMethod.Get, "/api/v1/pipelines/" + name, null).ConfigureAwait(false);
            if (reply.IsError) throw new InvalidOperationException("Pipeline " + name + " could not be read: " + reply);
            return reply.Json();
        }

        private static string StageSummary(JsonElement pipeline)
        {
            List<JsonElement> stages = new List<JsonElement>();
            if (ConfigurationSurfaces.TryProp(pipeline, "stages", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement stage in list.EnumerateArray()) stages.Add(stage);
            }

            stages.Sort((a, b) => Int32.Parse(ConfigurationSurfaces.Text(a, "order")).CompareTo(Int32.Parse(ConfigurationSurfaces.Text(b, "order"))));
            List<string> parts = new List<string>();
            foreach (JsonElement stage in stages)
            {
                parts.Add(ConfigurationSurfaces.Text(stage, "personaName") + "#" + ConfigurationSurfaces.Text(stage, "order")
                    + " optional=" + ConfigurationSurfaces.Text(stage, "isOptional")
                    + " review=" + ConfigurationSurfaces.Text(stage, "requiresReview")
                    + " deny=" + ConfigurationSurfaces.Text(stage, "reviewDenyAction")
                    + " model=" + ConfigurationSurfaces.Text(stage, "preferredModel")
                    + " desc=" + ConfigurationSurfaces.Text(stage, "description"));
            }

            return String.Join("|", parts);
        }

        #endregion
    }
}
