namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end descriptors for the vessel TestCommand, LintCommand and AuditCommand overrides through REST
    /// (create, get, update, clear) and MCP (add_vessel, update_vessel).
    /// </summary>
    public sealed class VesselCommandOverridesSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.VesselCommandOverrides";
        private static readonly Regex _VesselId = new Regex(@"vsl_[A-Za-z0-9_]+", RegexOptions.CultureInvariant);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("rest_round_trip", "REST create, get and update round-trip the three command overrides", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                string fleetId = await CreateFleetAsync(fx.AuthClient).ConfigureAwait(false);

                HttpResponseMessage created = await fx.AuthClient.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                {
                    Name = "cmd-overrides-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    FleetId = fleetId,
                    RepoUrl = "https://github.com/test/cmd-overrides",
                    TestCommand = "make test",
                    LintCommand = "npm run lint",
                    AuditCommand = "osv-scanner -r ."
                })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, created.StatusCode);
                Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(created).ConfigureAwait(false);
                AssertEqual("make test", vessel.TestCommand);
                AssertEqual("npm run lint", vessel.LintCommand);
                AssertEqual("osv-scanner -r .", vessel.AuditCommand);

                Vessel read = await GetAsync(fx.AuthClient, vessel.Id).ConfigureAwait(false);
                AssertEqual("make test", read.TestCommand);
                AssertEqual("npm run lint", read.LintCommand);
                AssertEqual("osv-scanner -r .", read.AuditCommand);

                read.TestCommand = null;
                read.LintCommand = "ruff check";
                HttpResponseMessage updated = await fx.AuthClient.PutAsync("/api/v1/vessels/" + vessel.Id, JsonHelper.ToJsonContent(read)).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync().ConfigureAwait(false));

                Vessel after = await GetAsync(fx.AuthClient, vessel.Id).ConfigureAwait(false);
                AssertNull(after.TestCommand);
                AssertEqual("ruff check", after.LintCommand);
                AssertEqual("osv-scanner -r .", after.AuditCommand);
            }));

            cases.Add(CaseAsync("rest_minimal_create_defaults_null", "A vessel created without the fields has null overrides", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                string fleetId = await CreateFleetAsync(fx.AuthClient).ConfigureAwait(false);
                HttpResponseMessage created = await fx.AuthClient.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                {
                    Name = "cmd-minimal-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    FleetId = fleetId,
                    RepoUrl = "https://github.com/test/cmd-minimal"
                })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, created.StatusCode);
                Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(created).ConfigureAwait(false);
                Vessel read = await GetAsync(fx.AuthClient, vessel.Id).ConfigureAwait(false);
                AssertNull(read.TestCommand);
                AssertNull(read.LintCommand);
                AssertNull(read.AuditCommand);
            }));

            cases.Add(CaseAsync("mcp_add_and_update", "MCP add_vessel sets the overrides and update_vessel changes or clears them", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                string fleetId = await CreateFleetAsync(fx.AuthClient).ConfigureAwait(false);

                using HttpClient mcp = new HttpClient();
                mcp.BaseAddress = fx.McpClient.BaseAddress;
                mcp.DefaultRequestHeaders.Add("X-Api-Key", fx.ApiKey);

                string added = await McpTestCalls.CallToolAsync(mcp, "add_vessel", new
                {
                    name = "cmd-mcp-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    repoUrl = "https://github.com/test/cmd-mcp",
                    fleetId = fleetId,
                    testCommand = "go test ./...",
                    lintCommand = "golangci-lint run"
                }).ConfigureAwait(false);
                Match id = _VesselId.Match(added);
                AssertTrue(id.Success, "add_vessel reply has a vessel id: " + added);

                Vessel afterAdd = await GetAsync(fx.AuthClient, id.Value).ConfigureAwait(false);
                AssertEqual("go test ./...", afterAdd.TestCommand);
                AssertEqual("golangci-lint run", afterAdd.LintCommand);
                AssertNull(afterAdd.AuditCommand);

                string updated = await McpTestCalls.CallToolAsync(mcp, "update_vessel", new
                {
                    vesselId = id.Value,
                    lintCommand = "",
                    auditCommand = "govulncheck ./..."
                }).ConfigureAwait(false);
                AssertFalse(updated.Contains("ErrorCode", StringComparison.Ordinal), updated);

                Vessel afterUpdate = await GetAsync(fx.AuthClient, id.Value).ConfigureAwait(false);
                AssertEqual("go test ./...", afterUpdate.TestCommand, "omitted argument keeps the value");
                AssertNull(afterUpdate.LintCommand, "empty string clears the override");
                AssertEqual("govulncheck ./...", afterUpdate.AuditCommand);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Vessel Command Overrides API",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<string> CreateFleetAsync(HttpClient client)
        {
            HttpResponseMessage response = await client.PostAsync("/api/v1/fleets", JsonHelper.ToJsonContent(new { Name = "cmd-fleet-" + Guid.NewGuid().ToString("N").Substring(0, 8) })).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            Fleet fleet = await JsonHelper.DeserializeAsync<Fleet>(response).ConfigureAwait(false);
            return fleet.Id;
        }

        private static async Task<Vessel> GetAsync(HttpClient client, string vesselId)
        {
            HttpResponseMessage response = await client.GetAsync("/api/v1/vessels/" + vesselId).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.OK, response.StatusCode, "GET vessel " + vesselId);
            return await JsonHelper.DeserializeAsync<Vessel>(response).ConfigureAwait(false);
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
