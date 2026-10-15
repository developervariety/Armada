namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// REST regressions for names with spaces in the path, partial captain and persona updates that keep omitted
    /// fields, and the RequireHarborForLaunch server setting.
    /// </summary>
    public sealed class PartialUpdateApiSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>Build the descriptors.</summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            return new TestSuiteDescriptor("E2E.PartialUpdateApi", "Named routes and partial updates", new List<TestCaseDescriptor>
            {
                Case("persona_name_with_space_get_put_delete", "A persona named with a space can be read, updated and deleted", PersonaNameWithSpaceAsync),
                Case("persona_put_keeps_omitted_default_captain", "Persona PUT without DefaultCaptainId keeps it; explicit null clears it", PersonaDefaultCaptainAsync),
                Case("captain_put_keeps_omitted_fields", "Captain PUT keeps personas and other fields the body omits", CaptainPartialUpdateAsync),
                Case("captain_merge_keeps_omitted_model_endpoint", "Captain merge keeps ModelEndpointId when the body omits it", CaptainMergeModelEndpointAsync),
                Case("settings_require_harbor_for_launch_roundtrip", "RequireHarborForLaunch is returned by GET and changed by PUT settings", RequireHarborSettingAsync),
            });
        }

        #endregion

        #region Private-Methods

        private TestCaseDescriptor Case(string id, string name, Func<CancellationToken, Task> body)
        {
            return new TestCaseDescriptor("E2E.PartialUpdateApi", id, name, body);
        }

        private async Task PersonaNameWithSpaceAsync(CancellationToken token)
        {
            E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
            HttpClient client = fixture.AuthClient;
            string name = "Spaced Persona " + Guid.NewGuid().ToString("N").Substring(0, 8);
            string path = "/api/v1/personas/" + Uri.EscapeDataString(name);
            using (StringContent content = JsonHelper.ToJsonContent(new { Name = name, PromptTemplateName = "persona.worker", Description = "before" }))
            using (HttpResponseMessage created = await client.PostAsync("/api/v1/personas", content, token).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync(token).ConfigureAwait(false));

            using (HttpResponseMessage get = await client.GetAsync(path, token).ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.OK, get.StatusCode, "GET " + path);
                Persona read = await JsonHelper.DeserializeAsync<Persona>(get).ConfigureAwait(false);
                AssertEqual(name, read.Name);
            }

            using (StringContent content = JsonHelper.ToJsonContent(new { Description = "after" }))
            using (HttpResponseMessage put = await client.PutAsync(path, content, token).ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.OK, put.StatusCode, "PUT " + path);
                Persona updated = await JsonHelper.DeserializeAsync<Persona>(put).ConfigureAwait(false);
                AssertEqual("after", updated.Description);
            }

            using (HttpResponseMessage deleted = await client.DeleteAsync(path, token).ConfigureAwait(false))
                AssertTrue(deleted.IsSuccessStatusCode, "DELETE " + path + " returned " + (int)deleted.StatusCode);
            using (HttpResponseMessage gone = await client.GetAsync(path, token).ConfigureAwait(false))
                AssertEqual(HttpStatusCode.NotFound, gone.StatusCode);
        }

        private async Task PersonaDefaultCaptainAsync(CancellationToken token)
        {
            E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
            HttpClient client = fixture.AuthClient;
            Captain captain = await CreateCaptainAsync(client, new { Name = "default-cpt-" + Guid.NewGuid().ToString("N").Substring(0, 8), Runtime = "ClaudeCode" }, token).ConfigureAwait(false);
            string name = "Default Captain Persona " + Guid.NewGuid().ToString("N").Substring(0, 8);
            string path = "/api/v1/personas/" + Uri.EscapeDataString(name);
            try
            {
                using (StringContent content = JsonHelper.ToJsonContent(new { Name = name, PromptTemplateName = "persona.worker", DefaultCaptainId = captain.Id }))
                using (HttpResponseMessage created = await client.PostAsync("/api/v1/personas", content, token).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync(token).ConfigureAwait(false));

                // The Personas list editor sends name, description, template, scope and routing, not the default captain.
                using (StringContent content = JsonHelper.ToJsonContent(new { Description = "list edit", PromptTemplateName = "persona.worker" }))
                using (HttpResponseMessage put = await client.PutAsync(path, content, token).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.OK, put.StatusCode);
                Persona kept = await ReadPersonaAsync(client, path, token).ConfigureAwait(false);
                AssertEqual(captain.Id, kept.DefaultCaptainId, "an omitted DefaultCaptainId is kept");
                AssertEqual("list edit", kept.Description);

                using (StringContent content = RawJson("{\"defaultCaptainId\":null}"))
                using (HttpResponseMessage put = await client.PutAsync(path, content, token).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.OK, put.StatusCode);
                Persona cleared = await ReadPersonaAsync(client, path, token).ConfigureAwait(false);
                AssertNull(cleared.DefaultCaptainId, "an explicit null clears DefaultCaptainId");
            }
            finally
            {
                using (HttpResponseMessage deleted = await client.DeleteAsync(path, token).ConfigureAwait(false)) { }
                using (HttpResponseMessage deleted = await client.DeleteAsync("/api/v1/captains/" + captain.Id, token).ConfigureAwait(false)) { }
            }
        }

        private async Task CaptainPartialUpdateAsync(CancellationToken token)
        {
            E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
            HttpClient client = fixture.AuthClient;
            Captain captain = await CreateCaptainAsync(client, new
            {
                Name = "partial-cpt-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Runtime = "Codex",
                SystemInstructions = "be brief",
                AllowedPersonas = "[\"Worker\",\"Judge\"]",
                PreferredPersona = "Worker",
                Tier = "Premium",
                ReasoningEffort = "High"
            }, token).ConfigureAwait(false);
            string path = "/api/v1/captains/" + captain.Id;
            try
            {
                // The Captains list editor did not carry the persona fields.
                string renamed = captain.Name + "-renamed";
                using (StringContent content = JsonHelper.ToJsonContent(new { Name = renamed, Runtime = "Codex" }))
                using (HttpResponseMessage put = await client.PutAsync(path, content, token).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.OK, put.StatusCode, await put.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                Captain kept = await ReadCaptainAsync(client, path, token).ConfigureAwait(false);
                AssertEqual(renamed, kept.Name);
                AssertEqual("[\"Worker\",\"Judge\"]", kept.AllowedPersonas, "omitted AllowedPersonas is kept");
                AssertEqual("Worker", kept.PreferredPersona, "omitted PreferredPersona is kept");
                AssertEqual("be brief", kept.SystemInstructions, "omitted SystemInstructions is kept");
                AssertEqual(CaptainTierEnum.Premium, kept.Tier, "omitted Tier is kept");
                AssertEqual(ReasoningEffortEnum.High, kept.ReasoningEffort, "omitted ReasoningEffort is kept");

                // An explicit null still clears a field; other omitted fields stay.
                using (StringContent content = RawJson("{\"allowedPersonas\":null,\"systemInstructions\":null}"))
                using (HttpResponseMessage put = await client.PutAsync(path, content, token).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.OK, put.StatusCode, await put.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                Captain cleared = await ReadCaptainAsync(client, path, token).ConfigureAwait(false);
                AssertNull(cleared.AllowedPersonas, "explicit null clears AllowedPersonas");
                AssertNull(cleared.SystemInstructions, "explicit null clears SystemInstructions");
                AssertEqual("Worker", cleared.PreferredPersona);
                AssertEqual(renamed, cleared.Name);
                AssertEqual(AgentRuntimeEnum.Codex, cleared.Runtime, "omitted Runtime is kept");
            }
            finally
            {
                using (HttpResponseMessage deleted = await client.DeleteAsync(path, token).ConfigureAwait(false)) { }
            }
        }

        private Task CaptainMergeModelEndpointAsync(CancellationToken token)
        {
            JsonSerializerOptions options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            Captain existing = new Captain { Name = "api-cpt", Runtime = AgentRuntimeEnum.ApiEndpoint, ModelEndpointId = "mep_keep", AllowedPersonas = "[\"Worker\"]" };

            string omitted = "{\"name\":\"api-cpt\",\"runtime\":\"ApiEndpoint\",\"model\":\"m\"}";
            Captain merged = EntityUpdateMerger.MergeCaptain(existing, JsonSerializer.Deserialize<Captain>(omitted, options)!, CaptainUpdateFieldPresence.FromJson(omitted, options));
            AssertEqual("mep_keep", merged.ModelEndpointId, "omitted ModelEndpointId is kept");
            AssertEqual("[\"Worker\"]", merged.AllowedPersonas);
            AssertEqual("m", merged.Model);

            string cleared = "{\"modelEndpointId\":null}";
            Captain clearedMerge = EntityUpdateMerger.MergeCaptain(existing, JsonSerializer.Deserialize<Captain>(cleared, options)!, CaptainUpdateFieldPresence.FromJson(cleared, options));
            AssertNull(clearedMerge.ModelEndpointId, "explicit null clears ModelEndpointId");
            AssertEqual("api-cpt", clearedMerge.Name, "omitted Name is kept");
            AssertEqual(AgentRuntimeEnum.ApiEndpoint, clearedMerge.Runtime, "omitted Runtime is kept");

            Captain replaced = EntityUpdateMerger.MergeCaptain(existing, new Captain { Name = "api-cpt", Runtime = AgentRuntimeEnum.ApiEndpoint });
            AssertNull(replaced.AllowedPersonas, "the two-argument overload keeps full-replacement semantics");
            return Task.CompletedTask;
        }

        private async Task RequireHarborSettingAsync(CancellationToken token)
        {
            E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
            HttpClient client = fixture.AuthClient;
            RequireHarborSettingsView before = await ReadSettingsAsync(client, token).ConfigureAwait(false);
            try
            {
                using (StringContent content = JsonHelper.ToJsonContent(new { RequireHarborForLaunch = !before.RequireHarborForLaunch }))
                using (HttpResponseMessage put = await client.PutAsync("/api/v1/settings", content, token).ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.OK, put.StatusCode, await put.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                    RequireHarborSettingsView response = await JsonHelper.DeserializeAsync<RequireHarborSettingsView>(put).ConfigureAwait(false);
                    AssertEqual(!before.RequireHarborForLaunch, response.RequireHarborForLaunch, "PUT response");
                }
                AssertEqual(!before.RequireHarborForLaunch, (await ReadSettingsAsync(client, token).ConfigureAwait(false)).RequireHarborForLaunch, "GET after PUT");

                using (StringContent content = JsonHelper.ToJsonContent(new { MaxCaptains = before.MaxCaptains }))
                using (HttpResponseMessage put = await client.PutAsync("/api/v1/settings", content, token).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.OK, put.StatusCode);
                AssertEqual(!before.RequireHarborForLaunch, (await ReadSettingsAsync(client, token).ConfigureAwait(false)).RequireHarborForLaunch, "an unrelated PUT keeps the value");
            }
            finally
            {
                using (StringContent content = JsonHelper.ToJsonContent(new { RequireHarborForLaunch = before.RequireHarborForLaunch }))
                using (HttpResponseMessage restore = await client.PutAsync("/api/v1/settings", content, token).ConfigureAwait(false)) { }
            }
        }

        private static async Task<RequireHarborSettingsView> ReadSettingsAsync(HttpClient client, CancellationToken token)
        {
            using (HttpResponseMessage get = await client.GetAsync("/api/v1/settings", token).ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.OK, get.StatusCode);
                return await JsonHelper.DeserializeAsync<RequireHarborSettingsView>(get).ConfigureAwait(false);
            }
        }

        private static async Task<Captain> CreateCaptainAsync(HttpClient client, object body, CancellationToken token)
        {
            using (StringContent content = JsonHelper.ToJsonContent(body))
            using (HttpResponseMessage created = await client.PostAsync("/api/v1/captains", content, token).ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                return await JsonHelper.DeserializeAsync<Captain>(created).ConfigureAwait(false);
            }
        }

        private static async Task<Captain> ReadCaptainAsync(HttpClient client, string path, CancellationToken token)
        {
            using (HttpResponseMessage get = await client.GetAsync(path, token).ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.OK, get.StatusCode);
                return await JsonHelper.DeserializeAsync<Captain>(get).ConfigureAwait(false);
            }
        }

        private static async Task<Persona> ReadPersonaAsync(HttpClient client, string path, CancellationToken token)
        {
            using (HttpResponseMessage get = await client.GetAsync(path, token).ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.OK, get.StatusCode, "GET " + path);
                return await JsonHelper.DeserializeAsync<Persona>(get).ConfigureAwait(false);
            }
        }

        private static StringContent RawJson(string json)
        {
            return new StringContent(json, Encoding.UTF8, "application/json");
        }

        #endregion
    }
}
