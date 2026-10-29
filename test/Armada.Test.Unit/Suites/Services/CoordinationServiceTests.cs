namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Microsoft.Extensions.Time.Testing;
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

            await RunTest("A participant key that alternates between names is reported as shared once per window, and a rename is not", async () =>
            {
                // Two sessions sharing one key alternate names on every post and heartbeat; the
                // presence row shows only the last writer and addressed wakes reach both. One
                // session renaming itself switches once and must not be reported.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTimeProvider time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero));
                    CoordinationService service = CreateService(testDb, time);

                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "renamed", "Session A").ConfigureAwait(false);
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "renamed", "Session A v2").ConfigureAwait(false);
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "renamed", "Session A v2").ConfigureAwait(false);
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "single", "Session C").ConfigureAwait(false);

                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "shared", "session-north").ConfigureAwait(false);
                    await service.PostMessageAsync(CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.Operator,
                        "shared", "session-south", "deploying").ConfigureAwait(false);
                    AssertEqual(0, service.EnumerateSharedParticipantKeys(15).Count, "one switch is a rename, not a shared key");
                    await service.HeartbeatAsync("campaign-alpha", "shared", "session-north").ConfigureAwait(false);
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "shared", "session-south").ConfigureAwait(false);
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "shared", "session-north").ConfigureAwait(false);

                    List<CoordinationSharedParticipantKey> shared = service.EnumerateSharedParticipantKeys(15);
                    AssertEqual(1, shared.Count, "only the alternating key is shared");
                    AssertEqual("shared", shared[0].ParticipantKey);
                    AssertEqual("session-north,session-south", String.Join(",", shared[0].DisplayNames), "both names, most recent first");
                    AssertEqual(4, shared[0].NameSwitches);
                    AssertContains("own participant key", shared[0].Reason);

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(
                        CoordinationService.SharedParticipantKeyEventType, 50).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "one event per key per window, however often the names alternate");
                    AssertEqual("shared", events[0].EntityId);

                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterCoordinationTools(testDb, service);
                    JsonElement read = ReadResult(await handlers["armada_coordination_read"](Args(new { })).ConfigureAwait(false));
                    JsonElement keys = read.GetProperty("SharedParticipantKeys");
                    AssertEqual(1, keys.GetArrayLength(), "the read result names the shared key");
                    AssertEqual("shared", keys[0].GetProperty("ParticipantKey").GetString());

                    JsonElement sharedBeat = ReadResult(await handlers["armada_coordination_heartbeat"](
                        Args(new { participantKey = "shared", displayName = "session-south" })).ConfigureAwait(false));
                    AssertEqual(JsonValueKind.Object, sharedBeat.GetProperty("SharedParticipantKey").ValueKind, "a session on a shared key is told at its heartbeat");
                    JsonElement singleBeat = ReadResult(await handlers["armada_coordination_heartbeat"](
                        Args(new { participantKey = "single", displayName = "Session C" })).ConfigureAwait(false));
                    AssertEqual(JsonValueKind.Null, singleBeat.GetProperty("SharedParticipantKey").ValueKind, "a key used under one name carries no warning");

                    time.Advance(TimeSpan.FromMinutes(16));
                    AssertEqual(0, service.EnumerateSharedParticipantKeys(15).Count, "names outside the window no longer count");
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "shared", "session-north").ConfigureAwait(false);
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "shared", "session-south").ConfigureAwait(false);
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "shared", "session-north").ConfigureAwait(false);
                    events = await testDb.Driver.Events.EnumerateByTypeAsync(
                        CoordinationService.SharedParticipantKeyEventType, 50).ConfigureAwait(false);
                    AssertEqual(2, events.Count, "a key still shared in a later window is reported again");
                }
            }).ConfigureAwait(false);

            await RunTest("A note addressed to a participant's name is delivered to that participant's key, and so is its wake", async () =>
            {
                // A peer addressed a session by the name it posts under. The session reads and
                // acknowledges as its participant key, so a note stored under the name reached
                // neither its read filter nor its wakes.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CoordinationService service = CreateService(testDb);
                    await service.PostMessageAsync(CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.Operator,
                        "session-key", "session-name", "working on the deploy").ConfigureAwait(false);
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "twin-a", "twin-name").ConfigureAwait(false);
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "twin-b", "twin-name").ConfigureAwait(false);

                    CoordinationMessage byName = await service.PostMessageAsync(CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.Operator,
                        "peer", "peer", "issue for you", toParticipantKey: "session-name").ConfigureAwait(false);
                    AssertEqual("session-key", byName.ToParticipantKey, "a name resolves to the key that uses it");
                    AssertContains("session-key", byName.AddressNotice ?? String.Empty, "the sender is told which key received the note");

                    List<CoordinationMessage> visible = await service.ReadMessagesAsync(
                        CoordinationService.DefaultRoomKey, visibleToParticipantKey: "session-key").ConfigureAwait(false);
                    AssertTrue(visible.Any(m => m.Id == byName.Id), "the key's read sees the note");
                    List<Signal> wakes = await service.EnumerateUnreadWakesAsync("session-key").ConfigureAwait(false);
                    AssertEqual(1, wakes.Count, "the wake goes to the key the session acknowledges as");
                    AssertEqual(0, (await service.EnumerateUnreadWakesAsync("session-name").ConfigureAwait(false)).Count, "no wake waits on the name");

                    CoordinationMessage byKey = await service.PostMessageAsync(CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.Operator,
                        "peer", "peer", "second issue", toParticipantKey: "session-key").ConfigureAwait(false);
                    AssertEqual("session-key", byKey.ToParticipantKey);
                    AssertNull(byKey.AddressNotice, "a real key needs no notice");

                    CoordinationMessage ambiguous = await service.PostMessageAsync(CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.Operator,
                        "peer", "peer", "which twin", toParticipantKey: "twin-name").ConfigureAwait(false);
                    AssertEqual("twin-name", ambiguous.ToParticipantKey, "a name several keys use is stored as given");
                    AssertContains("several participant keys", ambiguous.AddressNotice ?? String.Empty);
                    AssertContains("twin-a, twin-b", ambiguous.AddressNotice ?? String.Empty, "the sender is told every key the note reaches");
                    foreach (string twin in new[] { "twin-a", "twin-b" })
                    {
                        List<CoordinationMessage> twinVisible = await service.ReadMessagesAsync(
                            CoordinationService.DefaultRoomKey, visibleToParticipantKey: twin).ConfigureAwait(false);
                        AssertTrue(twinVisible.Any(m => m.Id == ambiguous.Id), twin + " reads a note addressed to a name it shares");
                        AssertEqual(1, (await service.EnumerateUnreadWakesAsync(twin).ConfigureAwait(false)).Count, twin + " gets its own wake");
                    }

                    CoordinationMessage unknown = await service.PostMessageAsync(CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.Operator,
                        "peer", "peer", "for a session not yet seen", toParticipantKey: "offline-key").ConfigureAwait(false);
                    AssertEqual("offline-key", unknown.ToParticipantKey, "a value nobody uses stays as given");
                    AssertNull(unknown.AddressNotice);
                }
            }).ConfigureAwait(false);

            await RunTest("A note addressed to a session's name reaches its key after an hour of silence and after a restart", async () =>
            {
                // A session posted under its name, then only read the board for over an hour. Its
                // presence row was pruned and its name aged out, so a note addressed to the name
                // was stored under the name: the key's read never returned it and the wake waited
                // on the name.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeTimeProvider time = new FakeTimeProvider(DateTimeOffset.UtcNow);
                    CoordinationService service = CreateService(testDb, time);
                    CoordinationRoom room = await service.EnsureRoomAsync(CoordinationService.DefaultRoomKey).ConfigureAwait(false);
                    await service.PostMessageAsync(CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.Operator,
                        "session-key", "session-name", "landing the fix").ConfigureAwait(false);

                    time.Advance(TimeSpan.FromHours(2));
                    await testDb.Driver.CoordinationParticipants.PruneAsync(room.Id, DateTime.UtcNow.AddMinutes(1)).ConfigureAwait(false);

                    CoordinationMessage note = await service.PostMessageAsync(CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.Operator,
                        "peer-key", "peer-name", "issue for the next deploy", toParticipantKey: "session-name").ConfigureAwait(false);
                    AssertEqual("session-key", note.ToParticipantKey, "the name still resolves to the key that posted under it");
                    List<CoordinationMessage> visible = await service.ReadMessagesAsync(
                        CoordinationService.DefaultRoomKey, visibleToParticipantKey: "session-key").ConfigureAwait(false);
                    AssertTrue(visible.Any(m => m.Id == note.Id), "the key's filtered read returns the note");
                    AssertEqual(1, (await service.EnumerateUnreadWakesAsync("session-key").ConfigureAwait(false)).Count, "the wake is raised for the key");

                    CoordinationMessage storedUnderName = await testDb.Driver.CoordinationMessages.CreateAsync(new CoordinationMessage
                    {
                        CoordinationRoomId = room.Id,
                        AuthorType = CoordinationAuthorTypeEnum.Operator,
                        AuthorId = "peer-key",
                        AuthorName = "peer-name",
                        Content = "stored under the name",
                        ToParticipantKey = "session-name"
                    }).ConfigureAwait(false);

                    CoordinationService restarted = CreateService(testDb, time);
                    await testDb.Driver.CoordinationParticipants.PruneAsync(room.Id, DateTime.UtcNow.AddMinutes(1)).ConfigureAwait(false);
                    CoordinationMessage afterRestart = await restarted.PostMessageAsync(CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.Operator,
                        "peer-key", "peer-name", "second issue", toParticipantKey: "session-name").ConfigureAwait(false);
                    AssertEqual("session-key", afterRestart.ToParticipantKey, "stored notes keep the name's key across a restart");
                    List<CoordinationMessage> restartedVisible = await restarted.ReadMessagesAsync(
                        CoordinationService.DefaultRoomKey, visibleToParticipantKey: "session-key").ConfigureAwait(false);
                    AssertTrue(restartedVisible.Any(m => m.Id == afterRestart.Id), "the key reads the note posted after the restart");
                    AssertTrue(restartedVisible.Any(m => m.Id == storedUnderName.Id), "a note already stored under the name is the key's mail too");
                    AssertEqual(2, (await restarted.EnumerateUnreadWakesAsync("session-key").ConfigureAwait(false)).Count, "each resolved note wakes the key");
                    List<CoordinationMessage> peerVisible = await restarted.ReadMessagesAsync(
                        CoordinationService.DefaultRoomKey, visibleToParticipantKey: "other-key").ConfigureAwait(false);
                    AssertFalse(peerVisible.Any(m => m.Id == storedUnderName.Id), "another key does not read mail addressed to the name");
                }
            }).ConfigureAwait(false);

            await RunTest("A session identified by the participant header is refused when it names a different participant key", () =>
            {
                AssertNull(McpCoordinationTools.ParticipantKeyRefusal(null, "any-key"), "no header: the given key is used");
                AssertNull(McpCoordinationTools.ParticipantKeyRefusal("session-key", null), "no key given: the header key is used");
                AssertNull(McpCoordinationTools.ParticipantKeyRefusal("session-key", " session-key "), "the same key");
                string? refusal = McpCoordinationTools.ParticipantKeyRefusal("session-key", "session-name");
                AssertNotNull(refusal, "a different key is refused, never read silently as the header key");
                AssertContains("session-key", refusal!);
                AssertContains("session-name", refusal!);
            }).ConfigureAwait(false);

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

            await RunTest("A cut wake names its note, and a read by that id returns the note whole", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                CoordinationService coordination = CreateService(testDb);
                string report = new string('r', 3500);

                CoordinationMessage posted = await coordination.PostMessageAsync(
                    CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.Operator,
                    "peer", "peer", report, toParticipantKey: "me").ConfigureAwait(false);

                List<Signal> wakes = await coordination.EnumerateUnreadWakesAsync("me").ConfigureAwait(false);
                AssertEqual(1, wakes.Count, "The addressed note emits one wake.");
                string payload = wakes[0].Payload!;
                AssertTrue(payload.Length < report.Length, "The wake carries a cut of the note, not the whole report.");
                AssertContains("[msg=" + posted.Id + "]", payload);
                AssertContains("messageId=" + posted.Id, payload);
                AssertContains("3500", payload);

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers =
                    RegisterCoordinationTools(testDb, coordination);
                JsonElement byId = ReadResult(await handlers["armada_coordination_read"](
                    Args(new { participantKey = "someone-else", messageId = posted.Id })).ConfigureAwait(false));
                JsonElement message = byId.GetProperty("Message");
                AssertEqual(posted.Id, message.GetProperty("Id").GetString());
                AssertEqual(report, message.GetProperty("Content").GetString(), "A read by id returns the note whole.");
                AssertFalse(message.GetProperty("Truncated").GetBoolean());

                JsonElement missing = ReadResult(await handlers["armada_coordination_read"](
                    Args(new { messageId = "cmsg_absent" })).ConfigureAwait(false));
                AssertContains("cmsg_absent", missing.GetProperty("Error").GetString()!);
            }).ConfigureAwait(false);

            await RunTest("A post, heartbeat, claim or hold that omits the name acts under the caller's identity, and a given name wins", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                CoordinationService coordination = CreateService(testDb);
                DispatchHold hold = new DispatchHold();
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers =
                    new Dictionary<string, Func<JsonElement?, Task<object>>>();
                McpCoordinationTools.Register(
                    (name, _, _, handler) => { handlers[name] = handler; },
                    testDb.Driver,
                    coordination,
                    hold);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective { Title = "claimed" }).ConfigureAwait(false);
                AuthContext caller = AuthContext.Authenticated(
                    Armada.Core.Constants.DefaultTenantId, "usr_operator", true, true, "Bearer", "crd_operator", "operator-key");

                using (McpCallerContext.Begin(caller))
                {
                    JsonElement byCredential = ReadResult(await handlers["armada_coordination_post"](
                        Args(new { content = "no name given" })).ConfigureAwait(false));
                    AssertFalse(byCredential.TryGetProperty("Error", out _), "A post without authorName must not be refused.");
                    AssertEqual("operator-key", byCredential.GetProperty("AuthorName").GetString(),
                        "Without a participant name the note carries the credential's name.");

                    await handlers["armada_coordination_heartbeat"](
                        Args(new { participantKey = "session-key", displayName = "Session Name" })).ConfigureAwait(false);
                    JsonElement byParticipant = ReadResult(await handlers["armada_coordination_post"](
                        Args(new { content = "from the session", authorId = "session-key" })).ConfigureAwait(false));
                    AssertEqual("Session Name", byParticipant.GetProperty("AuthorName").GetString(),
                        "The participant's board name wins over the credential's name.");

                    JsonElement heartbeat = ReadResult(await handlers["armada_coordination_heartbeat"](
                        Args(new { participantKey = "fresh-key" })).ConfigureAwait(false));
                    AssertEqual("operator-key", heartbeat.GetProperty("Participant").GetProperty("DisplayName").GetString(),
                        "A heartbeat without displayName takes the credential's name.");

                    JsonElement claim = ReadResult(await handlers["armada_coordination_claim"](
                        Args(new { action = "claim", subjectType = "objective", subjectId = objective.Id, participantKey = "session-key" })).ConfigureAwait(false));
                    AssertFalse(claim.TryGetProperty("Error", out _), "A claim without displayName must not be refused.");
                    AssertEqual("Session Name", claim.GetProperty("DisplayName").GetString());

                    JsonElement given = ReadResult(await handlers["armada_coordination_post"](
                        Args(new { content = "named", authorName = "explicit-name", authorId = "session-key" })).ConfigureAwait(false));
                    AssertEqual("explicit-name", given.GetProperty("AuthorName").GetString(), "A name the caller gives wins.");

                    JsonElement engaged = ReadResult(await handlers["armada_dispatch_hold"](
                        Args(new { action = "engage", reason = "redeploy" })).ConfigureAwait(false));
                    AssertFalse(engaged.TryGetProperty("Error", out _), "Engaging without setBy must not be refused.");
                    AssertEqual("operator-key", hold.Snapshot()!.SetBy, "The hold names the caller's credential.");
                }

                hold.Clear();
                JsonElement anonymous = ReadResult(await handlers["armada_dispatch_hold"](
                    Args(new { action = "engage", reason = "redeploy" })).ConfigureAwait(false));
                AssertContains("setBy is required", anonymous.GetProperty("Error").GetString()!);

                JsonElement anonymousPost = ReadResult(await handlers["armada_coordination_post"](
                    Args(new { content = "no identity at all" })).ConfigureAwait(false));
                string postRefusal = anonymousPost.GetProperty("Error").GetString()!;
                AssertContains("pass authorName", postRefusal);
                AssertContains("authorId", postRefusal);
                AssertContains(ArmadaMcpHttpServer.ParticipantHeaderName, postRefusal);
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

            await RunTest("A campaign room key is the nearest tagged ancestor's name reduced to a URL-safe slug", () =>
            {
                AssertEqual("campaign-porting-lane", CampaignRoomResolver.BuildRoomKey(" Porting Lane! "));
                AssertEqual("campaign-v2.1_x", CampaignRoomResolver.BuildRoomKey("V2.1_x"));
                AssertNull(CampaignRoomResolver.BuildRoomKey("!!!"), "a name with no safe character has no room");
                AssertNull(CampaignRoomResolver.BuildRoomKey("  "), "an empty campaign name has no room");

                Objective outer = new Objective { Title = "outer", Tags = new List<string> { "campaign:outer" } };
                Objective inner = new Objective { Title = "inner", ParentObjectiveId = outer.Id, Tags = new List<string> { "Campaign:inner" } };
                Objective leaf = new Objective { Title = "leaf", ParentObjectiveId = inner.Id };
                Objective loopA = new Objective { Title = "loop a" };
                Objective loopB = new Objective { Title = "loop b", ParentObjectiveId = loopA.Id };
                loopA.ParentObjectiveId = loopB.Id;
                Dictionary<string, Objective> byId = new List<Objective> { outer, inner, leaf, loopA, loopB }
                    .ToDictionary(o => o.Id, o => o, StringComparer.Ordinal);

                AssertEqual(inner.Id, CampaignRoomResolver.FindCampaignRoot(leaf, byId)!.Id, "the nearest tagged ancestor is the root");
                AssertEqual("inner", CampaignRoomResolver.ReadCampaignName(inner));
                AssertNull(CampaignRoomResolver.FindCampaignRoot(loopA, byId), "a parent loop has no campaign");
            });

            await RunTest("A claim on a campaign objective lands in the campaign room and every other claim in the fleet room", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    CoordinationService service = CreateService(testDb);
                    Objective hub = await testDb.Driver.Objectives.CreateAsync(new Objective { Title = "hub", Tags = new List<string> { "campaign:Example Lane" } }).ConfigureAwait(false);
                    Objective slice = await testDb.Driver.Objectives.CreateAsync(new Objective { Title = "slice", ParentObjectiveId = hub.Id }).ConfigureAwait(false);
                    Objective plain = await testDb.Driver.Objectives.CreateAsync(new Objective { Title = "plain" }).ConfigureAwait(false);

                    CoordinationClaim campaignClaim = await service.ClaimAsync("session-a", "Session A", CoordinationClaimSubjectEnum.Objective, slice.Id).ConfigureAwait(false);
                    CoordinationClaim plainClaim = await service.ClaimAsync("session-a", "Session A", CoordinationClaimSubjectEnum.Objective, plain.Id).ConfigureAwait(false);
                    CoordinationClaim vesselClaim = await service.ClaimAsync("session-a", "Session A", CoordinationClaimSubjectEnum.Vessel, "vsl_example").ConfigureAwait(false);
                    CoordinationClaim explicitClaim = await service.ClaimAsync("session-a", "Session A", CoordinationClaimSubjectEnum.Objective, slice.Id, roomKey: "fleet").ConfigureAwait(false);

                    CoordinationRoom? campaignRoom = await testDb.Driver.CoordinationRooms.ReadByKeyAsync("campaign-example-lane").ConfigureAwait(false);
                    CoordinationRoom? fleetRoom = await testDb.Driver.CoordinationRooms.ReadByKeyAsync(CoordinationService.DefaultRoomKey).ConfigureAwait(false);
                    AssertNotNull(campaignRoom, "the campaign room is created on the first claim that routes to it");
                    AssertEqual("Campaign example-lane", campaignRoom!.Name);
                    AssertEqual(campaignRoom.Id, campaignClaim.CoordinationRoomId, "a claim on a campaign slice lands in its campaign room");
                    AssertEqual(fleetRoom!.Id, plainClaim.CoordinationRoomId, "a claim on an objective outside every campaign stays in the fleet room");
                    AssertEqual(fleetRoom.Id, vesselClaim.CoordinationRoomId, "a vessel claim stays in the fleet room");
                    AssertEqual(fleetRoom.Id, explicitClaim.CoordinationRoomId, "an explicit room key wins over campaign routing");

                    List<CoordinationMessage> campaignNotes = await service.ReadMessagesAsync("campaign-example-lane").ConfigureAwait(false);
                    AssertEqual(1, campaignNotes.Count, "the claim announcement is posted where the claim lives");
                    AssertContains(slice.Id, campaignNotes[0].Content);
                }
            });

            await RunTest("A note posted with a campaign objectiveId lands in the campaign room; other notes stay in the fleet room", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    CoordinationService coordination = CreateService(testDb);
                    Objective hub = await testDb.Driver.Objectives.CreateAsync(new Objective { Title = "hub", Tags = new List<string> { "campaign:alpha" } }).ConfigureAwait(false);
                    Objective lane = await testDb.Driver.Objectives.CreateAsync(new Objective { Title = "lane", ParentObjectiveId = hub.Id }).ConfigureAwait(false);
                    Objective plain = await testDb.Driver.Objectives.CreateAsync(new Objective { Title = "plain" }).ConfigureAwait(false);
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterCoordinationTools(testDb, coordination);

                    JsonElement campaignPost = ReadResult(await handlers["armada_coordination_post"](
                        Args(new { content = "lane note", authorName = "Session A", objectiveId = lane.Id })).ConfigureAwait(false));
                    JsonElement plainPost = ReadResult(await handlers["armada_coordination_post"](
                        Args(new { content = "plain note", authorName = "Session A", objectiveId = plain.Id })).ConfigureAwait(false));
                    JsonElement barePost = ReadResult(await handlers["armada_coordination_post"](
                        Args(new { content = "bare note", authorName = "Session A" })).ConfigureAwait(false));

                    CoordinationRoom? campaignRoom = await testDb.Driver.CoordinationRooms.ReadByKeyAsync("campaign-alpha").ConfigureAwait(false);
                    CoordinationRoom? fleetRoom = await testDb.Driver.CoordinationRooms.ReadByKeyAsync(CoordinationService.DefaultRoomKey).ConfigureAwait(false);
                    AssertNotNull(campaignRoom, "the campaign room is created on the first note that routes to it");
                    AssertEqual(campaignRoom!.Id, campaignPost.GetProperty("CoordinationRoomId").GetString());
                    AssertEqual(fleetRoom!.Id, plainPost.GetProperty("CoordinationRoomId").GetString());
                    AssertEqual(fleetRoom.Id, barePost.GetProperty("CoordinationRoomId").GetString());

                    JsonElement campaignRead = ReadResult(await handlers["armada_coordination_read"](
                        Args(new { roomKey = "campaign-alpha" })).ConfigureAwait(false));
                    AssertEqual(1, campaignRead.GetProperty("Messages").GetArrayLength());
                    AssertEqual("lane note", campaignRead.GetProperty("Messages")[0].GetProperty("Content").GetString());
                }
            });

            await RunTest("Host and suite windows keep fixed expiry while actual holders remain authoritative", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                CoordinationService service = CreateService(testDb);
                using IDisposable holder = await HostWideCommandLock.AcquireAsync(
                    "window-live-suite", "actual running suite", CancellationToken.None).ConfigureAwait(false);
                List<CoordinationClaim> windows = new List<CoordinationClaim>();
                foreach (CoordinationClaimSubjectEnum kind in new[] { CoordinationClaimSubjectEnum.Host, CoordinationClaimSubjectEnum.Suite })
                    windows.Add(await service.ClaimAsync("window-owner", "Window owner", kind,
                        "shared-test-resource", ttlHours: 0.1, roomKey: "fleet").ConfigureAwait(false));
                CoordinationClaim work = await service.ClaimAsync("window-owner", "Window owner", CoordinationClaimSubjectEnum.Objective,
                    "obj_example", ttlHours: 0.5, roomKey: "fleet").ConfigureAwait(false);
                Dictionary<string, DateTime> expires = new Dictionary<string, DateTime>();
                foreach (CoordinationClaim window in windows)
                    expires[window.Id] = (await testDb.Driver.CoordinationClaims.ReadAsync(window.Id).ConfigureAwait(false))!.ExpiresUtc;
                await service.HeartbeatAsync("fleet", "window-owner", "Window owner").ConfigureAwait(false);
                foreach (CoordinationClaim window in windows)
                {
                    CoordinationClaim stored = (await testDb.Driver.CoordinationClaims.ReadAsync(window.Id).ConfigureAwait(false))!;
                    AssertEqual(expires[window.Id], stored.ExpiresUtc, "presence must not extend a bounded execution window");
                    AssertTrue(stored.ExpiresUtc < DateTime.UtcNow.AddMinutes(7), "resource TTL can be shorter than a work claim");
                    stored.ExpiresUtc = DateTime.UtcNow.AddSeconds(-1);
                    await testDb.Driver.CoordinationClaims.UpdateAsync(stored).ConfigureAwait(false);
                }
                AssertTrue((await testDb.Driver.CoordinationClaims.ReadAsync(work.Id).ConfigureAwait(false))!.ExpiresUtc > DateTime.UtcNow.AddHours(3),
                    "ordinary work claims still renew with presence");
                List<CoordinationClaim> active = await service.EnumerateActiveClaimsAsync().ConfigureAwait(false);
                AssertFalse(active.Any(claim => windows.Any(window => window.Id == claim.Id)), "expired window metadata is no longer a reservation");
                AssertTrue(HostWideCommandLock.Snapshot().Holders.Any(actual => actual.Key == "window-live-suite"),
                    "window expiry never revokes or kills a live execution holder");
            });

            await RunTest("A heartbeat in the fleet room keeps the participant's campaign-room claims alive", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    CoordinationService service = CreateService(testDb);
                    CoordinationClaim claim = await service.ClaimAsync(
                        "session-a", "Session A", CoordinationClaimSubjectEnum.Vessel, "vsl_example",
                        ttlHours: 0.5, roomKey: "campaign-alpha").ConfigureAwait(false);
                    AssertTrue(claim.ExpiresUtc < DateTime.UtcNow.AddHours(1), "precondition: the claim expires soon");

                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "session-a", "Session A").ConfigureAwait(false);

                    CoordinationClaim? refreshed = await testDb.Driver.CoordinationClaims.ReadAsync(claim.Id).ConfigureAwait(false);
                    AssertTrue(refreshed!.ExpiresUtc > DateTime.UtcNow.AddHours(3),
                        "a fleet heartbeat must extend a claim held in another room; expires " + refreshed.ExpiresUtc.ToString("O"));
                }
            });

            await RunTest("Voyage events for a campaign objective also reach the campaign room, untagged, so briefs see each note once", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    CoordinationService coordination = new CoordinationService(logging, testDb.Driver);
                    CoordinationFleetEventMirror.Attach(testDb.Driver, coordination, logging);

                    Voyage campaignVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("campaign voyage")).ConfigureAwait(false);
                    Voyage plainVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("plain voyage")).ConfigureAwait(false);
                    Objective hub = await testDb.Driver.Objectives.CreateAsync(new Objective { Title = "hub", Tags = new List<string> { "campaign:alpha" } }).ConfigureAwait(false);
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "slice",
                        ParentObjectiveId = hub.Id,
                        VoyageIds = new List<string> { campaignVoyage.Id }
                    }).ConfigureAwait(false);
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "plain",
                        VoyageIds = new List<string> { plainVoyage.Id }
                    }).ConfigureAwait(false);
                    Mission stage = await testDb.Driver.Missions.CreateAsync(new Mission("campaign stage") { VoyageId = campaignVoyage.Id }).ConfigureAwait(false);

                    // An operator note for the campaign voyage, routed to the campaign room.
                    await coordination.PostMessageAsync(
                        "campaign-alpha", CoordinationAuthorTypeEnum.Operator, "session-a", "Session A",
                        "use the cached fixture", voyageId: campaignVoyage.Id).ConfigureAwait(false);

                    await testDb.Driver.Events.CreateAsync(new ArmadaEvent("voyage.dispatched", "Voyage dispatched: campaign voyage")
                    {
                        VoyageId = campaignVoyage.Id
                    }).ConfigureAwait(false);
                    await testDb.Driver.Events.CreateAsync(new ArmadaEvent("mission.completed", "Mission completed: campaign stage")
                    {
                        MissionId = stage.Id
                    }).ConfigureAwait(false);
                    await testDb.Driver.Events.CreateAsync(new ArmadaEvent("voyage.dispatched", "Voyage dispatched: plain voyage")
                    {
                        VoyageId = plainVoyage.Id
                    }).ConfigureAwait(false);

                    List<CoordinationMessage> fleetNotes = await coordination.ReadMessagesAsync(CoordinationService.DefaultRoomKey).ConfigureAwait(false);
                    AssertEqual(3, fleetNotes.Count, "every mirrored event still reaches the fleet room");

                    List<CoordinationMessage> campaignNotes = await coordination.ReadMessagesAsync("campaign-alpha").ConfigureAwait(false);
                    List<CoordinationMessage> mirrored = campaignNotes.Where(m => m.AuthorType == CoordinationAuthorTypeEnum.System).ToList();
                    AssertEqual(2, mirrored.Count, "the campaign voyage's dispatch and its stage completion reach the campaign room; notes: "
                        + String.Join(" | ", campaignNotes.Select(m => m.Content)));
                    AssertTrue(mirrored.Any(m => m.Content.Contains("Voyage dispatched: campaign voyage", StringComparison.Ordinal)));
                    AssertTrue(mirrored.Any(m => m.Content.Contains("Mission completed: campaign stage", StringComparison.Ordinal) && m.MissionId == stage.Id));
                    AssertTrue(mirrored.All(m => m.VoyageId == null), "a campaign-room copy carries no voyage tag");
                    AssertFalse(campaignNotes.Any(m => m.Content.Contains("plain voyage", StringComparison.Ordinal)),
                        "a voyage outside every campaign does not reach the campaign room");

                    // Brief injection reads voyage-tagged notes from every room: the campaign-room
                    // operator note and the fleet dispatch note, each exactly once.
                    List<CoordinationMessage> briefNotes = await testDb.Driver.CoordinationMessages
                        .EnumerateByVoyageAsync(campaignVoyage.Id, null, 20).ConfigureAwait(false);
                    AssertEqual(2, briefNotes.Count, "voyage notes for the brief: " + String.Join(" | ", briefNotes.Select(m => m.Content)));
                    AssertTrue(briefNotes.Any(m => m.Content == "use the cached fixture"), "a voyage-tagged note in a campaign room reaches the brief");
                    AssertEqual(1, briefNotes.Count(m => m.Content.Contains("Voyage dispatched: campaign voyage", StringComparison.Ordinal)),
                        "the dispatch note reaches the brief once");
                }
            });

            await RunTest("Presence across rooms lists each session once with every room it sits in", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    CoordinationService service = CreateService(testDb);
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "session-a", "Session A").ConfigureAwait(false);
                    await service.HeartbeatAsync("campaign-alpha", "session-a", "Session A").ConfigureAwait(false);
                    await service.HeartbeatAsync("campaign-alpha", "session-b", "Session B").ConfigureAwait(false);

                    List<CoordinationPresenceEntry> presence = await service.EnumeratePresenceAcrossRoomsAsync(15).ConfigureAwait(false);
                    AssertEqual(2, presence.Count, "one entry per participant key");
                    CoordinationPresenceEntry a = presence.Single(p => p.ParticipantKey == "session-a");
                    CoordinationPresenceEntry b = presence.Single(p => p.ParticipantKey == "session-b");
                    AssertEqual(2, a.Rooms.Count);
                    AssertTrue(a.Rooms.Any(r => r.RoomKey == CoordinationService.DefaultRoomKey) && a.Rooms.Any(r => r.RoomKey == "campaign-alpha"));
                    AssertEqual("campaign-alpha", b.Rooms.Single().RoomKey);
                    AssertEqual("Campaign alpha", b.Rooms.Single().RoomName);
                }
            });

            await RunTest("Campaign status returns each root's campaign-room notes without creating a room", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    CoordinationService coordination = CreateService(testDb);
                    await testDb.Driver.Objectives.CreateAsync(new Objective { Title = "hub", Tags = new List<string> { "campaign:alpha" } }).ConfigureAwait(false);
                    await testDb.Driver.Objectives.CreateAsync(new Objective { Title = "other hub", Tags = new List<string> { "campaign:beta" } }).ConfigureAwait(false);
                    await coordination.PostMessageAsync(
                        "campaign-alpha", CoordinationAuthorTypeEnum.Operator, "session-a", "Session A", "alpha room note").ConfigureAwait(false);
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterCoordinationTools(testDb, coordination);

                    string alpha = JsonSerializer.Serialize(await handlers["armada_campaign_status"](
                        Args(new { tag = "campaign:alpha", includeFullContent = true })).ConfigureAwait(false));
                    AssertContains("campaign-alpha", alpha);
                    AssertContains("alpha room note", alpha);

                    string beta = JsonSerializer.Serialize(await handlers["armada_campaign_status"](
                        Args(new { tag = "campaign:beta", includeFullContent = true })).ConfigureAwait(false));
                    AssertContains("campaign-beta", beta);
                    AssertNull(await testDb.Driver.CoordinationRooms.ReadByKeyAsync("campaign-beta").ConfigureAwait(false),
                        "a status read must not create the campaign room");
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

        private static CoordinationService CreateService(TestDatabase testDb, TimeProvider? timeProvider = null)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new CoordinationService(logging, testDb.Driver, null, timeProvider);
        }
    }
}
