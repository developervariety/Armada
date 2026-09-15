namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// A bad enum value on an MCP tool argument returns the valid values for that field instead of an exception
    /// or a bare error, on every tool family that takes enum arguments.
    /// </summary>
    public class McpEnumArgumentFailureTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "MCP Enum Argument Failures";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("armada_create_incident with a bad severity returns the valid severity values", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterIncidentHandlers(testDb);
                string json = await InvokeAsync(handlers, "armada_create_incident", new { title = "t", severity = "Catastrophic" }).ConfigureAwait(false);

                AssertContains("severity", json);
                foreach (string name in Enum.GetNames<IncidentSeverityEnum>()) AssertContains(name, json);
            }).ConfigureAwait(false);

            await RunTest("armada_update_incident with a bad status returns the valid status values", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterIncidentHandlers(testDb);
                string json = await InvokeAsync(handlers, "armada_update_incident", new { incidentId = "inc_missing", status = "Resolved" }).ConfigureAwait(false);

                AssertContains("status", json);
                foreach (string name in Enum.GetNames<IncidentStatusEnum>()) AssertContains(name, json);
            }).ConfigureAwait(false);

            await RunTest("armada_list_incidents with a bad status filter returns the valid status values", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterIncidentHandlers(testDb);
                string json = await InvokeAsync(handlers, "armada_list_incidents", new { status = "Resolved" }).ConfigureAwait(false);

                foreach (string name in Enum.GetNames<IncidentStatusEnum>()) AssertContains(name, json);
            }).ConfigureAwait(false);

            await RunTest("run_check with a bad type returns the valid type values", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = Quiet();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                McpCheckRunTools.Register((name, _, _, handler) => { handlers[name] = McpTestCaller.Wrap(handler); }, testDb.Driver, checkRuns);

                string json = await InvokeAsync(handlers, "run_check", new { vesselId = "vsl_missing", type = "Compile" }).ConfigureAwait(false);

                foreach (string name in Enum.GetNames<CheckRunTypeEnum>()) AssertContains(name, json);
            }).ConfigureAwait(false);

            await RunTest("update_release with a bad status returns the valid status values", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = Quiet();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                ReleaseService releases = new ReleaseService(testDb.Driver, workflowProfiles, logging);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                McpReleaseTools.Register((name, _, _, handler) => { handlers[name] = McpTestCaller.Wrap(handler); }, releases);

                string json = await InvokeAsync(handlers, "update_release", new { releaseId = "rel_missing", status = "Published" }).ConfigureAwait(false);

                AssertContains("status", json);
                foreach (string name in Enum.GetNames<ReleaseStatusEnum>()) AssertContains(name, json);
            }).ConfigureAwait(false);
        }

        private static LoggingModule Quiet()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> RegisterIncidentHandlers(TestDatabase testDb)
        {
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
            McpIncidentTools.Register((name, _, _, handler) => { handlers[name] = McpTestCaller.Wrap(handler); }, new IncidentService(testDb.Driver));
            return handlers;
        }

        private static async Task<string> InvokeAsync(Dictionary<string, Func<JsonElement?, Task<object>>> handlers, string tool, object args)
        {
            JsonElement element = JsonSerializer.SerializeToElement(args);
            object result = await handlers[tool](element).ConfigureAwait(false);
            return JsonSerializer.Serialize(result);
        }
    }
}
