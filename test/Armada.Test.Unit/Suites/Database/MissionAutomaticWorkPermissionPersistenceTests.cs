namespace Armada.Test.Unit.Suites.Database
{
    using System;
    using System.Data.Common;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>Permission authority, stale-write isolation and atomic audit at the database boundary.</summary>
    public sealed class MissionAutomaticWorkPermissionPersistenceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Mission Automatic Work Permission Persistence";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Migration leaves an existing mission in legacy mode", async () =>
            {
                using (SqliteConnection connection = new SqliteConnection("Data Source=:memory:"))
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    using (SqliteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "CREATE TABLE missions (id TEXT PRIMARY KEY); INSERT INTO missions (id) VALUES ('existing');";
                        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                        foreach (string statement in MissionAutomaticWorkPermissionSchema.SqliteStatements)
                        {
                            command.CommandText = statement;
                            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                        command.CommandText = "SELECT automatic_work_permission_json, automatic_work_permission_revision FROM missions WHERE id='existing';";
                        using (SqliteDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                        {
                            AssertTrue(await reader.ReadAsync().ConfigureAwait(false));
                            AssertTrue(reader.IsDBNull(0));
                            AssertEqual(0L, reader.GetInt64(1));
                        }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Legacy and both summary shapes preserve the dedicated permission", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(db).ConfigureAwait(false);
                    AssertNull(mission.AutomaticWorkPermission);
                    AssertFalse(mission.AutomaticWorkPermissionInvalid);
                    MissionAutomaticWorkPermission enabled = new MissionAutomaticWorkPermission();
                    AssertTrue(await db.Driver.Missions.TryWriteAutomaticWorkPermissionAsync(mission, enabled, Audit(mission)).ConfigureAwait(false));
                    Mission current = (await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    MissionAutomaticWorkPermission grants = new MissionAutomaticWorkPermission
                    {
                        Verification = Grant(current), DependentAdmission = Grant(current)
                    };
                    AssertTrue(await db.Driver.Missions.TryWriteAutomaticWorkPermissionAsync(current, grants, Audit(current)).ConfigureAwait(false));
                    current = (await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    string expected = JsonSerializer.Serialize(current.AutomaticWorkPermission);
                    Mission summary = (await db.Driver.Missions.ReadSummaryAsync(mission.Id).ConfigureAwait(false))!;
                    MissionSummary light = await ReadLightSummaryAsync(db, mission.Id).ConfigureAwait(false);
                    AssertEqual(expected, JsonSerializer.Serialize(summary.AutomaticWorkPermission));
                    AssertEqual(expected, JsonSerializer.Serialize(light.AutomaticWorkPermission));
                    AssertFalse(light.AutomaticWorkPermissionInvalid);
                    current.AutomaticWorkPermission = null;
                    current.Title = "Metadata cannot remove the server-owned permission";
                    await db.Driver.Missions.UpdateAsync(current).ConfigureAwait(false);
                    Mission after = (await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    AssertEqual(expected, JsonSerializer.Serialize(after.AutomaticWorkPermission));
                    AssertEqual(current.Title, after.Title);
                }
            }).ConfigureAwait(false);

            await RunTest("Revoke is isolated and stale ordinary or dedicated writes cannot restore it", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(db).ConfigureAwait(false);
                    MissionAutomaticWorkPermission grants = new MissionAutomaticWorkPermission
                    {
                        Verification = Grant(mission), DependentAdmission = Grant(mission)
                    };
                    AssertTrue(await db.Driver.Missions.TryWriteAutomaticWorkPermissionAsync(mission, grants, Audit(mission)).ConfigureAwait(false));
                    Mission stale = (await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    Mission current = (await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    MissionAutomaticWorkPermission revoked = new MissionAutomaticWorkPermission { DependentAdmission = grants.DependentAdmission };
                    AssertTrue(await db.Driver.Missions.TryWriteAutomaticWorkPermissionAsync(current, revoked, Audit(current)).ConfigureAwait(false));
                    ArmadaEvent rejectedAudit = Audit(stale);
                    AssertFalse(await db.Driver.Missions.TryWriteAutomaticWorkPermissionAsync(stale, grants, rejectedAudit).ConfigureAwait(false));
                    AssertNull(await db.Driver.Events.ReadAsync(rejectedAudit.Id).ConfigureAwait(false));
                    AssertFalse(await db.Driver.Missions.TryUpdateIfStatusAsync(stale, stale.Status).ConfigureAwait(false));
                    bool refused = false;
                    try { await db.Driver.Missions.UpdateAsync(stale).ConfigureAwait(false); }
                    catch (InvalidOperationException ex) { refused = ex.Message.StartsWith("automatic_work_permission_stale_mission_write", StringComparison.Ordinal); }
                    AssertTrue(refused);
                    Mission after = (await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    AssertNull(after.AutomaticWorkPermission!.Verification);
                    AssertEqual(JsonSerializer.Serialize(grants.DependentAdmission), JsonSerializer.Serialize(after.AutomaticWorkPermission.DependentAdmission));
                }
            }).ConfigureAwait(false);

            await RunTest("A concurrent launch source or owner change rejects the permission and its audit", async () =>
            {
                foreach (string changed in new[] { "launch", "source", "owner" })
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Mission stale = await CreateMissionAsync(db).ConfigureAwait(false);
                        Mission current = (await db.Driver.Missions.ReadAsync(stale.Id).ConfigureAwait(false))!;
                        if (changed == "launch") current.StartedUtc = current.StartedUtc!.Value.AddSeconds(1);
                        if (changed == "source") current.CommitHash = new string('b', 40);
                        if (changed == "owner") current.UserId = null;
                        await db.Driver.Missions.UpdateAsync(current).ConfigureAwait(false);
                        ArmadaEvent rejectedAudit = Audit(stale);
                        AssertFalse(await db.Driver.Missions.TryWriteAutomaticWorkPermissionAsync(stale,
                            new MissionAutomaticWorkPermission { Verification = Grant(stale) }, rejectedAudit).ConfigureAwait(false));
                        AssertNull(await db.Driver.Events.ReadAsync(rejectedAudit.Id).ConfigureAwait(false));
                        AssertNull((await db.Driver.Missions.ReadAsync(stale.Id).ConfigureAwait(false))!.AutomaticWorkPermission);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("An audit insert failure rolls back the permission revision and grant", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(db).ConfigureAwait(false);
                    ArmadaEvent existing = Audit(mission);
                    AssertTrue(await db.Driver.Missions.TryWriteAutomaticWorkPermissionAsync(mission,
                        new MissionAutomaticWorkPermission(), existing).ConfigureAwait(false));
                    Mission current = (await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    string raw = current.AutomaticWorkPermissionRawJson!;
                    long revision = current.AutomaticWorkPermissionWriteRevision;
                    ArmadaEvent duplicate = Audit(current);
                    duplicate.Id = existing.Id;
                    bool failed = false;
                    try
                    {
                        await db.Driver.Missions.TryWriteAutomaticWorkPermissionAsync(current,
                            new MissionAutomaticWorkPermission { Verification = Grant(current) }, duplicate).ConfigureAwait(false);
                    }
                    catch (DbException) { failed = true; }
                    AssertTrue(failed, "An existing event ID must fail the audit insert.");
                    Mission after = (await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    AssertEqual(raw, after.AutomaticWorkPermissionRawJson);
                    AssertEqual(revision, after.AutomaticWorkPermissionWriteRevision);
                    AssertNull(after.AutomaticWorkPermission!.Verification);
                }
            }).ConfigureAwait(false);

            await RunTest("Raw-present invalid JSON never becomes legacy on detail summary or save", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(db).ConfigureAwait(false);
                    foreach (string raw in new[] { "null", "{}", "{", "{\"Version\":2}", "{\"Version\":1,\"Version\":1}", "{\"Version\":1,\"unexpected\":true}" })
                    {
                        using (SqliteConnection connection = new SqliteConnection(db.ConnectionString))
                        {
                            await connection.OpenAsync().ConfigureAwait(false);
                            using (SqliteCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "UPDATE missions SET automatic_work_permission_json=@raw, automatic_work_permission_revision=automatic_work_permission_revision+1 WHERE id=@id;";
                                command.Parameters.AddWithValue("@raw", raw);
                                command.Parameters.AddWithValue("@id", mission.Id);
                                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }
                        }
                        Mission detail = (await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                        Mission summary = (await db.Driver.Missions.ReadSummaryAsync(mission.Id).ConfigureAwait(false))!;
                        MissionSummary light = await ReadLightSummaryAsync(db, mission.Id).ConfigureAwait(false);
                        AssertTrue(detail.AutomaticWorkPermissionInvalid);
                        AssertTrue(summary.AutomaticWorkPermissionInvalid);
                        AssertTrue(light.AutomaticWorkPermissionInvalid);
                        detail.AutomaticWorkPermission = new MissionAutomaticWorkPermission();
                        await db.Driver.Missions.UpdateAsync(detail).ConfigureAwait(false);
                        Mission after = (await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                        AssertTrue(after.AutomaticWorkPermissionInvalid);
                        AssertEqual(raw, after.AutomaticWorkPermissionRawJson);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Audit scope mismatch is refused before either row changes", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = await CreateMissionAsync(db).ConfigureAwait(false);
                    ArmadaEvent audit = Audit(mission);
                    audit.MissionId = "another-mission";
                    bool refused = false;
                    try { await db.Driver.Missions.TryWriteAutomaticWorkPermissionAsync(mission, new MissionAutomaticWorkPermission(), audit).ConfigureAwait(false); }
                    catch (ArgumentException) { refused = true; }
                    AssertTrue(refused);
                    AssertNull((await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!.AutomaticWorkPermission);
                    AssertNull(await db.Driver.Events.ReadAsync(audit.Id).ConfigureAwait(false));
                }
            }).ConfigureAwait(false);
        }

        private static async Task<MissionSummary> ReadLightSummaryAsync(TestDatabase db, string missionId)
        {
            EnumerationResult<MissionSummary> result = await db.Driver.Missions.EnumerateMissionSummariesAsync(
                new EnumerationQuery { PageSize = 10 }).ConfigureAwait(false);
            return result.Objects.Single(mission => mission.Id == missionId);
        }

        private static async Task<Mission> CreateMissionAsync(TestDatabase db)
        {
            Mission created = await db.Driver.Missions.CreateAsync(new Mission("Automatic work", "Keep verification and admission separate")
            {
                TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                Status = MissionStatusEnum.WorkProduced, StartedUtc = DateTime.UtcNow.AddMinutes(-5),
                CommitHash = new string('a', 40), RecoveryAttempts = 2
            }).ConfigureAwait(false);
            return (await db.Driver.Missions.ReadAsync(created.Id).ConfigureAwait(false))!;
        }

        private static MissionAutomaticWorkGrant Grant(Mission mission)
        {
            return new MissionAutomaticWorkGrant
            {
                MissionId = mission.Id, TenantId = mission.TenantId, UserId = mission.UserId,
                StartedUtc = mission.StartedUtc!.Value, CommitHash = mission.CommitHash!,
                GrantedByUserId = Constants.DefaultUserId, GrantedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddMinutes(10)
            };
        }

        private static ArmadaEvent Audit(Mission mission)
        {
            return new ArmadaEvent("mission.automatic_work_permission_changed", "Operator changed the selected permission")
            {
                TenantId = mission.TenantId, UserId = mission.UserId, EntityType = "mission", EntityId = mission.Id,
                MissionId = mission.Id, VesselId = mission.VesselId, VoyageId = mission.VoyageId
            };
        }
    }
}
