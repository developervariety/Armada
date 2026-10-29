namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Runtimes.Mcp;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// REST and MCP enqueue guards distinguish landing evidence from a false Complete status.
    /// </summary>
    public sealed class CompleteLandingEvidenceSuite : IArmadaTestSuite
    {
        /// <summary>
        /// Build the landing-evidence regressions against a real local server.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            const string suite = "E2E.CompleteLandingEvidence";
            return new TestSuiteDescriptor(suite, "Complete landing evidence", new List<TestCaseDescriptor>
            {
                new TestCaseDescriptor(suiteId: suite, caseId: "enqueue_complete_without_evidence_reports_mismatch", displayName: "REST and MCP require landing evidence", tags: new List<string> { TestTags.Negative }, executeAsync: async _ =>
                {
                    E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    using DatabaseDriver db = DatabaseDriverFactory.Create(new DatabaseSettings { Type = DatabaseTypeEnum.Sqlite, Filename = Path.Combine(fx.TempDir, "armada.db") }, logging);
                    await db.InitializeAsync().ConfigureAwait(false);
                    Vessel vessel = await db.Vessels.CreateAsync(new Vessel("landing-evidence", "https://example.invalid/repo.git") { TenantId = Constants.DefaultTenantId }).ConfigureAwait(false);
                    Mission mission = await db.Missions.CreateAsync(new Mission("false complete") { TenantId = Constants.DefaultTenantId, VesselId = vessel.Id, Status = MissionStatusEnum.Complete }).ConfigureAwait(false);
                    using McpToolClient mcp = new McpToolClient("http://127.0.0.1:" + fx.McpPort + "/mcp", null, new Dictionary<string, string> { ["X-Api-Key"] = fx.ApiKey });
                    await mcp.InitializeAsync().ConfigureAwait(false);
                    foreach (string? commit in new string?[] { null, "", "landed-commit" })
                    {
                        mission.CommitHash = commit;
                        await db.Missions.UpdateAsync(mission).ConfigureAwait(false);
                        MergeEntry entry = new MergeEntry("armada/unlanded", "main") { MissionId = mission.Id, VesselId = vessel.Id };
                        using HttpResponseMessage response = await fx.AuthClient.PostAsync("/api/v1/merge-queue", JsonHelper.ToJsonContent(entry)).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.Conflict, response.StatusCode, "Complete cannot silently enqueue");
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Armada.Runtimes.Mcp.McpToolCallResult result = await mcp.CallToolResultAsync("enqueue_merge", JsonHelper.Serialize(new { missionId = mission.Id, vesselId = vessel.Id, branchName = entry.BranchName })).ConfigureAwait(false);
                        McpToolError error = JsonHelper.Deserialize<McpToolError>(result.Text);
                        AssertEqual(McpToolErrorCodeEnum.Conflict, error.ErrorCode, result.Text);
                        if (String.IsNullOrEmpty(commit))
                        {
                            AssertContains("status mismatch", body);
                            AssertContains("status mismatch", result.Text);
                            AssertFalse(body.Contains("already landed", StringComparison.Ordinal), body);
                            AssertFalse(result.Text.Contains("already landed", StringComparison.Ordinal), result.Text);
                        }
                        else
                        {
                            AssertContains("already landed", body);
                            AssertContains(commit!, result.Text);
                        }
                    }
                    AssertFalse((await db.MergeEntries.EnumerateAsync().ConfigureAwait(false)).Any(e => e.MissionId == mission.Id), "refusals never create queue entries");
                    AssertEqual(MissionStatusEnum.Complete, (await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!.Status, "guard does not silently rewrite status");
                })
            });
        }
    }
}
