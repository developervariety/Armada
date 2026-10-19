namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// The paginated browse path for checks (armada_enumerate entityType=checks) withholds the
    /// command log by default and says how much it withheld; the detail path (get_check_run)
    /// returns it whole when asked. One 14-row page measured 26.8 MB before the projection, 3.3 MB
    /// in a single record, on the surface Armada's own instructions tell operators to prefer.
    /// </summary>
    public class McpEnumerateChecksProjectionTests : TestSuite
    {
        private const int _RunCount = 12;
        private const int _OutputChars = 2_000_000;

        /// <summary>Suite name.</summary>
        public override string Name => "MCP enumerate checks projection";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("armada_enumerate checks withholds Output by default, reports its length, and returns it whole on includeTestOutput", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string marker = "OUTPUT-MARKER-" + Guid.NewGuid().ToString("N");
                    CheckRun run = await CreateRunWithLargeLogAsync(testDb, marker).ConfigureAwait(false);
                    int logLength = run.Output!.Length;

                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                    McpEnumerateTools.Register((name, _, _, handler) => { handlers[name] = McpTestCaller.Wrap(handler); }, testDb.Driver);

                    object page = await handlers["armada_enumerate"](Args("{\"entityType\":\"checks\",\"pageSize\":14}")).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(page);
                    AssertTrue(json.Length < 100_000, "a page of checks without opt-in is bounded (was " + json.Length + " chars)");
                    AssertFalse(json.Contains(marker), "the log is withheld by default");
                    AssertContains("\"OutputLength\":" + logLength, json, "the withheld log's length is reported beside the row");
                    AssertContains(run.Id, json, "the row itself is present");

                    object whole = await handlers["armada_enumerate"](Args("{\"entityType\":\"checks\",\"pageSize\":14,\"includeTestOutput\":true}")).ConfigureAwait(false);
                    string wholeJson = JsonSerializer.Serialize(whole);
                    AssertTrue(wholeJson.Contains(marker), "the log is returned whole when the caller opts in");
                }
            });

            await RunTest("armada_enumerate checks never loads the logs it withholds", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string log = new string('x', _OutputChars);
                    for (int i = 0; i < _RunCount; i++)
                    {
                        CheckRun run = new CheckRun();
                        run.TenantId = Constants.DefaultTenantId;
                        run.Label = "UnitTest";
                        run.Command = "dotnet test";
                        run.Output = log;
                        run.Summary = "1 failed";
                        await testDb.Driver.CheckRuns.CreateAsync(run).ConfigureAwait(false);
                    }

                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                    McpEnumerateTools.Register((name, _, _, handler) => { handlers[name] = McpTestCaller.Wrap(handler); }, testDb.Driver);
                    JsonElement args = Args("{\"entityType\":\"checks\",\"pageSize\":" + _RunCount + "}");

                    // The logs total _RunCount * _OutputChars characters, two bytes each as strings. A read that loads
                    // them allocates at least that; one that never selects the column stays far under a quarter of it.
                    long budget = (long)_RunCount * _OutputChars * 2 / 4;
                    await handlers["armada_enumerate"](args).ConfigureAwait(false);
                    long before = GC.GetTotalAllocatedBytes(true);
                    object page = await handlers["armada_enumerate"](args).ConfigureAwait(false);
                    long allocated = GC.GetTotalAllocatedBytes(true) - before;
                    AssertTrue(allocated < budget, "a page of checks without opt-in allocated " + allocated + " bytes; a read that never loads the logs stays under " + budget);

                    string json = JsonSerializer.Serialize(page);
                    AssertContains("\"OutputLength\":" + _OutputChars, json, "the length of each withheld log is still reported");
                }
            });

            await RunTest("get_check_run still returns the whole log when asked, and a bounded tail otherwise", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string marker = "OUTPUT-MARKER-" + Guid.NewGuid().ToString("N");
                    CheckRun run = await CreateRunWithLargeLogAsync(testDb, marker).ConfigureAwait(false);

                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                    VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                    CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                    McpCheckRunTools.Register((name, _, _, handler) => { handlers[name] = handler; }, testDb.Driver, checkRuns);

                    object whole = await handlers["get_check_run"](Args("{\"checkRunId\":\"" + run.Id + "\",\"includeOutput\":true}")).ConfigureAwait(false);
                    AssertTrue(JsonSerializer.Serialize(whole).Contains(marker), "the detail path returns the log whole on request");

                    object tail = await handlers["get_check_run"](Args("{\"checkRunId\":\"" + run.Id + "\"}")).ConfigureAwait(false);
                    string tailJson = JsonSerializer.Serialize(tail);
                    AssertTrue(tailJson.Length < run.Output!.Length, "the default detail view is a bounded tail (was " + tailJson.Length + " chars)");
                    AssertFalse(tailJson.Contains(marker), "the first line of a long log is outside the default tail");
                }
            });
        }

        private static async Task<CheckRun> CreateRunWithLargeLogAsync(TestDatabase testDb, string marker)
        {
            // 200 lines of 5,000 characters: about 1 MB, with the marker on the FIRST line so a
            // trailing-lines view cannot contain it.
            string line = new string('x', 5000);
            List<string> lines = new List<string> { marker };
            for (int i = 0; i < 200; i++) lines.Add(line);

            CheckRun run = new CheckRun();
            run.TenantId = Constants.DefaultTenantId;
            run.Label = "UnitTest";
            run.Command = "dotnet test";
            run.Output = String.Join("\n", lines);
            run.Summary = "1 failed";
            return await testDb.Driver.CheckRuns.CreateAsync(run).ConfigureAwait(false);
        }

        private static JsonElement Args(string json)
        {
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                return doc.RootElement.Clone();
            }
        }
    }
}
