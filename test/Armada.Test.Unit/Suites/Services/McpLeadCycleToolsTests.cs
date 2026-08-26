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
            await RunTest("Completion requires released claims and posts the handoff itself", async () =>
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

                    CoordinationClaim claim = await coordination.ClaimAsync(
                        settings.ParticipantKey,
                        "Armada Lead",
                        CoordinationClaimSubjectEnum.Vessel,
                        "vsl_test").ConfigureAwait(false);
                    string withClaim = JsonSerializer.Serialize(
                        await handlers["armada_lead_cycle_complete"](completionArgs));
                    AssertContains("Release all claims", withClaim);

                    await coordination.ReleaseClaimAsync(claim.Id).ConfigureAwait(false);
                    // Nothing was posted: the gate posts the handoff itself, once, and completes.
                    string completed = JsonSerializer.Serialize(
                        await handlers["armada_lead_cycle_complete"](completionArgs));
                    AssertContains("\"Completed\":true", completed);
                    AssertContains("posted_by_gate", completed);
                    AssertFalse((await coordinator.GetStatusAsync().ConfigureAwait(false)).Active);
                    List<CoordinationMessage> board = await coordination.ReadMessagesAsync(
                        CoordinationService.DefaultRoomKey).ConfigureAwait(false);
                    AssertEqual(1, board.Count(message => message.AuthorId == settings.ParticipantKey && message.Content == handoff),
                        "the gate posted the handoff exactly once");
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
