namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Test.Common;

    /// <summary>
    /// One playbook payload sent through REST and MCP must store the same record and be refused the same way.
    /// An update changes only the fields it names on both surfaces.
    /// </summary>
    public class PlaybookParityTests : TestSuite
    {
        #region Public-Members

        /// <inheritdoc />
        public override string Name => "Playbook Surface Parity";

        #endregion

        #region Private-Members

        private readonly ConfigurationSurfaces _Surfaces;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public PlaybookParityTests(ConfigurationSurfaces surfaces)
        {
            _Surfaces = surfaces ?? throw new ArgumentNullException(nameof(surfaces));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Playbook create and a partial update store the same record on REST and MCP", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/playbooks", ConfigurationSurfaces.Json(new { fileName = "rest-" + suffix + ".md", content = "# body", description = "parity" })).ConfigureAwait(false);
                SurfaceReply mcp = await _Surfaces.McpAsync("create_playbook", ConfigurationSurfaces.Json(new { fileName = "mcp-" + suffix + ".md", content = "# body", description = "parity" })).ConfigureAwait(false);
                AssertFalse(rest.IsError, "REST create: " + rest);
                AssertFalse(mcp.IsError, "MCP create: " + mcp);
                Dictionary<string, string> ids = new Dictionary<string, string>
                {
                    ["rest"] = ConfigurationSurfaces.Text(rest.Json(), "id"),
                    ["mcp"] = ConfigurationSurfaces.Text(mcp.Json(), "id")
                };
                await AssertSameStoredAsync(ids, suffix, "file={surface}-" + suffix + ".md content=# body desc=parity active=True").ConfigureAwait(false);

                // A body naming only the file name renames the playbook and keeps its content and description.
                rest = await _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/playbooks/" + ids["rest"], ConfigurationSurfaces.Json(new { fileName = "rest-renamed-" + suffix + ".md" })).ConfigureAwait(false);
                mcp = await _Surfaces.McpAsync("update_playbook", ConfigurationSurfaces.Json(new { id = ids["mcp"], fileName = "mcp-renamed-" + suffix + ".md" })).ConfigureAwait(false);
                AssertFalse(rest.IsError, "REST rename: " + rest);
                AssertFalse(mcp.IsError, "MCP rename: " + mcp);
                await AssertSameStoredAsync(ids, suffix, "file={surface}-renamed-" + suffix + ".md content=# body desc=parity active=True").ConfigureAwait(false);

                // An empty description clears it on both surfaces.
                await _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/playbooks/" + ids["rest"], ConfigurationSurfaces.Json(new { description = "" })).ConfigureAwait(false);
                await _Surfaces.McpAsync("update_playbook", ConfigurationSurfaces.Json(new { id = ids["mcp"], description = "" })).ConfigureAwait(false);
                await AssertSameStoredAsync(ids, suffix, "file={surface}-renamed-" + suffix + ".md content=# body desc= active=True").ConfigureAwait(false);

                foreach (string id in ids.Values)
                    await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/playbooks/" + id, null).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Playbook create takes the id and timestamps from the server, never the body", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                string forgedId = "pbk_forged" + suffix;
                SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/playbooks", ConfigurationSurfaces.Json(new
                {
                    id = forgedId,
                    fileName = "forged-" + suffix + ".md",
                    content = "# body",
                    tenantId = "ten_forged",
                    createdUtc = "2001-01-01T00:00:00Z"
                })).ConfigureAwait(false);
                AssertFalse(rest.IsError, "REST create: " + rest);
                string id = ConfigurationSurfaces.Text(rest.Json(), "id");
                AssertFalse(String.Equals(forgedId, id, StringComparison.Ordinal), "the stored id is generated, not taken from the body");
                SurfaceReply stored = await _Surfaces.RestAsync(HttpMethod.Get, "/api/v1/playbooks/" + id, null).ConfigureAwait(false);
                AssertFalse(ConfigurationSurfaces.Text(stored.Json(), "createdUtc").StartsWith("2001", StringComparison.Ordinal), "the creation time comes from the server");
                AssertFalse(String.Equals("ten_forged", ConfigurationSurfaces.Text(stored.Json(), "tenantId"), StringComparison.Ordinal), "the tenant comes from the caller");
                await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/playbooks/" + id, null).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Playbook writes refuse a bad file name, missing content and a duplicate file name the same way on REST and MCP", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                List<string> failures = new List<string>();

                Dictionary<string, object> invalid = new Dictionary<string, object>
                {
                    ["a file name without .md"] = new { fileName = "notes-" + suffix + ".txt", content = "# body" },
                    ["missing content"] = new { fileName = "empty-" + suffix + ".md" }
                };
                foreach (KeyValuePair<string, object> entry in invalid)
                {
                    SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/playbooks", ConfigurationSurfaces.Json(entry.Value)).ConfigureAwait(false);
                    SurfaceReply mcp = await _Surfaces.McpAsync("create_playbook", ConfigurationSurfaces.Json(entry.Value)).ConfigureAwait(false);
                    if (rest.Status != 400) failures.Add("REST create with " + entry.Key + " returned " + rest);
                    if (!(mcp.Status == 200 && mcp.IsError && mcp.Text.Contains("\"invalid\"", StringComparison.Ordinal))) failures.Add("MCP create with " + entry.Key + " returned " + mcp);
                }

                string taken = "taken-" + suffix + ".md";
                SurfaceReply first = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/playbooks", ConfigurationSurfaces.Json(new { fileName = taken, content = "# body" })).ConfigureAwait(false);
                AssertFalse(first.IsError, "seed create: " + first);
                SurfaceReply restDuplicate = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/playbooks", ConfigurationSurfaces.Json(new { fileName = taken, content = "# body" })).ConfigureAwait(false);
                SurfaceReply mcpDuplicate = await _Surfaces.McpAsync("create_playbook", ConfigurationSurfaces.Json(new { fileName = taken, content = "# body" })).ConfigureAwait(false);
                if (restDuplicate.Status != 409) failures.Add("REST duplicate create returned " + restDuplicate);
                if (!(mcpDuplicate.IsError && mcpDuplicate.Text.Contains("\"conflict\"", StringComparison.Ordinal))) failures.Add("MCP duplicate create returned " + mcpDuplicate);
                await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/playbooks/" + ConfigurationSurfaces.Text(first.Json(), "id"), null).ConfigureAwait(false);

                AssertEqual(0, failures.Count, String.Join("; ", failures));
            }).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private async Task AssertSameStoredAsync(Dictionary<string, string> ids, string suffix, string expectedTemplate)
        {
            List<string> failures = new List<string>();
            foreach (KeyValuePair<string, string> entry in ids)
            {
                SurfaceReply stored = await _Surfaces.RestAsync(HttpMethod.Get, "/api/v1/playbooks/" + entry.Value, null).ConfigureAwait(false);
                if (stored.IsError) throw new InvalidOperationException("Playbook " + entry.Value + " could not be read: " + stored);
                JsonElement playbook = stored.Json();
                string summary = "file=" + ConfigurationSurfaces.Text(playbook, "fileName")
                    + " content=" + ConfigurationSurfaces.Text(playbook, "content")
                    + " desc=" + ConfigurationSurfaces.Text(playbook, "description")
                    + " active=" + ConfigurationSurfaces.Text(playbook, "active");
                string expected = expectedTemplate.Replace("{surface}", entry.Key, StringComparison.Ordinal);
                if (!String.Equals(expected, summary, StringComparison.Ordinal)) failures.Add(entry.Key + " stored '" + summary + "'");
            }

            AssertEqual(0, failures.Count, String.Join("; ", failures));
        }

        #endregion
    }
}
