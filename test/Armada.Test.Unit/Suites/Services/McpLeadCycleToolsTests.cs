namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests server-side completion gates for unattended lead lifecycle tools.
    /// </summary>
    public class McpLeadCycleToolsTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "MCP Lead Cycle Tools";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Completion requires board handoff and released claims", async () =>
            {
                TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                using (testDb)
                {
                    LoggingModule logging = new LoggingModule { Settings = { EnableConsole = false } };
                    CoordinationService coordination = new CoordinationService(logging, testDb.Driver);
                    GrokLeadSettings settings = new GrokLeadSettings();
                    LeadCycleCoordinator coordinator = new LeadCycleCoordinator(testDb.Driver, settings);
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                    McpLeadCycleTools.Register(
                        (name, description, schema, handler) => handlers[name] = handler,
                        testDb.Driver,
                        coordination,
                        coordinator,
                        LeadRunnerTypeEnum.Legacy,
                        settings.ParticipantKey);

                    object beginResult = await handlers["armada_lead_cycle_begin"](Args("{}"));
                    LeadCycleStartResult? started = JsonSerializer.Deserialize<LeadCycleStartResult>(
                        JsonSerializer.Serialize(beginResult));
                    AssertNotNull(started);
                    AssertTrue(started!.Acquired);
                    string handoff = "Cycle complete. No remaining work.";
                    JsonElement completionArgs = Args(JsonSerializer.Serialize(new
                    {
                        cycleId = started.CycleId,
                        handoff
                    }));

                    string withoutBoard = JsonSerializer.Serialize(
                        await handlers["armada_lead_cycle_complete"](completionArgs));
                    AssertContains("Post the same handoff", withoutBoard);

                    await coordination.PostMessageAsync(
                        CoordinationService.DefaultRoomKey,
                        CoordinationAuthorTypeEnum.Operator,
                        settings.ParticipantKey,
                        "Armada Lead",
                        handoff).ConfigureAwait(false);
                    CoordinationClaim claim = await coordination.ClaimAsync(
                        settings.ParticipantKey,
                        "Armada Lead",
                        CoordinationClaimSubjectEnum.Vessel,
                        "vsl_test").ConfigureAwait(false);
                    string withClaim = JsonSerializer.Serialize(
                        await handlers["armada_lead_cycle_complete"](completionArgs));
                    AssertContains("Release all claims", withClaim);

                    await coordination.ReleaseClaimAsync(claim.Id).ConfigureAwait(false);
                    string completed = JsonSerializer.Serialize(
                        await handlers["armada_lead_cycle_complete"](completionArgs));
                    AssertContains("\"Completed\":true", completed);
                    AssertFalse((await coordinator.GetStatusAsync().ConfigureAwait(false)).Active);
                }
            }).ConfigureAwait(false);
        }

        private static JsonElement Args(string json)
        {
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                return document.RootElement.Clone();
            }
        }
    }
}
