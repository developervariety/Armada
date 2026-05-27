namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for MCP structured check-run tools.
    /// </summary>
    public class McpCheckRunToolsTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "MCP Check Run Tools";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("run_check returns structured failure instead of Error object", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    vesselId = "vsl_missing",
                    type = CheckRunTypeEnum.Build
                });

                object result = await handlers["run_check"](args).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result);

                AssertContains("check_run_failed", json);
                AssertContains("Vessel not found", json);
                AssertFalse(json.Contains("\"Error\"", StringComparison.OrdinalIgnoreCase), "Expected MCP tool result to avoid top-level Error.");
            }).ConfigureAwait(false);

            await RunTest("retry_check_run returns structured failure instead of Error object", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    checkRunId = "chk_missing"
                });

                object result = await handlers["retry_check_run"](args).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result);

                AssertContains("check_run_retry_failed", json);
                AssertContains("Check run not found", json);
                AssertFalse(json.Contains("\"Error\"", StringComparison.OrdinalIgnoreCase), "Expected MCP retry result to avoid top-level Error.");
            }).ConfigureAwait(false);

            await RunTest("armada_resolve_check validation returns structured failure instead of Error object", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver);
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    checkRunId = "chk_missing",
                    status = "NotAStatus"
                });

                object result = await handlers["armada_resolve_check"](args).ConfigureAwait(false);
                string json = JsonSerializer.Serialize(result);

                AssertContains("check_status_invalid", json);
                AssertContains("ValidStatusValues", json);
                AssertFalse(json.Contains("\"Error\"", StringComparison.OrdinalIgnoreCase), "Expected MCP resolve result to avoid top-level Error.");
            }).ConfigureAwait(false);
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> RegisterHandlers(DatabaseDriver database)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            WorkflowProfileService workflowProfiles = new WorkflowProfileService(database, logging);
            VesselReadinessService readiness = new VesselReadinessService(database, workflowProfiles, logging);
            CheckRunService checkRuns = new CheckRunService(database, workflowProfiles, readiness, logging);

            Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
            McpCheckRunTools.Register(
                (name, _, _, handler) =>
                {
                    handlers[name] = handler;
                },
                database,
                checkRuns);
            return handlers;
        }
    }
}
