namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Test.Common;

    /// <summary>
    /// One workflow-profile payload sent through REST and MCP must store the same record, validate the same
    /// way as it would be created, and be refused the same way. Validation must not reveal whether another
    /// tenant's fleet exists: its id reads the same as an id that exists nowhere, on the workflow-profile and
    /// the project-profile validate routes alike.
    /// </summary>
    public class WorkflowProfileParityTests : TestSuite
    {
        #region Public-Members

        /// <inheritdoc />
        public override string Name => "Workflow Profile Surface Parity";

        #endregion

        #region Private-Members

        private readonly ConfigurationSurfaces _Surfaces;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public WorkflowProfileParityTests(ConfigurationSurfaces surfaces)
        {
            _Surfaces = surfaces ?? throw new ArgumentNullException(nameof(surfaces));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Workflow profile create and replace store the same trimmed commands on REST and MCP", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                object created = new
                {
                    name = "Parity profile " + suffix,
                    scope = "Global",
                    buildCommand = "  dotnet build  ",
                    unitTestCommand = "dotnet test",
                    environmentVariables = new Dictionary<string, string> { ["A"] = "1" }
                };

                SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/workflow-profiles", ConfigurationSurfaces.Json(created)).ConfigureAwait(false);
                SurfaceReply mcp = await _Surfaces.McpAsync("create_workflow_profile", ConfigurationSurfaces.Json(new { profile = created })).ConfigureAwait(false);
                AssertFalse(rest.IsError, "REST create: " + rest);
                AssertFalse(mcp.IsError, "MCP create: " + mcp);
                Dictionary<string, string> ids = new Dictionary<string, string>
                {
                    ["rest"] = ConfigurationSurfaces.Text(rest.Json(), "id"),
                    ["mcp"] = ConfigurationSurfaces.Text(mcp.Json(), "id")
                };

                await AssertSameStoredAsync(ids, "build=dotnet build test=dotnet test vars=A=1").ConfigureAwait(false);

                object replaced = new
                {
                    name = "Parity profile " + suffix,
                    scope = "Global",
                    buildCommand = "dotnet build -c Release ",
                    unitTestCommand = " dotnet test --no-build",
                    environmentVariables = new Dictionary<string, string> { ["A"] = "2", ["B"] = "3" }
                };
                rest = await _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/workflow-profiles/" + ids["rest"], ConfigurationSurfaces.Json(replaced)).ConfigureAwait(false);
                mcp = await _Surfaces.McpAsync("update_workflow_profile", ConfigurationSurfaces.Json(new { workflowProfileId = ids["mcp"], profile = replaced })).ConfigureAwait(false);
                AssertFalse(rest.IsError, "REST replace: " + rest);
                AssertFalse(mcp.IsError, "MCP replace: " + mcp);

                await AssertSameStoredAsync(ids, "build=dotnet build -c Release test=dotnet test --no-build vars=A=2,B=3").ConfigureAwait(false);

                foreach (string id in ids.Values)
                    await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/workflow-profiles/" + id, null).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Workflow profile replace stores environment variables the same way on REST and MCP", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                object created = new { name = "Parity variables " + suffix, scope = "Global", buildCommand = "make", environmentVariables = new Dictionary<string, string> { ["A"] = "1" } };
                SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/workflow-profiles", ConfigurationSurfaces.Json(created)).ConfigureAwait(false);
                SurfaceReply mcp = await _Surfaces.McpAsync("create_workflow_profile", ConfigurationSurfaces.Json(new { profile = created })).ConfigureAwait(false);
                AssertFalse(rest.IsError, "REST create: " + rest);
                AssertFalse(mcp.IsError, "MCP create: " + mcp);
                Dictionary<string, string> ids = new Dictionary<string, string>
                {
                    ["rest"] = ConfigurationSurfaces.Text(rest.Json(), "id"),
                    ["mcp"] = ConfigurationSurfaces.Text(mcp.Json(), "id")
                };

                object replaced = new { name = "Parity variables " + suffix, scope = "Global", buildCommand = "make", environmentVariables = new Dictionary<string, string> { ["A"] = "2", ["B"] = "3" } };
                AssertFalse((await _Surfaces.RestAsync(HttpMethod.Put, "/api/v1/workflow-profiles/" + ids["rest"], ConfigurationSurfaces.Json(replaced)).ConfigureAwait(false)).IsError, "REST replace");
                AssertFalse((await _Surfaces.McpAsync("update_workflow_profile", ConfigurationSurfaces.Json(new { workflowProfileId = ids["mcp"], profile = replaced })).ConfigureAwait(false)).IsError, "MCP replace");
                await AssertSameStoredAsync(ids, "build=make test= vars=A=2,B=3").ConfigureAwait(false);

                foreach (string id in ids.Values)
                    await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/workflow-profiles/" + id, null).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("An administrator's validate agrees with its create on REST and MCP for another tenant's fleet", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                string tenantToken = await _Surfaces.CreateTenantAdministratorTokenAsync("workflow-parity").ConfigureAwait(false);
                SurfaceReply fleet = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/fleets", ConfigurationSurfaces.Json(new { name = "parity-fleet-" + suffix }), tenantToken).ConfigureAwait(false);
                AssertFalse(fleet.IsError, "tenant fleet create: " + fleet);
                string fleetId = ConfigurationSurfaces.Text(fleet.Json(), "id");
                string tenantOfFleet = ConfigurationSurfaces.Text(fleet.Json(), "tenantId");
                object profile = new { name = "Parity fleet profile " + suffix, scope = "Fleet", fleetId = fleetId, buildCommand = "make" };

                SurfaceReply restValidate = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/workflow-profiles/validate", ConfigurationSurfaces.Json(profile)).ConfigureAwait(false);
                SurfaceReply mcpValidate = await _Surfaces.McpAsync("validate_workflow_profile", ConfigurationSurfaces.Json(new { profile = profile })).ConfigureAwait(false);
                string restVerdict = Verdict(restValidate);
                string mcpVerdict = Verdict(mcpValidate);

                SurfaceReply restCreate = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/workflow-profiles", ConfigurationSurfaces.Json(profile)).ConfigureAwait(false);
                SurfaceReply mcpCreate = await _Surfaces.McpAsync("create_workflow_profile", ConfigurationSurfaces.Json(new { profile = profile })).ConfigureAwait(false);

                List<string> failures = new List<string>();
                if (restVerdict != mcpVerdict) failures.Add("REST validate '" + restVerdict + "' but MCP validate '" + mcpVerdict + "'");
                if ((restVerdict == "valid") == restCreate.IsError) failures.Add("REST validate '" + restVerdict + "' but REST create " + restCreate);
                if ((mcpVerdict == "valid") == mcpCreate.IsError) failures.Add("MCP validate '" + mcpVerdict + "' but MCP create " + mcpCreate);
                AssertEqual(0, failures.Count, String.Join("; ", failures));
                AssertEqual("valid", restVerdict, "an administrator may scope a profile to any tenant's fleet");

                AssertEqual(tenantOfFleet, ConfigurationSurfaces.Text(restCreate.Json(), "tenantId"), "the REST profile belongs to the fleet's tenant");
                AssertEqual(tenantOfFleet, ConfigurationSurfaces.Text(mcpCreate.Json(), "tenantId"), "the MCP profile belongs to the fleet's tenant");

                await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/workflow-profiles/" + ConfigurationSurfaces.Text(restCreate.Json(), "id"), null).ConfigureAwait(false);
                await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/workflow-profiles/" + ConfigurationSurfaces.Text(mcpCreate.Json(), "id"), null).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Validating with another tenant's fleet id reads the same as an id that exists nowhere", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                string callerToken = await _Surfaces.CreateTenantAdministratorTokenAsync("oracle-caller").ConfigureAwait(false);
                string ownerToken = await _Surfaces.CreateTenantAdministratorTokenAsync("oracle-owner").ConfigureAwait(false);
                string foreignFleetId = await CreateFleetAsync("oracle-fleet-" + suffix, ownerToken).ConfigureAwait(false);
                string missingFleetId = "flt_missing" + Guid.NewGuid().ToString("N").Substring(0, 12);

                List<string> failures = new List<string>();
                foreach (string route in new[] { "/api/v1/workflow-profiles/validate", "/api/v1/project-profiles/validate" })
                {
                    SurfaceReply foreign = await _Surfaces.RestAsync(HttpMethod.Post, route, ConfigurationSurfaces.Json(new { name = "Oracle " + suffix, scope = "Fleet", fleetId = foreignFleetId, buildCommand = "make" }), callerToken).ConfigureAwait(false);
                    SurfaceReply missing = await _Surfaces.RestAsync(HttpMethod.Post, route, ConfigurationSurfaces.Json(new { name = "Oracle " + suffix, scope = "Fleet", fleetId = missingFleetId, buildCommand = "make" }), callerToken).ConfigureAwait(false);
                    string foreignErrors = Errors(foreign);
                    string missingErrors = Errors(missing);
                    if (foreign.Status != missing.Status || foreignErrors != missingErrors)
                        failures.Add(route + " answered another tenant's fleet with " + foreign.Status + " '" + foreignErrors + "' but a missing fleet with " + missing.Status + " '" + missingErrors + "'");
                }

                // The create path must not reveal it either.
                SurfaceReply foreignCreate = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/workflow-profiles", ConfigurationSurfaces.Json(new { name = "Oracle " + suffix, scope = "Fleet", fleetId = foreignFleetId, buildCommand = "make" }), callerToken).ConfigureAwait(false);
                SurfaceReply missingCreate = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/workflow-profiles", ConfigurationSurfaces.Json(new { name = "Oracle " + suffix, scope = "Fleet", fleetId = missingFleetId, buildCommand = "make" }), callerToken).ConfigureAwait(false);
                if (foreignCreate.Text != missingCreate.Text || foreignCreate.Status != missingCreate.Status)
                    failures.Add("workflow-profile create answered another tenant's fleet with " + foreignCreate + " but a missing fleet with " + missingCreate);

                AssertEqual(0, failures.Count, String.Join("; ", failures));
            }).ConfigureAwait(false);

            await RunTest("Workflow profile writes refuse a blank name and a profile without commands the same way on REST and MCP", async () =>
            {
                List<string> failures = new List<string>();
                Dictionary<string, object> cases = new Dictionary<string, object>
                {
                    ["a blank name"] = new { name = "  ", scope = "Global", buildCommand = "make" },
                    ["no commands"] = new { name = "Parity no commands", scope = "Global" }
                };

                foreach (KeyValuePair<string, object> entry in cases)
                {
                    SurfaceReply rest = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/workflow-profiles", ConfigurationSurfaces.Json(entry.Value)).ConfigureAwait(false);
                    SurfaceReply mcp = await _Surfaces.McpAsync("create_workflow_profile", ConfigurationSurfaces.Json(new { profile = entry.Value })).ConfigureAwait(false);
                    if (rest.Status != 400) failures.Add("REST create with " + entry.Key + " returned " + rest);
                    if (!(mcp.Status == 200 && mcp.IsError)) failures.Add("MCP create with " + entry.Key + " returned " + mcp);
                    if (!rest.IsError) await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/workflow-profiles/" + ConfigurationSurfaces.Text(rest.Json(), "id"), null).ConfigureAwait(false);
                    if (!mcp.IsError) await _Surfaces.RestAsync(HttpMethod.Delete, "/api/v1/workflow-profiles/" + ConfigurationSurfaces.Text(mcp.Json(), "id"), null).ConfigureAwait(false);
                }

                AssertEqual(0, failures.Count, String.Join("; ", failures));
            }).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private async Task<string> CreateFleetAsync(string name, string bearer)
        {
            SurfaceReply reply = await _Surfaces.RestAsync(HttpMethod.Post, "/api/v1/fleets", ConfigurationSurfaces.Json(new { name = name }), bearer).ConfigureAwait(false);
            if (reply.IsError) throw new InvalidOperationException("Fleet create failed: " + reply);
            return ConfigurationSurfaces.Text(reply.Json(), "id");
        }

        private static string Verdict(SurfaceReply reply)
        {
            if (reply.IsError) return "refused: " + reply.Text;
            return ConfigurationSurfaces.Text(reply.Json(), "isValid") == "True" ? "valid" : "invalid: " + Errors(reply);
        }

        private static string Errors(SurfaceReply reply)
        {
            if (reply.IsError || !reply.Text.StartsWith("{", StringComparison.Ordinal)) return reply.Text;
            return ConfigurationSurfaces.TryProp(reply.Json(), "errors", out JsonElement errors) ? errors.GetRawText() : "";
        }

        private async Task AssertSameStoredAsync(Dictionary<string, string> ids, string expected)
        {
            List<string> failures = new List<string>();
            foreach (KeyValuePair<string, string> entry in ids)
            {
                SurfaceReply stored = await _Surfaces.RestAsync(HttpMethod.Get, "/api/v1/workflow-profiles/" + entry.Value, null).ConfigureAwait(false);
                if (stored.IsError) throw new InvalidOperationException("Profile " + entry.Value + " could not be read: " + stored);
                string summary = Summary(stored.Json());
                if (!String.Equals(expected, summary, StringComparison.Ordinal)) failures.Add(entry.Key + " stored '" + summary + "'");
            }

            AssertEqual(0, failures.Count, "expected '" + expected + "', but " + String.Join("; ", failures));
        }

        private static string Summary(JsonElement profile)
        {
            List<string> variables = new List<string>();
            if (ConfigurationSurfaces.TryProp(profile, "environmentVariables", out JsonElement map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty variable in map.EnumerateObject()) variables.Add(variable.Name + "=" + variable.Value.GetString());
            }

            variables.Sort(StringComparer.Ordinal);
            return "build=" + ConfigurationSurfaces.Text(profile, "buildCommand")
                + " test=" + ConfigurationSurfaces.Text(profile, "unitTestCommand")
                + " vars=" + String.Join(",", variables);
        }

        #endregion
    }
}
