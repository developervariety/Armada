namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the coordination board service: default room provisioning,
    /// message flow, presence, and fleet event mirroring.
    /// </summary>
    public class CoordinationServiceTests : TestSuite
    {
        public override string Name => "Coordination Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("EnsureRoomAsync creates the default room exactly once", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    CoordinationService service = CreateService(testDb);

                    var first = await service.EnumerateRoomsAsync();
                    AssertEqual(1, first.Count);
                    AssertEqual(CoordinationService.DefaultRoomKey, first[0].Key);
                    AssertEqual("Fleet", first[0].Name);

                    var second = await service.EnumerateRoomsAsync();
                    AssertEqual(1, second.Count);
                }
            });

            await RunTest("PostMessageAsync stores notes and refreshes author presence", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    CoordinationService service = CreateService(testDb);

                    await service.PostMessageAsync(
                        CoordinationService.DefaultRoomKey,
                        CoordinationAuthorTypeEnum.Operator,
                        "session-a",
                        "Session A",
                        "I am dispatching a voyage",
                        voyageId: "vyg_example");

                    var messages = await service.ReadMessagesAsync(CoordinationService.DefaultRoomKey);
                    AssertEqual(1, messages.Count);
                    AssertEqual("session-a", messages[0].AuthorId);
                    AssertEqual("vyg_example", messages[0].VoyageId);

                    var participants = await service.EnumerateParticipantsAsync(CoordinationService.DefaultRoomKey, 15);
                    AssertEqual(1, participants.Count);
                    AssertEqual("session-a", participants[0].ParticipantKey);
                }
            });

            await RunTest("HeartbeatAsync upserts one presence row per participant key", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    CoordinationService service = CreateService(testDb);

                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "session-a", "Session A");
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "session-a", "Session A v2");
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "session-b", "Session B");

                    var participants = await service.EnumerateParticipantsAsync(CoordinationService.DefaultRoomKey, 15);
                    AssertEqual(2, participants.Count);
                }
            });

            await RunTest("A long broadcast note is previewed, but directed mail never is", async () =>
            {
                // A full room read returned 57,501 characters and blew the caller's tool
                // output limit, costing every autonomous cycle turns spent spilling the
                // payload to a file. Previewing is the fix -- but a truncated board preview
                // has already hidden five complete reports once, so anything addressed to
                // the caller stays whole whatever else is trimmed.
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                CoordinationService coordination = CreateService(testDb);

                string longBroadcast = new string('b', 4000);
                string longDirected = new string('d', 4000);

                await coordination.PostMessageAsync(
                    CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.Operator,
                    "peer", "peer", longBroadcast).ConfigureAwait(false);
                await coordination.PostMessageAsync(
                    CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.Operator,
                    "peer", "peer", longDirected, toParticipantKey: "me").ConfigureAwait(false);

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers =
                    RegisterCoordinationTools(testDb, coordination);

                JsonElement result = ReadResult(await handlers["armada_coordination_read"](
                    Args(new { participantKey = "me" })).ConfigureAwait(false));

                JsonElement messages = result.GetProperty("Messages");
                AssertEqual(2, messages.GetArrayLength(), "Both notes should be visible to the addressee.");
                AssertEqual(1, result.GetProperty("TruncatedMessageCount").GetInt32(),
                    "Only the broadcast note should be previewed.");

                foreach (JsonElement message in messages.EnumerateArray())
                {
                    bool directed = message.GetProperty("ToParticipantKey").ValueKind == JsonValueKind.String;
                    int contentLength = message.GetProperty("Content").GetString()!.Length;
                    AssertEqual(4000, message.GetProperty("ContentLength").GetInt32(),
                        "ContentLength must always report the full note length.");

                    if (directed)
                    {
                        AssertFalse(message.GetProperty("Truncated").GetBoolean(),
                            "Directed mail must never be truncated.");
                        AssertEqual(4000, contentLength, "Directed mail must arrive whole.");
                    }
                    else
                    {
                        AssertTrue(message.GetProperty("Truncated").GetBoolean(),
                            "A long broadcast note should be previewed.");
                        AssertTrue(contentLength < 4000, "The preview should be shorter than the note.");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("includeFullContent returns previewed notes whole", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                CoordinationService coordination = CreateService(testDb);

                await coordination.PostMessageAsync(
                    CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.Operator,
                    "peer", "peer", new string('b', 4000)).ConfigureAwait(false);

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers =
                    RegisterCoordinationTools(testDb, coordination);

                JsonElement previewed = ReadResult(await handlers["armada_coordination_read"](
                    Args(new { participantKey = "me" })).ConfigureAwait(false));
                AssertEqual(1, previewed.GetProperty("TruncatedMessageCount").GetInt32());

                JsonElement whole = ReadResult(await handlers["armada_coordination_read"](
                    Args(new { participantKey = "me", includeFullContent = true })).ConfigureAwait(false));
                AssertEqual(0, whole.GetProperty("TruncatedMessageCount").GetInt32(),
                    "The escape hatch must return every note whole.");
                AssertEqual(4000,
                    whole.GetProperty("Messages")[0].GetProperty("Content").GetString()!.Length);
            }).ConfigureAwait(false);

            await RunTest("A short note is returned whole and is not marked truncated", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                CoordinationService coordination = CreateService(testDb);

                await coordination.PostMessageAsync(
                    CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.Operator,
                    "peer", "peer", "short note").ConfigureAwait(false);

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers =
                    RegisterCoordinationTools(testDb, coordination);

                JsonElement result = ReadResult(await handlers["armada_coordination_read"](
                    Args(new { })).ConfigureAwait(false));
                JsonElement message = result.GetProperty("Messages")[0];

                AssertEqual("short note", message.GetProperty("Content").GetString());
                AssertFalse(message.GetProperty("Truncated").GetBoolean());
                AssertEqual(0, result.GetProperty("TruncatedMessageCount").GetInt32());
            }).ConfigureAwait(false);

            await RunTest("BuildSystemNoteContent mirrors only selected event types and appends context", () =>
            {
                string? dispatched = CoordinationService.BuildSystemNoteContent(
                    "voyage.dispatched", "Voyage dispatched", null, null, "vyg_example");
                AssertNotNull(dispatched);
                AssertContains("[fleet]", dispatched!);
                AssertContains("vyg_example", dispatched);

                string? failed = CoordinationService.BuildSystemNoteContent(
                    "mission.failed", "Mission failed", "mission", "msn_example", null, "msn_example");
                AssertNotNull(failed);
                AssertContains("msn_example", failed!);

                AssertNull(CoordinationService.BuildSystemNoteContent(
                    "captain.updated", "Captain updated"));
                AssertNull(CoordinationService.BuildSystemNoteContent("", "empty"));
            });

            await RunTest("Unknown rooms provision on demand and the default room arrives on first post", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    CoordinationService service = CreateService(testDb);

                    var messages = await service.ReadMessagesAsync("ad-hoc-room");
                    AssertEqual(0, messages.Count);

                    var rooms = await service.EnumerateRoomsAsync();
                    AssertEqual(1, rooms.Count);
                    AssertEqual("ad-hoc-room", rooms[0].Key);

                    await service.PostMessageAsync(
                        CoordinationService.DefaultRoomKey,
                        CoordinationAuthorTypeEnum.Operator,
                        "session-a",
                        "Session A",
                        "hello");

                    rooms = await service.EnumerateRoomsAsync();
                    AssertEqual(2, rooms.Count);
                }
            });
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> RegisterCoordinationTools(
            TestDatabase testDb, CoordinationService coordination)
        {
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers =
                new Dictionary<string, Func<JsonElement?, Task<object>>>();
            McpCoordinationTools.Register(
                (name, _, _, handler) => { handlers[name] = handler; },
                testDb.Driver,
                coordination);
            return handlers;
        }

        private static JsonElement Args(object value)
        {
            return JsonSerializer.SerializeToElement(value);
        }

        private static JsonElement ReadResult(object result)
        {
            return JsonSerializer.SerializeToElement(result);
        }

        private static CoordinationService CreateService(TestDatabase testDb)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new CoordinationService(logging, testDb.Driver);
        }
    }
}
