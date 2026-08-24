namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the captain voice channel ([ARMADA:NOTE] parsing and redaction) and
    /// the armada_campaign_status aggregation tool.
    /// </summary>
    public class CoordinationCaptainVoiceTests : TestSuite
    {
        private static readonly JsonSerializerOptions _JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public override string Name => "CoordinationCaptainVoice";

        protected override async Task RunTestsAsync()
        {
            await RunTest("ProgressParser recognizes a board-note marker line", () =>
            {
                ProgressParser.ProgressSignal? signal = ProgressParser.TryParse("[ARMADA:NOTE] landed the ExampleOem J1587 slice");
                AssertNotNull(signal);
                AssertEqual("note", signal!.Type);
                AssertEqual("landed the ExampleOem J1587 slice", signal.Value);

                AssertNull(ProgressParser.TryParse("prose mentioning [ARMADA:NOTE] mid-line is not a signal"));
                return Task.CompletedTask;
            });

            await RunTest("RedactSecrets strips credential-shaped content from captain notes", () =>
            {
                string secretBody = new string('a', 24);
                string note = "used api_key=" + secretBody + " to auth";
                string redacted = PapercutParser.RedactSecrets(note);
                AssertTrue(!redacted.Contains(secretBody), "secret value must not survive");
                AssertContains("[REDACTED]", redacted);
            });

            await RunTest("Addressed notes reach their target and stay visible to everyone unfiltered", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    CoordinationService service = new CoordinationService(logging, testDb.Driver);

                    await service.PostMessageAsync(
                        CoordinationService.DefaultRoomKey,
                        CoordinationAuthorTypeEnum.Operator,
                        "session-a", "Session A",
                        "please audit the ExampleOem lane",
                        toParticipantKey: "helper-1");
                    await service.PostMessageAsync(
                        CoordinationService.DefaultRoomKey,
                        CoordinationAuthorTypeEnum.Operator,
                        "session-a", "Session A",
                        "fleet-wide note");

                    var forHelper = await service.ReadMessagesAsync(CoordinationService.DefaultRoomKey, visibleToParticipantKey: "helper-1");
                    AssertEqual(2, forHelper.Count);

                    var forOther = await service.ReadMessagesAsync(CoordinationService.DefaultRoomKey, visibleToParticipantKey: "session-b");
                    AssertEqual(1, forOther.Count);
                    AssertContains("fleet-wide", forOther[0].Content);

                    var unfiltered = await service.ReadMessagesAsync(CoordinationService.DefaultRoomKey);
                    AssertEqual(2, unfiltered.Count);
                    AssertEqual("helper-1", unfiltered[0].ToParticipantKey);
                }
            });

            await RunTest("Voyage board-notes block renders authors and replaces prior sections", () =>
            {
                List<CoordinationMessage> notes = new List<CoordinationMessage>
                {
                    new CoordinationMessage { AuthorName = "Session A", Content = "hold off regen slice" },
                    new CoordinationMessage { AuthorName = "armada", Content = "[claims] overlap on vsl_example" }
                };

                string block = MissionService.BuildVoyageBoardNotesBlock(notes);
                AssertContains("### Board notes on this voyage", block);
                AssertContains("[Session A] hold off regen slice", block);
                AssertContains("[armada]", block);

                string description = "base brief\n\nmore context\n\n" + block;
                string cleaned = MissionService.RemoveVoyageBoardNotesSection(description);
                AssertTrue(!cleaned.Contains("Board notes on this voyage"), "prior section must be stripped");
                AssertContains("base brief", cleaned);
                AssertContains("more context", cleaned);

                AssertEqual(String.Empty, MissionService.BuildVoyageBoardNotesBlock(null));
                return Task.CompletedTask;
            });

            await RunTest("Addressed notes emit wakes that surface for the target and clear on acknowledge", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    CoordinationService service = new CoordinationService(logging, testDb.Driver);

                    await service.PostMessageAsync(
                        CoordinationService.DefaultRoomKey,
                        CoordinationAuthorTypeEnum.Operator,
                        "session-a", "Session A",
                        "please audit the ExampleOem lane",
                        toParticipantKey: "helper-1");
                    await service.PostMessageAsync(
                        CoordinationService.DefaultRoomKey,
                        CoordinationAuthorTypeEnum.Operator,
                        "session-a", "Session A",
                        "fleet-wide note");

                    var wakesForHelper = await service.EnumerateUnreadWakesAsync("helper-1");
                    AssertEqual(1, wakesForHelper.Count);
                    AssertContains("[to=helper-1]", wakesForHelper[0].Payload);
                    AssertContains("audit the ExampleOem lane", wakesForHelper[0].Payload);
                    AssertEqual(SignalTypeEnum.Wake, wakesForHelper[0].Type);

                    var wakesForOther = await service.EnumerateUnreadWakesAsync("session-b");
                    AssertEqual(0, wakesForOther.Count);

                    await testDb.Driver.Signals.MarkReadAsync(wakesForHelper[0].Id);
                    var afterAck = await service.EnumerateUnreadWakesAsync("helper-1");
                    AssertEqual(0, afterAck.Count);
                }
            });

            await RunTest("CampaignStatus resolves a tagged hub into lanes slices claims and notes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    CoordinationService coordination = new CoordinationService(logging, testDb.Driver);

                    Objective hub = new Objective { Title = "campaign hub", Tags = new List<string> { "campaign:porting" } };
                    await testDb.Driver.Objectives.CreateAsync(hub);
                    Objective lane = new Objective { Title = "lane jpro", ParentObjectiveId = hub.Id };
                    await testDb.Driver.Objectives.CreateAsync(lane);
                    Objective slice = new Objective { Title = "ledger pass bendix", ParentObjectiveId = lane.Id };
                    await testDb.Driver.Objectives.CreateAsync(slice);
                    Objective stray = new Objective { Title = "unrelated feature" };
                    await testDb.Driver.Objectives.CreateAsync(stray);

                    await coordination.ClaimAsync("session-a", "Session A", CoordinationClaimSubjectEnum.Vessel, "vsl_example", "working the lane");
                    await coordination.PostMessageAsync(
                        CoordinationService.DefaultRoomKey,
                        Armada.Core.Enums.CoordinationAuthorTypeEnum.Operator,
                        "session-a",
                        "Session A",
                        "claimed vsl_example");

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpCoordinationTools.Register(
                        (name, _, _, h) => { if (name == "armada_campaign_status") handler = h; },
                        testDb.Driver,
                        coordination);
                    AssertNotNull(handler, "armada_campaign_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { tag = "campaign:porting" }, _JsonOpts);
                    object result = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("campaign hub", json);
                    AssertContains("lane jpro", json);
                    AssertContains("ledger pass bendix", json);
                    AssertTrue(!json.Contains("unrelated feature"), "objects outside the campaign tree must not appear");
                    AssertContains("session-a", json);
                    AssertContains("claimed vsl_example", json);

                    JsonElement noTag = JsonSerializer.SerializeToElement(new { }, _JsonOpts);
                    object error = await handler(noTag).ConfigureAwait(false);
                    AssertContains("no campaign roots resolved", JsonSerializer.Serialize(error));
                }
            });
        }
    }
}
