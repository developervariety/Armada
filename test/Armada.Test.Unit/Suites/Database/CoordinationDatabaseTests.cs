namespace Armada.Test.Unit.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for coordination room, message, and participant database operations.
    /// </summary>
    public class CoordinationDatabaseTests : TestSuite
    {
        public override string Name => "Coordination Database";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Room create read by id and read by key round-trips", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    CoordinationRoom room = new CoordinationRoom
                    {
                        Key = "fleet",
                        Name = "Fleet",
                        Description = "Shared board"
                    };
                    await db.CoordinationRooms.CreateAsync(room);

                    CoordinationRoom? read = await db.CoordinationRooms.ReadAsync(room.Id);
                    AssertNotNull(read);
                    AssertEqual("fleet", read!.Key);
                    AssertEqual("Fleet", read.Name);
                    AssertEqual("Shared board", read.Description);

                    CoordinationRoom? byKey = await db.CoordinationRooms.ReadByKeyAsync("fleet");
                    AssertNotNull(byKey);
                    AssertEqual(room.Id, byKey!.Id);
                }
            });

            await RunTest("Message create enumerate ordered ascending and delete", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    CoordinationRoom room = new CoordinationRoom { Key = "fleet", Name = "Fleet" };
                    await db.CoordinationRooms.CreateAsync(room);

                    await db.CoordinationMessages.CreateAsync(new CoordinationMessage
                    {
                        CoordinationRoomId = room.Id,
                        AuthorType = CoordinationAuthorTypeEnum.Operator,
                        AuthorId = "session-a",
                        AuthorName = "Session A",
                        Content = "claiming voyage work"
                    });
                    await db.CoordinationMessages.CreateAsync(new CoordinationMessage
                    {
                        CoordinationRoomId = room.Id,
                        AuthorType = CoordinationAuthorTypeEnum.System,
                        AuthorName = "armada",
                        Content = "[fleet] voyage dispatched",
                        VoyageId = "vyg_example"
                    });

                    List<CoordinationMessage> messages = await db.CoordinationMessages.EnumerateByRoomAsync(room.Id);
                    AssertEqual(2, messages.Count);
                    AssertEqual(CoordinationAuthorTypeEnum.Operator, messages[0].AuthorType);
                    AssertEqual("vyg_example", messages[1].VoyageId);
                    AssertNull(messages[0].VoyageId);

                    List<CoordinationMessage> limited = await db.CoordinationMessages.EnumerateByRoomAsync(room.Id, null, 1);
                    AssertEqual(1, limited.Count);

                    await db.CoordinationMessages.DeleteByRoomAsync(room.Id);
                    List<CoordinationMessage> emptied = await db.CoordinationMessages.EnumerateByRoomAsync(room.Id);
                    AssertEqual(0, emptied.Count);
                }
            });

            await RunTest("Participant upsert keeps one row per key and active window filters stale rows", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    CoordinationRoom room = new CoordinationRoom { Key = "fleet", Name = "Fleet" };
                    await db.CoordinationRooms.CreateAsync(room);

                    await db.CoordinationParticipants.UpsertAsync(new CoordinationParticipant
                    {
                        CoordinationRoomId = room.Id,
                        ParticipantKey = "session-a",
                        DisplayName = "Session A"
                    });
                    await db.CoordinationParticipants.UpsertAsync(new CoordinationParticipant
                    {
                        CoordinationRoomId = room.Id,
                        ParticipantKey = "session-a",
                        DisplayName = "Session A Renamed"
                    });

                    List<CoordinationParticipant> all = await db.CoordinationParticipants.EnumerateAllInRoomAsync(room.Id);
                    AssertEqual(1, all.Count);
                    AssertEqual("Session A Renamed", all[0].DisplayName);

                    // Backdate one row past the activity window, then confirm only fresh rows survive.
                    await db.CoordinationParticipants.PruneAsync(room.Id, DateTime.UtcNow.AddMinutes(-30));
                    all[0].DisplayName = "Backdated";
                    await db.CoordinationParticipants.PruneAsync(room.Id, DateTime.UtcNow.AddMinutes(-5));

                    List<CoordinationParticipant> active = await db.CoordinationParticipants.EnumerateByRoomAsync(room.Id, 15);
                    AssertEqual(1, active.Count);

                    await db.CoordinationParticipants.PruneAsync(room.Id, DateTime.UtcNow.AddMinutes(1));
                    List<CoordinationParticipant> pruned = await db.CoordinationParticipants.EnumerateAllInRoomAsync(room.Id);
                    AssertEqual(0, pruned.Count);
                }
            });
        }
    }
}
