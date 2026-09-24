namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

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

            await RunTest("Fleet events written by the admiral reach the board as system notes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    CoordinationService coordination = new CoordinationService(logging, testDb.Driver);
                    CoordinationFleetEventMirror.Attach(testDb.Driver, coordination, logging);

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                    settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
                    settings.StageWatchdogTimeoutMinutes = 5;
                    StubGitService git = new StubGitService();
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    IMissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains,
                        resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    AdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captains, missions,
                        new VoyageService(logging, testDb.Driver), docks, git: git);

                    // voyage.dispatched: the admiral's dispatch path.
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("MirrorVessel", "https://github.com/test/repo")).ConfigureAwait(false);
                    Voyage voyage = await admiral.DispatchVoyageAsync("Mirrored voyage", "A test", vessel.Id,
                        new List<MissionDescription> { new MissionDescription("Mission 1", "Desc 1") }).ConfigureAwait(false);

                    // mission.failed: the admiral's stage watchdog, run by its health check.
                    Mission stale = await testDb.Driver.Missions.CreateAsync(new Mission("Stale stage mission")
                    {
                        Status = MissionStatusEnum.Assigned
                    }).ConfigureAwait(false);
                    await SetMissionLastUpdateUtcAsync(testDb, stale.Id, DateTime.UtcNow.AddMinutes(-6)).ConfigureAwait(false);
                    await admiral.HealthCheckAsync().ConfigureAwait(false);

                    List<CoordinationMessage> notes = await coordination.ReadMessagesAsync(CoordinationService.DefaultRoomKey).ConfigureAwait(false);
                    CoordinationMessage? dispatched = notes.FirstOrDefault(m => m.VoyageId == voyage.Id && m.Content.Contains("Voyage dispatched", StringComparison.Ordinal));
                    AssertNotNull(dispatched, "the dispatched voyage must appear on the board; notes: " + String.Join(" | ", notes.Select(m => m.Content)));
                    AssertEqual(CoordinationAuthorTypeEnum.System, dispatched!.AuthorType, "a mirrored note is a system note");
                    AssertContains("[fleet]", dispatched.Content);

                    CoordinationMessage? failed = notes.FirstOrDefault(m => m.MissionId == stale.Id);
                    AssertNotNull(failed, "the mission failed by the stage watchdog must appear on the board");
                    AssertContains("[fleet] Mission failed by stage watchdog", failed!.Content);
                }
            });

            await RunTest("Event types the board does not mirror stay off the board", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    CoordinationService coordination = new CoordinationService(logging, testDb.Driver);
                    CoordinationFleetEventMirror.Attach(testDb.Driver, coordination, logging);

                    await testDb.Driver.Events.CreateAsync(new ArmadaEvent("captain.launched", "Captain launched")).ConfigureAwait(false);
                    await testDb.Driver.Events.CreateAsync(new ArmadaEvent("mission.cancelled", "Mission cancelled: example")
                    {
                        MissionId = "msn_example"
                    }).ConfigureAwait(false);

                    List<CoordinationMessage> notes = await coordination.ReadMessagesAsync(CoordinationService.DefaultRoomKey).ConfigureAwait(false);
                    AssertEqual(1, notes.Count, "only the mirrored event type becomes a note");
                    AssertEqual("[fleet] Mission cancelled: example (mission msn_example)", notes[0].Content);
                }
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

        private static async Task SetMissionLastUpdateUtcAsync(TestDatabase testDb, string missionId, DateTime lastUpdateUtc)
        {
            using (SqliteConnection conn = new SqliteConnection(testDb.ConnectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE missions SET last_update_utc = @last_update_utc WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", missionId);
                    cmd.Parameters.AddWithValue("@last_update_utc", lastUpdateUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        private static CoordinationService CreateService(TestDatabase testDb)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new CoordinationService(logging, testDb.Driver);
        }
    }
}
