#nullable enable

namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Provider-backed round trips for the coordination board: rooms, participants, messages and claims.
    /// MySQL and SQL Server do not store the board, so each case is a named, counted skip there.
    /// </summary>
    internal sealed class CoordinationDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        internal CoordinationDatabaseTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        internal async Task VerifyRoomsAsync(CancellationToken token)
        {
            SkipWhereNotStored();
            CoordinationRoom room = await CreateRoomAsync("rooms", token).ConfigureAwait(false);
            try
            {
                DatabaseAssert.AllProperties(room, await _Driver.CoordinationRooms.ReadAsync(room.Id, token).ConfigureAwait(false), "CoordinationRoom");
                DatabaseAssert.AllProperties(room, await _Driver.CoordinationRooms.ReadByKeyAsync(room.Key, token).ConfigureAwait(false), "CoordinationRoom by key");

                room.Name = "Renamed room 名前";
                room.Description = null;
                CoordinationRoom updated = await _Driver.CoordinationRooms.UpdateAsync(room, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.CoordinationRooms.ReadAsync(room.Id, token).ConfigureAwait(false), "Reopened CoordinationRoom");
                }

                List<CoordinationRoom> rooms = await _Driver.CoordinationRooms.EnumerateAsync(token).ConfigureAwait(false);
                DatabaseAssert.ContainsIds(rooms, item => item.Id, room.Id);
            }
            finally
            {
                if (!_NoCleanup) await _Driver.CoordinationRooms.DeleteAsync(room.Id, token).ConfigureAwait(false);
            }

            if (!_NoCleanup)
                DatabaseAssert.True(await _Driver.CoordinationRooms.ReadAsync(room.Id, token).ConfigureAwait(false) == null, "Deleted coordination room is gone");
        }

        internal async Task VerifyParticipantsAsync(CancellationToken token)
        {
            SkipWhereNotStored();
            CoordinationRoom room = await CreateRoomAsync("participants", token).ConfigureAwait(false);
            try
            {
                string key = "participant-" + Guid.NewGuid().ToString("N").Substring(0, 12);
                CoordinationParticipant participant = new CoordinationParticipant
                {
                    CoordinationRoomId = room.Id,
                    TenantId = room.TenantId,
                    ParticipantKey = key,
                    DisplayName = "Operator 名前",
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-3)
                };
                CoordinationParticipant created = await _Driver.CoordinationParticipants.UpsertAsync(participant, token).ConfigureAwait(false);
                List<CoordinationParticipant> all = await _Driver.CoordinationParticipants.EnumerateAllInRoomAsync(room.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1, all.Count, "One participant in the room");
                DatabaseAssert.AllProperties(created, all[0], "CoordinationParticipant");
                DatabaseAssert.AllProperties(created, await _Driver.CoordinationParticipants.ReadLatestByKeyAsync(key, token).ConfigureAwait(false), "CoordinationParticipant by key");
                DatabaseAssert.Equal(1, (await _Driver.CoordinationParticipants.EnumerateByRoomAsync(room.Id, 15, token).ConfigureAwait(false)).Count, "Recently seen participant is active");

                CoordinationParticipant heartbeat = new CoordinationParticipant
                {
                    CoordinationRoomId = room.Id,
                    TenantId = room.TenantId,
                    ParticipantKey = key,
                    DisplayName = "Operator renamed"
                };
                await _Driver.CoordinationParticipants.UpsertAsync(heartbeat, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    List<CoordinationParticipant> afterHeartbeat = await reopened.CoordinationParticipants.EnumerateAllInRoomAsync(room.Id, token).ConfigureAwait(false);
                    DatabaseAssert.Equal(1, afterHeartbeat.Count, "A heartbeat for a known key updates the one participant row");
                    DatabaseAssert.Equal(created.Id, afterHeartbeat[0].Id, "A heartbeat keeps the original participant id");
                    DatabaseAssert.Equal("Operator renamed", afterHeartbeat[0].DisplayName, "A heartbeat updates the display name");
                    DatabaseAssert.UtcInstant(created.CreatedUtc, afterHeartbeat[0].CreatedUtc, "A heartbeat keeps the participant creation time");
                    DatabaseAssert.UtcInstant(heartbeat.LastSeenUtc, afterHeartbeat[0].LastSeenUtc, "A heartbeat moves the last-seen time");
                }

                // Concurrent first heartbeats for one new key must all succeed and leave exactly one row.
                for (int round = 0; round < 10; round++)
                {
                    string racedKey = key + "-race-" + round;
                    List<Task<CoordinationParticipant>> attempts = new List<Task<CoordinationParticipant>>();
                    for (int index = 0; index < 8; index++)
                    {
                        CoordinationParticipant contender = new CoordinationParticipant
                        {
                            CoordinationRoomId = room.Id,
                            TenantId = room.TenantId,
                            ParticipantKey = racedKey,
                            DisplayName = "Contender " + index
                        };
                        attempts.Add(Task.Run(() => _Driver.CoordinationParticipants.UpsertAsync(contender, token)));
                    }

                    await Task.WhenAll(attempts).ConfigureAwait(false);
                }

                List<CoordinationParticipant> raced = await _Driver.CoordinationParticipants.EnumerateAllInRoomAsync(room.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(11, raced.Count, "Each raced key holds exactly one participant row");

                await _Driver.CoordinationParticipants.PruneAsync(room.Id, DateTime.UtcNow.AddMinutes(1), token).ConfigureAwait(false);
                DatabaseAssert.Equal(0, (await _Driver.CoordinationParticipants.EnumerateAllInRoomAsync(room.Id, token).ConfigureAwait(false)).Count, "Prune removes participants last seen before the cutoff");
            }
            finally
            {
                if (!_NoCleanup)
                {
                    await _Driver.CoordinationParticipants.DeleteByRoomAsync(room.Id, token).ConfigureAwait(false);
                    await _Driver.CoordinationRooms.DeleteAsync(room.Id, token).ConfigureAwait(false);
                }
            }
        }

        internal async Task VerifyMessagesAsync(CancellationToken token)
        {
            SkipWhereNotStored();
            CoordinationRoom room = await CreateRoomAsync("messages", token).ConfigureAwait(false);
            try
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
                string voyageId = "vyg_board_" + suffix;
                DateTime before = DateTime.UtcNow.AddSeconds(-30);
                CoordinationMessage directed = await _Driver.CoordinationMessages.CreateAsync(new CoordinationMessage
                {
                    CoordinationRoomId = room.Id,
                    TenantId = room.TenantId,
                    AuthorType = CoordinationAuthorTypeEnum.Captain,
                    AuthorId = "cpt_board_" + suffix,
                    AuthorName = "Captain 名前",
                    Content = "Directed note ユニコード",
                    VoyageId = voyageId,
                    MissionId = "msn_board_" + suffix,
                    VesselId = "vsl_board_" + suffix,
                    IncidentId = "inc_board_" + suffix,
                    ToParticipantKey = "recipient-" + suffix,
                    CreatedUtc = DateTime.UtcNow.AddSeconds(-2)
                }, token).ConfigureAwait(false);
                CoordinationMessage broadcast = await _Driver.CoordinationMessages.CreateAsync(new CoordinationMessage
                {
                    CoordinationRoomId = room.Id,
                    TenantId = room.TenantId,
                    AuthorType = CoordinationAuthorTypeEnum.Operator,
                    AuthorName = "Operator",
                    Content = "Broadcast note",
                    VoyageId = voyageId,
                    CreatedUtc = DateTime.UtcNow.AddSeconds(-1)
                }, token).ConfigureAwait(false);

                DatabaseAssert.AllProperties(directed, await _Driver.CoordinationMessages.ReadAsync(directed.Id, token).ConfigureAwait(false), "CoordinationMessage");
                directed.Content = "Edited directed note";
                CoordinationMessage updated = await _Driver.CoordinationMessages.UpdateAsync(directed, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(updated, await reopened.CoordinationMessages.ReadAsync(directed.Id, token).ConfigureAwait(false), "Reopened CoordinationMessage");
                }

                DatabaseAssert.Equal(2, (await _Driver.CoordinationMessages.EnumerateByRoomAsync(room.Id, null, 200, token).ConfigureAwait(false)).Count, "Room holds both messages");
                DatabaseAssert.Equal(2, (await _Driver.CoordinationMessages.EnumerateByRoomAsync(room.Id, before, 200, token).ConfigureAwait(false)).Count, "Both messages are after the earlier cutoff");
                DatabaseAssert.Equal(1, (await _Driver.CoordinationMessages.EnumerateVisibleToAsync(room.Id, "someone-else", null, 200, token).ConfigureAwait(false)).Count,
                    "Another participant sees only the broadcast");
                DatabaseAssert.Equal(2, (await _Driver.CoordinationMessages.EnumerateVisibleToAsync(room.Id, directed.ToParticipantKey, null, 200, token).ConfigureAwait(false)).Count,
                    "The recipient sees the broadcast and its directed message");
                DatabaseAssert.Equal(2, (await _Driver.CoordinationMessages.EnumerateByVoyageAsync(voyageId, null, 20, token).ConfigureAwait(false)).Count, "Voyage view holds both messages");

                await _Driver.CoordinationMessages.DeleteAsync(broadcast.Id, token).ConfigureAwait(false);
                DatabaseAssert.True(await _Driver.CoordinationMessages.ReadAsync(broadcast.Id, token).ConfigureAwait(false) == null, "Deleted message is gone");
            }
            finally
            {
                if (!_NoCleanup)
                {
                    await _Driver.CoordinationMessages.DeleteByRoomAsync(room.Id, token).ConfigureAwait(false);
                    await _Driver.CoordinationRooms.DeleteAsync(room.Id, token).ConfigureAwait(false);
                }
            }
        }

        internal async Task VerifyClaimsAsync(CancellationToken token)
        {
            SkipWhereNotStored();
            CoordinationRoom room = await CreateRoomAsync("claims", token).ConfigureAwait(false);
            CoordinationClaim? claim = null;
            try
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
                string subjectId = "vsl_claim_" + suffix;
                claim = await _Driver.CoordinationClaims.CreateAsync(new CoordinationClaim
                {
                    CoordinationRoomId = room.Id,
                    TenantId = room.TenantId,
                    ParticipantKey = "claimant-" + suffix,
                    DisplayName = "Claimant 名前",
                    SubjectType = CoordinationClaimSubjectEnum.Vessel,
                    SubjectId = subjectId,
                    Note = "Working on the vessel",
                    Status = CoordinationClaimStatusEnum.Active,
                    ExpiresUtc = DateTime.UtcNow.AddMinutes(30),
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-1)
                }, token).ConfigureAwait(false);

                DatabaseAssert.AllProperties(claim, await _Driver.CoordinationClaims.ReadAsync(claim.Id, token).ConfigureAwait(false), "CoordinationClaim");
                List<CoordinationClaim> active = await _Driver.CoordinationClaims.EnumerateActiveAsync(CoordinationClaimSubjectEnum.Vessel, subjectId, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1, active.Count, "Active claim on the subject");
                DatabaseAssert.AllProperties(claim, active[0], "Active CoordinationClaim");

                DateTime extended = DateTime.UtcNow.AddHours(2);
                DatabaseAssert.Equal(1, await _Driver.CoordinationClaims.ExtendActiveForParticipantAsync(room.Id, claim.ParticipantKey, extended, token).ConfigureAwait(false),
                    "Heartbeat extends the participant's one active claim");
                CoordinationClaim afterExtend = DatabaseAssert.NotNull(await _Driver.CoordinationClaims.ReadAsync(claim.Id, token).ConfigureAwait(false), "Extended claim");
                DatabaseAssert.UtcInstant(extended, afterExtend.ExpiresUtc, "Extended CoordinationClaim.ExpiresUtc");

                afterExtend.Status = CoordinationClaimStatusEnum.Released;
                afterExtend.Note = null;
                CoordinationClaim released = await _Driver.CoordinationClaims.UpdateAsync(afterExtend, token).ConfigureAwait(false);
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    DatabaseAssert.AllProperties(released, await reopened.CoordinationClaims.ReadAsync(claim.Id, token).ConfigureAwait(false), "Reopened CoordinationClaim");
                }

                DatabaseAssert.Equal(0, (await _Driver.CoordinationClaims.EnumerateActiveAsync(CoordinationClaimSubjectEnum.Vessel, subjectId, token).ConfigureAwait(false)).Count,
                    "A released claim is not active");
                DatabaseAssert.Equal(0, await _Driver.CoordinationClaims.ExtendActiveForParticipantAsync(room.Id, claim.ParticipantKey, extended.AddHours(1), token).ConfigureAwait(false),
                    "A heartbeat does not extend a released claim");
            }
            finally
            {
                if (!_NoCleanup)
                {
                    if (claim != null) await DeleteClaimRowAsync(claim.Id, token).ConfigureAwait(false);
                    await _Driver.CoordinationRooms.DeleteAsync(room.Id, token).ConfigureAwait(false);
                }
            }
        }

        private void SkipWhereNotStored()
        {
            if (_Settings.Type == DatabaseTypeEnum.Mysql)
                throw new DatabaseTestSkipException("coordination_board_not_stored_on_mysql: rooms, participants, messages and claims are stored on SQLite and PostgreSQL only");
            if (_Settings.Type == DatabaseTypeEnum.SqlServer)
                throw new DatabaseTestSkipException("coordination_board_not_stored_on_sqlserver: rooms, participants, messages and claims are stored on SQLite and PostgreSQL only");
        }

        private async Task<CoordinationRoom> CreateRoomAsync(string purpose, CancellationToken token)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            CoordinationRoom room = new CoordinationRoom
            {
                TenantId = "ten_board_" + suffix,
                UserId = "usr_board_" + suffix,
                Key = "board-" + purpose + "-" + suffix,
                Name = "Board " + purpose,
                Description = "Board room for " + purpose + " 部屋",
                CreatedUtc = DateTime.UtcNow.AddMinutes(-10)
            };
            return await _Driver.CoordinationRooms.CreateAsync(room, token).ConfigureAwait(false);
        }

        private async Task DeleteClaimRowAsync(string claimId, CancellationToken token)
        {
            // The claim method set has no delete; remove the fixture row directly.
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "DELETE FROM coordination_claims WHERE id = @id;";
                    DbParameter parameter = command.CreateParameter();
                    parameter.ParameterName = "@id";
                    parameter.Value = claimId;
                    command.Parameters.Add(parameter);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }
    }
}
