namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Test.Common;

    /// <summary>
    /// One persona payload sent through REST, MCP and WebSocket must store the same record and be refused the
    /// same way. Each surface writes a record of its own name; the stored records are read back through one
    /// path and compared field by field.
    /// </summary>
    public class PersonaParityTests : TestSuite
    {
        #region Public-Members

        /// <inheritdoc />
        public override string Name => "Persona Surface Parity";

        #endregion

        #region Private-Members

        private readonly ConfigurationSurfaces _Surfaces;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public PersonaParityTests(ConfigurationSurfaces surfaces)
        {
            _Surfaces = surfaces ?? throw new ArgumentNullException(nameof(surfaces));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Persona create and update store the same fields, default playbooks included, on every surface", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                Dictionary<string, string> names = new Dictionary<string, string>
                {
                    ["rest"] = "ParityPersonaRest-" + suffix,
                    ["mcp"] = "ParityPersonaMcp-" + suffix,
                    ["ws"] = "ParityPersonaWs-" + suffix
                };

                foreach (KeyValuePair<string, string> entry in names)
                {
                    string body = ConfigurationSurfaces.Json(new { name = entry.Value, promptTemplateName = "persona.worker", description = "parity", minimumTier = "Standard" });
                    SurfaceReply created = await CreateAsync(entry.Key, body).ConfigureAwait(false);
                    AssertFalse(created.IsError, entry.Key + " create: " + created);
                }

                await AssertSameStoredAsync(names, "desc=parity template=persona.worker tier=Standard playbooks= active=True").ConfigureAwait(false);

                object update = new
                {
                    description = "changed",
                    defaultPlaybooks = new object[] { new { playbookId = "pbk_parity" + suffix, deliveryMode = "InstructionWithReference" } }
                };
                foreach (KeyValuePair<string, string> entry in names)
                {
                    SurfaceReply updated = await UpdateAsync(entry.Key, entry.Value, update).ConfigureAwait(false);
                    AssertFalse(updated.IsError, entry.Key + " update: " + updated);
                }

                await AssertSameStoredAsync(names, "desc=changed template=persona.worker tier=Standard playbooks=pbk_parity" + suffix + ":InstructionWithReference active=True").ConfigureAwait(false);

                // An empty description and an empty playbook list clear both, the same way on every surface.
                foreach (KeyValuePair<string, string> entry in names)
                    await UpdateAsync(entry.Key, entry.Value, new { description = "", defaultPlaybooks = new object[0] }).ConfigureAwait(false);
                await AssertSameStoredAsync(names, "desc= template=persona.worker tier=Standard playbooks= active=True").ConfigureAwait(false);

                foreach (string name in names.Values)
                    await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/personas/" + name, null).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Persona writes refuse a missing name, a missing prompt template and the retired specialist flag on every surface", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                List<string> failures = new List<string>();
                Dictionary<string, string> cases = new Dictionary<string, string>
                {
                    ["an empty body"] = "{}",
                    ["a missing name"] = ConfigurationSurfaces.Json(new { promptTemplateName = "persona.worker" }),
                    ["a missing prompt template"] = ConfigurationSurfaces.Json(new { name = "ParityPersonaNoTemplate-" + suffix }),
                    ["the specialist flag"] = ConfigurationSurfaces.Json(new { name = "ParityPersonaSpecialist-" + suffix, promptTemplateName = "persona.worker", specialist = true })
                };

                foreach (KeyValuePair<string, string> entry in cases)
                {
                    foreach (string surface in new[] { "rest", "mcp", "ws" })
                    {
                        SurfaceReply reply = await CreateAsync(surface, entry.Value).ConfigureAwait(false);
                        if (!reply.IsError) failures.Add(surface + " accepted " + entry.Key + ": " + reply);
                        if (reply.Status >= 500) failures.Add(surface + " failed " + entry.Key + " with a server error: " + reply);
                    }
                }

                foreach (string name in new[] { "ParityPersonaNoTemplate-" + suffix, "ParityPersonaSpecialist-" + suffix })
                {
                    SurfaceReply stored = await _Surfaces.RestAsync(HttpMethod.Get, "/api/v1/personas/" + name, null).ConfigureAwait(false);
                    if (!stored.IsError)
                    {
                        failures.Add("a refused create stored " + name);
                        await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/personas/" + name, null).ConfigureAwait(false);
                    }
                }

                AssertEqual(0, failures.Count, String.Join("; ", failures));
            }).ConfigureAwait(false);

            await RunTest("Persona create takes identity, ownership and built-in status from the server, never the body", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                string forgedId = "prs_forged" + suffix;
                string name = "ParityPersonaForged-" + suffix;
                SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/personas", ConfigurationSurfaces.Json(new
                {
                    id = forgedId,
                    name = name,
                    promptTemplateName = "persona.worker",
                    isBuiltIn = true,
                    tenantId = "ten_forged",
                    createdUtc = "2001-01-01T00:00:00Z"
                })).ConfigureAwait(false);
                AssertFalse(rest.IsError, "REST create: " + rest);
                JsonElement stored = await ReadStoredAsync(name).ConfigureAwait(false);
                AssertFalse(String.Equals(forgedId, ConfigurationSurfaces.Text(stored, "id"), StringComparison.Ordinal), "the stored id is generated, not taken from the body");
                AssertEqual("False", ConfigurationSurfaces.Text(stored, "isBuiltIn"), "a request never creates a built-in persona");
                AssertFalse(ConfigurationSurfaces.Text(stored, "createdUtc").StartsWith("2001", StringComparison.Ordinal), "the creation time comes from the server");
                await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/personas/" + name, null).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("A global administrator's persona update and delete by name reach its own tenant's record, not another tenant's", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                string name = "ParityPersonaShared-" + suffix;
                string tenantToken = await _Surfaces.CreateTenantAdministratorTokenAsync("persona-parity").ConfigureAwait(false);

                // The other tenant's record is created first, so an unscoped read by name finds it first.
                SurfaceReply tenantCreate = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/personas", ConfigurationSurfaces.Json(new { name = name, promptTemplateName = "persona.worker", description = "tenant" }), tenantToken).ConfigureAwait(false);
                AssertFalse(tenantCreate.IsError, "tenant create: " + tenantCreate);
                SurfaceReply adminCreate = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/personas", ConfigurationSurfaces.Json(new { name = name, promptTemplateName = "persona.worker", description = "admin" })).ConfigureAwait(false);
                AssertFalse(adminCreate.IsError, "admin create: " + adminCreate);

                SurfaceReply update = await _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/personas/" + name, ConfigurationSurfaces.Json(new { description = "admin-updated" })).ConfigureAwait(false);
                AssertFalse(update.IsError, "admin update: " + update);
                SurfaceReply tenantView = await _Surfaces.RestAsync(HttpMethod.Get, "/api/v1/personas/" + name, null, tenantToken).ConfigureAwait(false);
                AssertEqual("tenant", ConfigurationSurfaces.Text(tenantView.Json(), "description"), "the other tenant's record is untouched");
                AssertEqual("admin-updated", ConfigurationSurfaces.Text(await ReadStoredAsync(name).ConfigureAwait(false), "description"), "the administrator's own record changed");

                SurfaceReply delete = await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/personas/" + name, null).ConfigureAwait(false);
                AssertFalse(delete.IsError, "admin delete: " + delete);
                tenantView = await _Surfaces.RestAsync(HttpMethod.Get, "/api/v1/personas/" + name, null, tenantToken).ConfigureAwait(false);
                AssertFalse(tenantView.IsError, "a global administrator's delete by name leaves the other tenant's record: " + tenantView);
                await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/personas/" + name, null, tenantToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private Task<SurfaceReply> CreateAsync(string surface, string body)
        {
            if (surface == "rest") return _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/personas", body);
            if (surface == "mcp") return _Surfaces.McpAsync("create_persona", body);
            return _Surfaces.WsAsync("create_persona", null, body);
        }

        private Task<SurfaceReply> UpdateAsync(string surface, string name, object fields)
        {
            if (surface == "rest") return _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/personas/" + name, ConfigurationSurfaces.Json(fields));
            if (surface == "ws") return _Surfaces.WsAsync("update_persona", name, ConfigurationSurfaces.Json(fields));

            Dictionary<string, object?> args = JsonSerializer.Deserialize<Dictionary<string, object?>>(ConfigurationSurfaces.Json(fields)) ?? new Dictionary<string, object?>();
            args["name"] = name;
            return _Surfaces.McpAsync("update_persona", ConfigurationSurfaces.Json(args));
        }

        private async Task AssertSameStoredAsync(Dictionary<string, string> names, string expected)
        {
            List<string> failures = new List<string>();
            foreach (KeyValuePair<string, string> entry in names)
            {
                string summary = Summary(await ReadStoredAsync(entry.Value).ConfigureAwait(false));
                if (!String.Equals(expected, summary, StringComparison.Ordinal)) failures.Add(entry.Key + " stored '" + summary + "'");
            }

            AssertEqual(0, failures.Count, "expected '" + expected + "', but " + String.Join("; ", failures));
        }

        private async Task<JsonElement> ReadStoredAsync(string name)
        {
            SurfaceReply reply = await _Surfaces.RestAsync(HttpMethod.Get, "/api/v1/personas/" + name, null).ConfigureAwait(false);
            if (reply.IsError) throw new InvalidOperationException("Persona " + name + " could not be read: " + reply);
            return reply.Json();
        }

        private static string Summary(JsonElement persona)
        {
            List<string> playbooks = new List<string>();
            string stored = ConfigurationSurfaces.Text(persona, "defaultPlaybooks");
            if (!String.IsNullOrWhiteSpace(stored))
            {
                using (JsonDocument document = JsonDocument.Parse(stored))
                {
                    foreach (JsonElement entry in document.RootElement.EnumerateArray())
                        playbooks.Add(ConfigurationSurfaces.Text(entry, "playbookId") + ":" + ConfigurationSurfaces.Text(entry, "deliveryMode"));
                }
            }

            return "desc=" + ConfigurationSurfaces.Text(persona, "description")
                + " template=" + ConfigurationSurfaces.Text(persona, "promptTemplateName")
                + " tier=" + ConfigurationSurfaces.Text(persona, "minimumTier")
                + " playbooks=" + String.Join(",", playbooks)
                + " active=" + ConfigurationSurfaces.Text(persona, "active");
        }

        #endregion
    }
}
