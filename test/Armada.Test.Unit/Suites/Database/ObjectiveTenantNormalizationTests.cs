namespace Armada.Test.Unit.Suites.Database
{
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Database.Mysql;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using Microsoft.Data.Sqlite;
    using MysqlTableQueries = Armada.Core.Database.Mysql.Queries.TableQueries;
    using SqliteTableQueries = Armada.Core.Database.Sqlite.Queries.TableQueries;

    /// <summary>
    /// Verifies objective tenant and user identifiers are normalized on load, save, and migration.
    /// </summary>
    public class ObjectiveTenantNormalizationTests : TestSuite
    {
        /// <summary>
        /// Test suite name.
        /// </summary>
        public override string Name => "Objective Tenant Normalization";

        /// <summary>
        /// Run objective tenancy normalization tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("LoadNormalizesGenuineNullObjectiveTenancy", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string id = "obj_null_load";
                    await InsertRawObjectiveAsync(db.ConnectionString, id, null, null).ConfigureAwait(false);

                    Objective? loaded = await db.Driver.Objectives.ReadAsync(id).ConfigureAwait(false);

                    AssertNotNull(loaded, "Objective should load");
                    AssertEqual(Constants.DefaultTenantId, loaded!.TenantId, "Null tenant_id should normalize on load");
                    AssertEqual(Constants.DefaultUserId, loaded.UserId, "Null user_id should normalize on load");
                }
            });

            await RunTest("SaveNeverWritesNullObjectiveTenancy", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Objective objective = new Objective
                    {
                        Title = "T",
                        TenantId = null,
                        UserId = null
                    };

                    await db.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    AssertEqual(Constants.DefaultTenantId, await ReadScalarStringAsync(db.ConnectionString, objective.Id, "tenant_id").ConfigureAwait(false), "Create should write default tenant_id");
                    AssertEqual(Constants.DefaultUserId, await ReadScalarStringAsync(db.ConnectionString, objective.Id, "user_id").ConfigureAwait(false), "Create should write default user_id");
                }
            });

            await RunTest("UpdateNeverWritesNullObjectiveTenancy", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Objective objective = await db.Driver.Objectives.CreateAsync(new Objective { Title = "T" }).ConfigureAwait(false);
                    objective.TenantId = null;
                    objective.UserId = null;

                    await db.Driver.Objectives.UpdateAsync(objective).ConfigureAwait(false);

                    AssertEqual(Constants.DefaultTenantId, await ReadScalarStringAsync(db.ConnectionString, objective.Id, "tenant_id").ConfigureAwait(false), "Update should write default tenant_id");
                    AssertEqual(Constants.DefaultUserId, await ReadScalarStringAsync(db.ConnectionString, objective.Id, "user_id").ConfigureAwait(false), "Update should write default user_id");
                }
            });

            await RunTest("NormalizeTenancy_DefaultsNullAndWhitespaceButPreservesCustom", () =>
            {
                Objective missing = new Objective
                {
                    Title = "T",
                    TenantId = null,
                    UserId = null
                };
                missing.NormalizeTenancy();
                AssertEqual(Constants.DefaultTenantId, missing.TenantId, "Null tenant should normalize");
                AssertEqual(Constants.DefaultUserId, missing.UserId, "Null user should normalize");

                Objective whitespace = new Objective
                {
                    Title = "T",
                    TenantId = "  ",
                    UserId = "\t"
                };
                whitespace.NormalizeTenancy();
                AssertEqual(Constants.DefaultTenantId, whitespace.TenantId, "Whitespace tenant should normalize");
                AssertEqual(Constants.DefaultUserId, whitespace.UserId, "Whitespace user should normalize");

                Objective custom = new Objective
                {
                    Title = "T",
                    TenantId = "tnt_x",
                    UserId = "usr_x"
                };
                custom.NormalizeTenancy();
                AssertEqual("tnt_x", custom.TenantId, "Custom tenant should be preserved");
                AssertEqual("usr_x", custom.UserId, "Custom user should be preserved");
            });

            await RunTest("MigrationProviderDefinitions_ContainVersion52", () =>
            {
                List<SchemaMigration> sqlite = SqliteTableQueries.GetMigrations();
                SchemaMigration? sqlite52 = sqlite.Find(m => m.Version == 52);
                AssertNotNull(sqlite52, "SQLite migrations must include v52");
                AssertContains("objectives", sqlite52!.Statements[0]);
                AssertContains("tenant_id IS NULL", sqlite52.Statements[0]);

                AssertEqual(2, MysqlTableQueries.MigrationV52Statements.Length, "MySQL v52 should include tenant and user normalization statements");
                AssertContains("objectives", MysqlTableQueries.MigrationV52Statements[0]);
                AssertContains("tenant_id IS NULL", MysqlTableQueries.MigrationV52Statements[0]);

                System.Reflection.MethodInfo? mysqlGetMigrations = typeof(MysqlDatabaseDriver).GetMethod("GetMigrations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                AssertNotNull(mysqlGetMigrations, "MySQL driver must expose its migration registration list internally");
                object? mysqlMigrationsObject = mysqlGetMigrations!.Invoke(null, Array.Empty<object>());
                AssertNotNull(mysqlMigrationsObject, "MySQL driver migration list should not be null");
                List<SchemaMigration> mysql = (List<SchemaMigration>)mysqlMigrationsObject!;
                SchemaMigration? mysql52 = mysql.Find(m => m.Version == 52);
                AssertNotNull(mysql52, "MySQL driver migrations must register v52");
                AssertContains("tenant_id IS NULL", mysql52!.Statements[0]);
            });

            await RunTest("ExistingDefaultAndCustomTenancyArePreserved", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string defaultId = "obj_default_preserve";
                    string customId = "obj_custom_preserve";
                    await InsertRawObjectiveAsync(db.ConnectionString, defaultId, Constants.DefaultTenantId, Constants.DefaultUserId).ConfigureAwait(false);
                    await InsertRawObjectiveAsync(db.ConnectionString, customId, "tnt_x", "usr_x").ConfigureAwait(false);

                    Objective? defaultObjective = await db.Driver.Objectives.ReadAsync(defaultId).ConfigureAwait(false);
                    Objective? customObjective = await db.Driver.Objectives.ReadAsync(customId).ConfigureAwait(false);
                    AssertNotNull(defaultObjective, "Default objective should load");
                    AssertNotNull(customObjective, "Custom objective should load");
                    AssertEqual(Constants.DefaultTenantId, defaultObjective!.TenantId, "Default tenant should be preserved on load");
                    AssertEqual(Constants.DefaultUserId, defaultObjective.UserId, "Default user should be preserved on load");
                    AssertEqual("tnt_x", customObjective!.TenantId, "Custom tenant should be preserved on load");
                    AssertEqual("usr_x", customObjective.UserId, "Custom user should be preserved on load");

                    await db.Driver.Objectives.UpdateAsync(customObjective).ConfigureAwait(false);
                    AssertEqual("tnt_x", await ReadScalarStringAsync(db.ConnectionString, customId, "tenant_id").ConfigureAwait(false), "Custom tenant should be preserved on save");
                    AssertEqual("usr_x", await ReadScalarStringAsync(db.ConnectionString, customId, "user_id").ConfigureAwait(false), "Custom user should be preserved on save");
                }
            });

            await RunTest("LoadNormalizesWhitespaceObjectiveTenancy", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string id = "obj_ws_load";
                    await InsertRawObjectiveAsync(db.ConnectionString, id, "   ", "\t").ConfigureAwait(false);

                    Objective? loaded = await db.Driver.Objectives.ReadAsync(id).ConfigureAwait(false);

                    AssertNotNull(loaded, "Objective should load");
                    AssertEqual(Constants.DefaultTenantId, loaded!.TenantId, "Whitespace tenant_id should normalize on load");
                    AssertEqual(Constants.DefaultUserId, loaded.UserId, "Whitespace user_id should normalize on load");
                }
            });

            await RunTest("NullObjectiveRow_RehydrateThenSave_PersistsDefaultNotNull", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string id = "obj_null_roundtrip";
                    await InsertRawObjectiveAsync(db.ConnectionString, id, null, null).ConfigureAwait(false);

                    Objective? loaded = await db.Driver.Objectives.ReadAsync(id).ConfigureAwait(false);
                    AssertNotNull(loaded, "Stale NULL-tenant row should load");
                    await db.Driver.Objectives.UpdateAsync(loaded!).ConfigureAwait(false);

                    AssertEqual(Constants.DefaultTenantId, await ReadScalarStringAsync(db.ConnectionString, id, "tenant_id").ConfigureAwait(false), "Rehydrated NULL row must persist default tenant_id, never NULL");
                    AssertEqual(Constants.DefaultUserId, await ReadScalarStringAsync(db.ConnectionString, id, "user_id").ConfigureAwait(false), "Rehydrated NULL row must persist default user_id, never NULL");
                }
            });

            await RunTest("DeserializeSnapshot_NullTenancy_NormalizesToDefault", () =>
            {
                Objective? objective = InvokeDeserializeObjective("{\"Id\":\"obj_snap_null\",\"Title\":\"T\",\"TenantId\":null,\"UserId\":null}");

                AssertNotNull(objective, "Snapshot with explicit null tenancy should deserialize");
                AssertEqual(Constants.DefaultTenantId, objective!.TenantId, "Null tenant_id in snapshot must normalize on rehydrate");
                AssertEqual(Constants.DefaultUserId, objective.UserId, "Null user_id in snapshot must normalize on rehydrate");
            });

            await RunTest("DeserializeSnapshot_OmittedTenancy_NormalizesToDefault", () =>
            {
                Objective? objective = InvokeDeserializeObjective("{\"Id\":\"obj_snap_omit\",\"Title\":\"T\"}");

                AssertNotNull(objective, "Snapshot omitting tenancy fields should deserialize");
                AssertEqual(Constants.DefaultTenantId, objective!.TenantId, "Omitted tenant_id in snapshot must normalize on rehydrate");
                AssertEqual(Constants.DefaultUserId, objective.UserId, "Omitted user_id in snapshot must normalize on rehydrate");
            });

            await RunTest("DeserializeSnapshot_CustomTenancy_Preserved", () =>
            {
                Objective? objective = InvokeDeserializeObjective("{\"Id\":\"obj_snap_custom\",\"Title\":\"T\",\"TenantId\":\"tnt_real\",\"UserId\":\"usr_real\"}");

                AssertNotNull(objective, "Snapshot with custom tenancy should deserialize");
                AssertEqual("tnt_real", objective!.TenantId, "Custom tenant_id in snapshot must be preserved on rehydrate");
                AssertEqual("usr_real", objective.UserId, "Custom user_id in snapshot must be preserved on rehydrate");
            });
        }

        private static Objective? InvokeDeserializeObjective(string payload)
        {
            System.Reflection.MethodInfo? method = typeof(Armada.Core.Services.ObjectiveService).GetMethod(
                "DeserializeObjective",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (method == null)
            {
                throw new InvalidOperationException("ObjectiveService.DeserializeObjective(ArmadaEvent) was not found via reflection");
            }

            ArmadaEvent snapshot = new ArmadaEvent
            {
                EventType = "objective.snapshot",
                Payload = payload
            };
            return (Objective?)method.Invoke(null, new object[] { snapshot });
        }

        private static async Task InsertRawObjectiveAsync(string connectionString, string id, string? tenantId, string? userId)
        {
            using (SqliteConnection connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"INSERT INTO objectives
                        (id, tenant_id, user_id, title, status, kind, priority, backlog_state, effort, created_utc, last_update_utc)
                        VALUES
                        (@id, @tenant_id, @user_id, @title, @status, @kind, @priority, @backlog_state, @effort, @created_utc, @last_update_utc);";
                    command.Parameters.AddWithValue("@id", id);
                    command.Parameters.AddWithValue("@tenant_id", (object?)tenantId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@user_id", (object?)userId ?? DBNull.Value);
                    command.Parameters.AddWithValue("@title", "T");
                    command.Parameters.AddWithValue("@status", "Draft");
                    command.Parameters.AddWithValue("@kind", "Feature");
                    command.Parameters.AddWithValue("@priority", "P2");
                    command.Parameters.AddWithValue("@backlog_state", "Inbox");
                    command.Parameters.AddWithValue("@effort", "M");
                    command.Parameters.AddWithValue("@created_utc", DateTime.UtcNow.ToString("O"));
                    command.Parameters.AddWithValue("@last_update_utc", DateTime.UtcNow.ToString("O"));
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task<string?> ReadScalarStringAsync(string connectionString, string id, string columnName)
        {
            using (SqliteConnection connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + columnName + " FROM objectives WHERE id = @id;";
                    command.Parameters.AddWithValue("@id", id);
                    object? value = await command.ExecuteScalarAsync().ConfigureAwait(false);
                    if (value == null || value == DBNull.Value)
                    {
                        return null;
                    }

                    return value.ToString();
                }
            }
        }
    }
}
