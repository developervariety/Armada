namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
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

        private static CoordinationService CreateService(TestDatabase testDb)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new CoordinationService(logging, testDb.Driver);
        }
    }
}
