namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Test.Common;

    /// <summary>
    /// One prompt-template payload sent through REST, MCP and WebSocket must store the same record and be
    /// refused the same way. An update changes an existing template only; it never creates one.
    /// </summary>
    public class PromptTemplateParityTests : TestSuite
    {
        #region Public-Members

        /// <inheritdoc />
        public override string Name => "Prompt Template Surface Parity";

        #endregion

        #region Private-Members

        private readonly ConfigurationSurfaces _Surfaces;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public PromptTemplateParityTests(ConfigurationSurfaces surfaces)
        {
            _Surfaces = surfaces ?? throw new ArgumentNullException(nameof(surfaces));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Prompt template create trims the name and category the same way on REST and MCP", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                Dictionary<string, string> names = new Dictionary<string, string>
                {
                    ["rest"] = "parity.rest." + suffix,
                    ["mcp"] = "parity.mcp." + suffix
                };

                SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/prompt-templates", ConfigurationSurfaces.Json(new { name = "  " + names["rest"] + "  ", category = " mission ", content = "body", description = " parity " })).ConfigureAwait(false);
                SurfaceReply mcp = await _Surfaces.McpAsync("create_prompt_template", ConfigurationSurfaces.Json(new { name = "  " + names["mcp"] + "  ", category = " mission ", content = "body", description = " parity " })).ConfigureAwait(false);
                AssertFalse(rest.IsError, "REST create: " + rest);
                AssertFalse(mcp.IsError, "MCP create: " + mcp);

                List<string> failures = new List<string>();
                foreach (KeyValuePair<string, string> entry in names)
                {
                    SurfaceReply stored = await _Surfaces.RestAsync(HttpMethod.Get, "/api/v1/prompt-templates/" + entry.Value, null).ConfigureAwait(false);
                    if (stored.IsError)
                    {
                        failures.Add(entry.Key + " stored no template under the trimmed name: " + stored);
                        continue;
                    }

                    string summary = Summary(stored.Json());
                    if (summary != "category=mission content=body desc=parity") failures.Add(entry.Key + " stored '" + summary + "'");
                }

                AssertEqual(0, failures.Count, String.Join("; ", failures));
            }).ConfigureAwait(false);

            await RunTest("Prompt template update stores the same fields on REST, MCP and WebSocket", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                Dictionary<string, string> names = new Dictionary<string, string>
                {
                    ["rest"] = "parity.update.rest." + suffix,
                    ["mcp"] = "parity.update.mcp." + suffix,
                    ["ws"] = "parity.update.ws." + suffix
                };
                foreach (string name in names.Values)
                {
                    SurfaceReply created = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/prompt-templates", ConfigurationSurfaces.Json(new { name = name, category = "mission", content = "original", description = "original" })).ConfigureAwait(false);
                    AssertFalse(created.IsError, "seed create: " + created);
                }

                SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/prompt-templates/" + names["rest"], ConfigurationSurfaces.Json(new { content = "changed", description = "" })).ConfigureAwait(false);
                SurfaceReply mcp = await _Surfaces.McpAsync("update_prompt_template", ConfigurationSurfaces.Json(new { name = names["mcp"], content = "changed", description = "" })).ConfigureAwait(false);
                SurfaceReply ws = await _Surfaces.WsAsync("update_prompt_template", names["ws"], ConfigurationSurfaces.Json(new { content = "changed", description = "" })).ConfigureAwait(false);
                AssertFalse(rest.IsError, "REST update: " + rest);
                AssertFalse(mcp.IsError, "MCP update: " + mcp);
                AssertFalse(ws.IsError, "WebSocket update: " + ws);

                List<string> failures = new List<string>();
                foreach (KeyValuePair<string, string> entry in names)
                {
                    SurfaceReply stored = await _Surfaces.RestAsync(HttpMethod.Get, "/api/v1/prompt-templates/" + entry.Value, null).ConfigureAwait(false);
                    string summary = Summary(stored.Json());
                    if (summary != "category=mission content=changed desc=") failures.Add(entry.Key + " stored '" + summary + "'");
                }

                AssertEqual(0, failures.Count, String.Join("; ", failures));
            }).ConfigureAwait(false);

            await RunTest("Prompt template update of a missing template is refused and creates nothing on REST, MCP and WebSocket", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                List<string> failures = new List<string>();
                Dictionary<string, SurfaceReply> replies = new Dictionary<string, SurfaceReply>
                {
                    ["rest"] = await _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/prompt-templates/parity.missing.rest." + suffix, ConfigurationSurfaces.Json(new { content = "x" })).ConfigureAwait(false),
                    ["mcp"] = await _Surfaces.McpAsync("update_prompt_template", ConfigurationSurfaces.Json(new { name = "parity.missing.mcp." + suffix, content = "x" })).ConfigureAwait(false),
                    ["ws"] = await _Surfaces.WsAsync("update_prompt_template", "parity.missing.ws." + suffix, ConfigurationSurfaces.Json(new { content = "x" })).ConfigureAwait(false)
                };

                foreach (KeyValuePair<string, SurfaceReply> entry in replies)
                {
                    if (!entry.Value.IsError) failures.Add(entry.Key + " accepted an update of a missing template: " + entry.Value);
                    SurfaceReply stored = await _Surfaces.RestAsync(HttpMethod.Get, "/api/v1/prompt-templates/parity.missing." + entry.Key + "." + suffix, null).ConfigureAwait(false);
                    if (!stored.IsError) failures.Add(entry.Key + " created the missing template");
                }

                AssertEqual(404, replies["rest"].Status, "REST answers 404");
                AssertEqual(0, failures.Count, String.Join("; ", failures));
            }).ConfigureAwait(false);

            await RunTest("Prompt template create refuses a missing category or content the same way on REST and MCP", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                List<string> failures = new List<string>();
                Dictionary<string, object> cases = new Dictionary<string, object>
                {
                    ["a missing category"] = new { name = "parity.nocategory." + suffix, content = "body" },
                    ["blank content"] = new { name = "parity.nocontent." + suffix, category = "mission", content = "   " }
                };

                foreach (KeyValuePair<string, object> entry in cases)
                {
                    SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/prompt-templates", ConfigurationSurfaces.Json(entry.Value)).ConfigureAwait(false);
                    SurfaceReply mcp = await _Surfaces.McpAsync("create_prompt_template", ConfigurationSurfaces.Json(entry.Value)).ConfigureAwait(false);
                    if (rest.Status != 400) failures.Add("REST create with " + entry.Key + " returned " + rest);
                    if (!(mcp.Status == 200 && mcp.IsError && mcp.Text.Contains("\"invalid\"", StringComparison.Ordinal))) failures.Add("MCP create with " + entry.Key + " returned " + mcp);
                }

                AssertEqual(0, failures.Count, String.Join("; ", failures));
            }).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private static string Summary(JsonElement template)
        {
            return "category=" + ConfigurationSurfaces.Text(template, "category")
                + " content=" + ConfigurationSurfaces.Text(template, "content")
                + " desc=" + ConfigurationSurfaces.Text(template, "description");
        }

        #endregion
    }
}
