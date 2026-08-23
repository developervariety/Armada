namespace Armada.Test.Unit.Suites.Services
{
    using System;
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
    /// Tests for coordination claims: reservations, heartbeat refresh, dispatch
    /// conflict detection, and stale-peer inbox items.
    /// </summary>
    public class CoordinationClaimTests : TestSuite
    {
        private static readonly JsonSerializerOptions _JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public override string Name => "CoordinationClaim";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Claim create enumerate and expiry filtering round-trips", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    CoordinationService service = new CoordinationService(logging, testDb.Driver);

                    await service.ClaimAsync("session-a", "Session A", CoordinationClaimSubjectEnum.Objective, "obj_example", "porting slice", 4);
                    await service.ClaimAsync("session-b", "Session B", CoordinationClaimSubjectEnum.Vessel, "vsl_example", null, 4);

                    var active = await service.EnumerateActiveClaimsAsync();
                    AssertEqual(2, active.Count);

                    var objectiveClaims = await service.EnumerateActiveClaimsAsync(CoordinationClaimSubjectEnum.Objective, "obj_example");
                    AssertEqual(1, objectiveClaims.Count);
                    AssertEqual("session-a", objectiveClaims[0].ParticipantKey);

                    await service.ReleaseClaimAsync(objectiveClaims[0].Id);
                    var afterRelease = await service.EnumerateActiveClaimsAsync(CoordinationClaimSubjectEnum.Objective, "obj_example");
                    AssertEqual(0, afterRelease.Count);
                }
            });

            await RunTest("Heartbeat extends a live session's claims and lapsed claims vanish", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    CoordinationService service = new CoordinationService(logging, testDb.Driver);

                    CoordinationClaim claim = await service.ClaimAsync("session-a", "Session A", CoordinationClaimSubjectEnum.Vessel, "vsl_example", null, 0.5);

                    // Force the claim near expiry, then heartbeat: the claim must survive.
                    claim.ExpiresUtc = DateTime.UtcNow.AddMinutes(10);
                    await testDb.Driver.CoordinationClaims.UpdateAsync(claim);
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "session-a", "Session A");

                    var stillActive = await service.EnumerateActiveClaimsAsync(CoordinationClaimSubjectEnum.Vessel, "vsl_example");
                    AssertEqual(1, stillActive.Count);
                    AssertTrue(stillActive[0].ExpiresUtc > DateTime.UtcNow.AddHours(3), "heartbeat must push expiry hours out");

                    // A claim already past expiry is not resurrected by a heartbeat.
                    claim.ExpiresUtc = DateTime.UtcNow.AddMinutes(-5);
                    await testDb.Driver.CoordinationClaims.UpdateAsync(claim);
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "session-a", "Session A");
                    var gone = await service.EnumerateActiveClaimsAsync(CoordinationClaimSubjectEnum.Vessel, "vsl_example");
                    AssertEqual(0, gone.Count);
                }
            });

            await RunTest("FindDispatchConflicts matches vessel and objective claims but not the holder's own", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    CoordinationService service = new CoordinationService(logging, testDb.Driver);

                    await service.ClaimAsync("session-a", "Session A", CoordinationClaimSubjectEnum.Vessel, "vsl_example");
                    await service.ClaimAsync("session-b", "Session B", CoordinationClaimSubjectEnum.Objective, "obj_example");

                    var byVessel = await service.FindDispatchConflictsAsync("vsl_example");
                    AssertEqual(1, byVessel.Count);
                    AssertEqual("session-a", byVessel[0].ParticipantKey);

                    var byObjective = await service.FindDispatchConflictsAsync("vsl_other", "obj_example");
                    AssertEqual(1, byObjective.Count);
                    AssertEqual("session-b", byObjective[0].ParticipantKey);

                    var ownExcluded = await service.FindDispatchConflictsAsync("vsl_example", null, "session-a");
                    AssertEqual(0, ownExcluded.Count);

                    var none = await service.FindDispatchConflictsAsync("vsl_unrelated");
                    AssertEqual(0, none.Count);
                }
            });

            await RunTest("Silent peer holding an active claim surfaces in the inbox", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    CoordinationService service = new CoordinationService(logging, testDb.Driver);
                    InboxService inbox = new InboxService(testDb.Driver, logging);

                    // Silent holder: claim without any presence row at all.
                    await service.ClaimAsync("ghost-session", "Ghost Session", CoordinationClaimSubjectEnum.Vessel, "vsl_example");

                    // Live holder whose presence row is fresh.
                    await service.ClaimAsync("live-session", "Live Session", CoordinationClaimSubjectEnum.Vessel, "vsl_other");
                    await service.HeartbeatAsync(CoordinationService.DefaultRoomKey, "live-session", "Live Session");

                    var items = await inbox.GetInboxAsync();
                    items.RemoveAll(i => i.Kind != "StalePeer");
                    AssertEqual(1, items.Count);
                    AssertContains("ghost-session", items[0].Detail);
                    AssertEqual("vessel", items[0].EntityType);
                    AssertEqual("vsl_example", items[0].EntityId);
                }
            });

            await RunTest("MCP tool claims announces on the board and lists through read", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    CoordinationService coordination = new CoordinationService(logging, testDb.Driver);

                    Func<JsonElement?, Task<object>>? claimHandler = null;
                    Func<JsonElement?, Task<object>>? readHandler = null;
                    McpCoordinationTools.Register(
                        (name, _, _, h) =>
                        {
                            if (name == "armada_coordination_claim") claimHandler = h;
                            if (name == "armada_coordination_read") readHandler = h;
                        },
                        testDb.Driver,
                        coordination);
                    AssertNotNull(claimHandler, "armada_coordination_claim handler must be registered");

                    JsonElement engage = JsonSerializer.SerializeToElement(new
                    {
                        action = "claim",
                        subjectType = "objective",
                        subjectId = "obj_example",
                        note = "refining scope",
                        participantKey = "session-a",
                        displayName = "Session A"
                    }, _JsonOpts);
                    object result = await claimHandler!(engage).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("obj_example", resultJson);
                    AssertTrue(!resultJson.Contains("\"error\""), "claim should succeed");

                    JsonElement list = JsonSerializer.SerializeToElement(new { action = "list" }, _JsonOpts);
                    result = await claimHandler(list).ConfigureAwait(false);
                    AssertContains("session-a", JsonSerializer.Serialize(result));

                    JsonElement read = JsonSerializer.SerializeToElement(new { }, _JsonOpts);
                    result = await readHandler!(read).ConfigureAwait(false);
                    string readJson = JsonSerializer.Serialize(result);
                    AssertContains("activeClaims", readJson.Replace("ActiveClaims", "activeClaims"));
                    AssertContains("[claim]", readJson);
                }
            });
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }
    }
}
