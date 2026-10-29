namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Runtimes.Mcp;
    using McpToolCallResult = Armada.Runtimes.Mcp.McpToolCallResult;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end tests for the handoff artifact API against a live server: REST GET /api/v1/missions/{id}/handoff-artifacts
    /// and GET /api/v1/handoff-artifacts/{id}, and MCP get_handoff_artifact and list_handoff_artifacts, including tenant
    /// isolation, global admin access, missing ids and unauthenticated calls.
    /// </summary>
    public sealed class HandoffArtifactApiSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.HandoffArtifacts";

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("rest_tenant_isolation", "REST: a mission's artifacts are listed (without output and diff) and read within the caller's tenant; another tenant gets 404; a global admin reads any tenant", async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser userA = await E2ETenantUser.CreateAsync(fx.AuthClient, "hoa-user-a", false).ConfigureAwait(false);
                E2ETenantUser userB = await E2ETenantUser.CreateAsync(fx.AuthClient, "hoa-user-b", false).ConfigureAwait(false);
                Seeded a = await SeedAsync(fx, userA.TenantId).ConfigureAwait(false);
                Seeded b = await SeedAsync(fx, userB.TenantId).ConfigureAwait(false);
                using HttpClient clientA = userA.CreateClient(fx.BaseUrl);
                using HttpClient clientB = userB.CreateClient(fx.BaseUrl);

                EnumerationResult<HandoffArtifact> page = await GetAsync<EnumerationResult<HandoffArtifact>>(clientA, "/api/v1/missions/" + a.Mission.Id + "/handoff-artifacts").ConfigureAwait(false);
                AssertEqual(2, (int)page.TotalRecords, "both attempts listed");
                AssertEqual(a.Second.Id, page.Objects[0].Id, "newest first");
                AssertEqual(25, page.PageSize, "default page size");
                AssertTrue(page.Objects.All(o => o.Output == null && o.Diff == null), "list leaves out output and diff");
                AssertEqual(a.Second.OutputLength, page.Objects[0].OutputLength, "output length kept");
                AssertEqual(2, page.Objects[0].Findings.Count, "findings listed");
                EnumerationResult<HandoffArtifact> paged = await GetAsync<EnumerationResult<HandoffArtifact>>(clientA, "/api/v1/missions/" + a.Mission.Id + "/handoff-artifacts?pageNumber=2&pageSize=1").ConfigureAwait(false);
                AssertEqual(a.First.Id, paged.Objects.Single().Id, "second page");

                HandoffArtifact read = await GetAsync<HandoffArtifact>(clientA, "/api/v1/handoff-artifacts/" + a.Second.Id).ConfigureAwait(false);
                AssertEqual(a.Second.Output, read.Output, "full output on read");
                AssertEqual(a.Second.Diff, read.Diff, "full diff on read");
                AssertEqual("src/Parser.cs", read.Findings[0].File, "finding file round-trips");
                AssertEqual(StageCheckStatusEnum.Failed, read.CheckResults.Single().Status, "check result round-trips");

                AssertStatusCode(HttpStatusCode.NotFound, await clientA.GetAsync("/api/v1/handoff-artifacts/" + b.Second.Id).ConfigureAwait(false), "A cannot read B's artifact");
                AssertStatusCode(HttpStatusCode.NotFound, await clientB.GetAsync("/api/v1/handoff-artifacts/" + a.Second.Id).ConfigureAwait(false), "B cannot read A's artifact");
                AssertStatusCode(HttpStatusCode.NotFound, await clientB.GetAsync("/api/v1/missions/" + a.Mission.Id + "/handoff-artifacts").ConfigureAwait(false), "B cannot list A's mission");
                AssertStatusCode(HttpStatusCode.NotFound, await clientA.GetAsync("/api/v1/handoff-artifacts/hoa_missing").ConfigureAwait(false), "missing artifact");
                AssertStatusCode(HttpStatusCode.NotFound, await clientA.GetAsync("/api/v1/missions/msn_missing/handoff-artifacts").ConfigureAwait(false), "missing mission");
                AssertStatusCode(HttpStatusCode.BadRequest, await clientA.GetAsync("/api/v1/missions/" + a.Mission.Id + "/handoff-artifacts?pageSize=ten").ConfigureAwait(false), "invalid page size");
                AssertStatusCode(HttpStatusCode.Unauthorized, await fx.UnauthClient.GetAsync("/api/v1/handoff-artifacts/" + a.Second.Id).ConfigureAwait(false), "unauthenticated");

                Seeded empty = await SeedAsync(fx, userA.TenantId, false).ConfigureAwait(false);
                EnumerationResult<HandoffArtifact> none = await GetAsync<EnumerationResult<HandoffArtifact>>(clientA, "/api/v1/missions/" + empty.Mission.Id + "/handoff-artifacts").ConfigureAwait(false);
                AssertEqual(0, (int)none.TotalRecords, "a mission without artifacts lists empty");
                AssertEqual(0, none.Objects.Count, "no objects");

                HandoffArtifact global = await GetAsync<HandoffArtifact>(fx.AuthClient, "/api/v1/handoff-artifacts/" + b.Second.Id).ConfigureAwait(false);
                AssertEqual(b.Second.Id, global.Id, "a global admin reads any tenant");
            }));

            cases.Add(CaseAsync("mcp_tenant_isolation", "MCP: get_handoff_artifact and list_handoff_artifacts stay within the caller's tenant and answer typed NotFound otherwise", async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                E2ETenantUser userA = await E2ETenantUser.CreateAsync(fx.AuthClient, "hom-user-a", false).ConfigureAwait(false);
                E2ETenantUser userB = await E2ETenantUser.CreateAsync(fx.AuthClient, "hom-user-b", false).ConfigureAwait(false);
                Seeded a = await SeedAsync(fx, userA.TenantId).ConfigureAwait(false);

                using (McpToolClient clientA = Mcp(fx, userA))
                using (McpToolClient clientB = Mcp(fx, userB))
                using (McpToolClient global = new McpToolClient("http://127.0.0.1:" + fx.McpPort + "/mcp", null, new Dictionary<string, string> { ["X-Api-Key"] = fx.ApiKey }))
                {
                    await clientA.InitializeAsync().ConfigureAwait(false);
                    await clientB.InitializeAsync().ConfigureAwait(false);
                    await global.InitializeAsync().ConfigureAwait(false);

                    HandoffArtifact read = await OkAsync<HandoffArtifact>(clientA, "get_handoff_artifact", new { artifactId = a.Second.Id }).ConfigureAwait(false);
                    AssertEqual(a.Second.Output, read.Output, "full output");
                    AssertEqual(HandoffArtifact.CurrentSchemaVersion, read.SchemaVersion, "schema version");
                    EnumerationResult<HandoffArtifact> listed = await OkAsync<EnumerationResult<HandoffArtifact>>(clientA, "list_handoff_artifacts", new { missionId = a.Mission.Id, pageSize = 1 }).ConfigureAwait(false);
                    AssertEqual(2, (int)listed.TotalRecords, "two attempts");
                    AssertEqual(a.Second.Id, listed.Objects.Single().Id, "newest first");
                    AssertNull(listed.Objects.Single().Output, "list leaves out output");

                    await ErrorAsync(clientB, "get_handoff_artifact", new { artifactId = a.Second.Id }, McpToolErrorCodeEnum.NotFound).ConfigureAwait(false);
                    await ErrorAsync(clientB, "list_handoff_artifacts", new { missionId = a.Mission.Id }, McpToolErrorCodeEnum.NotFound).ConfigureAwait(false);
                    await ErrorAsync(clientA, "get_handoff_artifact", new { artifactId = "hoa_missing" }, McpToolErrorCodeEnum.NotFound).ConfigureAwait(false);
                    HandoffArtifact globalRead = await OkAsync<HandoffArtifact>(global, "get_handoff_artifact", new { artifactId = a.Second.Id }).ConfigureAwait(false);
                    AssertEqual(a.Second.Id, globalRead.Id, "a global admin reads any tenant");
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Handoff artifact API (E2E)",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<Seeded> SeedAsync(E2EServerFixture fx, string tenantId, bool withArtifacts = true)
        {
            using (DatabaseDriver db = await OpenDatabaseAsync(fx).ConfigureAwait(false))
            {
                Mission mission = new Mission("[Judge] Review", "Review the parser change");
                mission.TenantId = tenantId;
                mission.Persona = "Judge";
                mission = await db.Missions.CreateAsync(mission).ConfigureAwait(false);
                Seeded seeded = new Seeded { Mission = mission };
                if (!withArtifacts) return seeded;

                seeded.First = await db.HandoffArtifacts.CreateAsync(Artifact(mission, DateTime.UtcNow.AddMinutes(-5))).ConfigureAwait(false);
                seeded.Second = await db.HandoffArtifacts.CreateAsync(Artifact(mission, DateTime.UtcNow)).ConfigureAwait(false);
                return seeded;
            }
        }

        private static HandoffArtifact Artifact(Mission mission, DateTime created)
        {
            HandoffArtifact artifact = new HandoffArtifact
            {
                TenantId = mission.TenantId,
                MissionId = mission.Id,
                Persona = "Judge",
                Verdict = "NEEDS_REVISION",
                Output = "## Affected Case\nFile: src/Parser.cs:12\n[ARMADA:VERDICT] NEEDS_REVISION",
                Diff = "diff --git a/src/Parser.cs b/src/Parser.cs\n+x\n",
                CreatedUtc = created
            };
            artifact.OutputLength = artifact.Output.Length;
            artifact.DiffLength = artifact.Diff.Length;
            artifact.Findings.Add(new HandoffFinding { Number = 1, File = "src/Parser.cs", Line = 12, Severity = HandoffFindingSeverityEnum.Blocking, Summary = "File: src/Parser.cs:12" });
            artifact.Findings.Add(new HandoffFinding { Number = 2, Summary = "Scenario: empty input returns success" });
            artifact.CheckResults.Add(new StageCheckResult { Status = StageCheckStatusEnum.Failed, FailedCommand = "dotnet test", ExitCode = 1 });
            return artifact;
        }

        private static async Task<T> GetAsync<T>(HttpClient client, string path)
        {
            HttpResponseMessage response = await client.GetAsync(path).ConfigureAwait(false);
            AssertStatusCode(HttpStatusCode.OK, response, "GET " + path);
            return await JsonHelper.DeserializeAsync<T>(response).ConfigureAwait(false);
        }

        private static McpToolClient Mcp(E2EServerFixture fx, E2ETenantUser user)
        {
            Dictionary<string, string> headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + user.BearerToken };
            return new McpToolClient("http://127.0.0.1:" + fx.McpPort + "/mcp", null, headers);
        }

        private static async Task<T> OkAsync<T>(McpToolClient client, string tool, object args)
        {
            McpToolCallResult result = await client.CallToolResultAsync(tool, JsonHelper.Serialize(args)).ConfigureAwait(false);
            AssertFalse(result.IsError, tool + ": " + result.Text);
            AssertFalse(result.Text.Contains("\"errorCode\"", StringComparison.OrdinalIgnoreCase), tool + " returned an error: " + result.Text);
            return JsonHelper.Deserialize<T>(result.Text);
        }

        private static async Task ErrorAsync(McpToolClient client, string tool, object args, McpToolErrorCodeEnum expected)
        {
            McpToolCallResult result = await client.CallToolResultAsync(tool, JsonHelper.Serialize(args)).ConfigureAwait(false);
            AssertTrue(result.Text.TrimStart().StartsWith("{", StringComparison.Ordinal), tool + " returned no McpToolError: " + result.Text);
            McpToolError error = JsonHelper.Deserialize<McpToolError>(result.Text);
            AssertEqual(expected, error.ErrorCode, tool + ": " + result.Text);
        }

        private static async Task<DatabaseDriver> OpenDatabaseAsync(E2EServerFixture fx)
        {
            LoggingModule quiet = new LoggingModule();
            quiet.Settings.EnableConsole = false;
            DatabaseSettings settings = new DatabaseSettings { Type = DatabaseTypeEnum.Sqlite, Filename = Path.Combine(fx.TempDir, "armada.db") };
            DatabaseDriver db = DatabaseDriverFactory.Create(settings, quiet);
            await db.InitializeAsync().ConfigureAwait(false);
            return db;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { TestTags.EndToEnd });
        }

        #endregion

        #region Private-Classes

        private sealed class Seeded
        {
            public Mission Mission { get; set; } = null!;

            public HandoffArtifact First { get; set; } = null!;

            public HandoffArtifact Second { get; set; } = null!;
        }

        #endregion
    }
}
