namespace Armada.Test.Unit.Suites.Database
{
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using MysqlTableQueries = Armada.Core.Database.Mysql.Queries.TableQueries;
    using PostgresqlTableQueries = Armada.Core.Database.Postgresql.Queries.TableQueries;
    using SqliteTableQueries = Armada.Core.Database.Sqlite.Queries.TableQueries;
    using SqlServerTableQueries = Armada.Core.Database.SqlServer.Queries.TableQueries;

    /// <summary>
    /// Verifies the objective AutoDispatchEnabled opt-in flag persists across create/update and is wired into every backend migration.
    /// </summary>
    public class ObjectiveAutoDispatchTests : TestSuite
    {
        /// <summary>
        /// Test suite name.
        /// </summary>
        public override string Name => "Objective AutoDispatch";

        /// <summary>
        /// Run objective auto-dispatch persistence tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            // DB round-trip coverage uses the SQLite TestDatabaseHelper; the other three backends are covered by migration-definition assertions below.
            await RunTest("DefaultConstruct_AutoDispatchEnabledIsFalse", () =>
            {
                Objective objective = new Objective { Title = "T" };
                AssertEqual(false, objective.AutoDispatchEnabled, "AutoDispatchEnabled should default to false");
                return Task.CompletedTask;
            });

            await RunTest("CreateWithAutoDispatchTrue_RoundTripsTrue", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Objective created = await db.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "Opted in",
                        AutoDispatchEnabled = true
                    }).ConfigureAwait(false);

                    Objective? loaded = await db.Driver.Objectives.ReadAsync(created.Id).ConfigureAwait(false);

                    AssertNotNull(loaded, "Objective should load");
                    AssertEqual(true, loaded!.AutoDispatchEnabled, "AutoDispatchEnabled=true should round-trip from the database");
                }
            });

            await RunTest("CreateDefault_RoundTripsFalse", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Objective created = await db.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "Not opted in"
                    }).ConfigureAwait(false);

                    Objective? loaded = await db.Driver.Objectives.ReadAsync(created.Id).ConfigureAwait(false);

                    AssertNotNull(loaded, "Objective should load");
                    AssertEqual(false, loaded!.AutoDispatchEnabled, "Default AutoDispatchEnabled should round-trip as false");
                }
            });

            await RunTest("UpdateFlipsTrueToFalse_Persists", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Objective created = await db.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "Flip me",
                        AutoDispatchEnabled = true
                    }).ConfigureAwait(false);

                    created.AutoDispatchEnabled = false;
                    await db.Driver.Objectives.UpdateAsync(created).ConfigureAwait(false);

                    Objective? loaded = await db.Driver.Objectives.ReadAsync(created.Id).ConfigureAwait(false);

                    AssertNotNull(loaded, "Objective should load");
                    AssertEqual(false, loaded!.AutoDispatchEnabled, "Update flipping true->false should persist false");
                }
            });

            await RunTest("UpdateFlipsFalseToTrue_Persists", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Objective created = await db.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "Opt me in later"
                    }).ConfigureAwait(false);

                    AssertEqual(false, created.AutoDispatchEnabled, "Newly created objective should start opted out");

                    created.AutoDispatchEnabled = true;
                    await db.Driver.Objectives.UpdateAsync(created).ConfigureAwait(false);

                    Objective? loaded = await db.Driver.Objectives.ReadAsync(created.Id).ConfigureAwait(false);

                    AssertNotNull(loaded, "Objective should load");
                    AssertEqual(true, loaded!.AutoDispatchEnabled, "Update flipping false->true should persist true");
                }
            });

            // Service-layer no-clobber coverage: ObjectiveService.UpdateAsync only assigns
            // AutoDispatchEnabled when request.AutoDispatchEnabled.HasValue. A request that omits
            // the flag must leave a previously-set value untouched (the negative branch of the guard).
            await RunTest("Service_CreateWithoutFlag_DefaultsFalse", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string tenantId = "ten_autodispatch_default";
                    string userId = "usr_autodispatch_default";
                    await EnsureTenantAndUserAsync(db, tenantId, userId).ConfigureAwait(false);

                    ObjectiveService objectives = new ObjectiveService(db.Driver);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");

                    Objective created = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "No flag supplied"
                    }).ConfigureAwait(false);

                    AssertEqual(false, created.AutoDispatchEnabled, "Create with a null request flag should default to false");
                }
            });

            await RunTest("Service_CreateWithFlagTrue_PersistsTrue", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string tenantId = "ten_autodispatch_optin";
                    string userId = "usr_autodispatch_optin";
                    await EnsureTenantAndUserAsync(db, tenantId, userId).ConfigureAwait(false);

                    ObjectiveService objectives = new ObjectiveService(db.Driver);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");

                    Objective created = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Flag supplied true",
                        AutoDispatchEnabled = true
                    }).ConfigureAwait(false);

                    AssertEqual(true, created.AutoDispatchEnabled, "Create with request flag true should persist true");

                    Objective? loaded = await objectives.ReadAsync(auth, created.Id).ConfigureAwait(false);
                    AssertNotNull(loaded, "Objective should load via the service");
                    AssertEqual(true, loaded!.AutoDispatchEnabled, "Service read should round-trip the opted-in flag");
                }
            });

            await RunTest("Service_UpdateWithNullFlag_DoesNotClobberExistingTrue", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string tenantId = "ten_autodispatch_noclobber";
                    string userId = "usr_autodispatch_noclobber";
                    await EnsureTenantAndUserAsync(db, tenantId, userId).ConfigureAwait(false);

                    ObjectiveService objectives = new ObjectiveService(db.Driver);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");

                    Objective created = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Stay opted in",
                        AutoDispatchEnabled = true
                    }).ConfigureAwait(false);

                    // Update an unrelated field WITHOUT supplying AutoDispatchEnabled (null).
                    Objective updated = await objectives.UpdateAsync(auth, created.Id, new ObjectiveUpsertRequest
                    {
                        Owner = "new-owner"
                    }).ConfigureAwait(false);

                    AssertEqual(true, updated.AutoDispatchEnabled, "Omitting the flag on update must not clobber an existing true value");

                    Objective? loaded = await objectives.ReadAsync(auth, created.Id).ConfigureAwait(false);
                    AssertNotNull(loaded, "Objective should load via the service");
                    AssertEqual(true, loaded!.AutoDispatchEnabled, "No-clobber semantics must persist the prior true value");
                }
            });

            await RunTest("Service_UpdateWithFlagTrue_SetsTrueFromFalse", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string tenantId = "ten_autodispatch_settrue";
                    string userId = "usr_autodispatch_settrue";
                    await EnsureTenantAndUserAsync(db, tenantId, userId).ConfigureAwait(false);

                    ObjectiveService objectives = new ObjectiveService(db.Driver);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");

                    Objective created = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Opt in via update"
                    }).ConfigureAwait(false);

                    AssertEqual(false, created.AutoDispatchEnabled, "Should begin opted out");

                    Objective updated = await objectives.UpdateAsync(auth, created.Id, new ObjectiveUpsertRequest
                    {
                        AutoDispatchEnabled = true
                    }).ConfigureAwait(false);

                    AssertEqual(true, updated.AutoDispatchEnabled, "Supplying flag true on update should opt in");
                }
            });

            await RunTest("Service_UpdateWithFlagFalse_SetsFalseFromTrue", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string tenantId = "ten_autodispatch_setfalse";
                    string userId = "usr_autodispatch_setfalse";
                    await EnsureTenantAndUserAsync(db, tenantId, userId).ConfigureAwait(false);

                    ObjectiveService objectives = new ObjectiveService(db.Driver);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");

                    Objective created = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Opt out via update",
                        AutoDispatchEnabled = true
                    }).ConfigureAwait(false);

                    AssertEqual(true, created.AutoDispatchEnabled, "Should begin opted in");

                    // Explicit false is distinct from null/omitted: HasValue is true so it must be applied.
                    Objective updated = await objectives.UpdateAsync(auth, created.Id, new ObjectiveUpsertRequest
                    {
                        AutoDispatchEnabled = false
                    }).ConfigureAwait(false);

                    AssertEqual(false, updated.AutoDispatchEnabled, "Supplying explicit flag false on update should opt out");

                    Objective? loaded = await objectives.ReadAsync(auth, created.Id).ConfigureAwait(false);
                    AssertNotNull(loaded, "Objective should load via the service");
                    AssertEqual(false, loaded!.AutoDispatchEnabled, "Explicit false should persist through the service");
                }
            });

            await RunTest("MigrationDefinitions_ContainVersion53AcrossAllBackends", () =>
            {
                SchemaMigration? sqlite53 = SqliteTableQueries.GetMigrations().Find(m => m.Version == 53);
                AssertNotNull(sqlite53, "SQLite migrations must include v53");
                AssertContains("auto_dispatch_enabled", sqlite53!.Statements[0]);

                SchemaMigration? postgres53 = PostgresqlTableQueries.GetMigrations().Find(m => m.Version == 53);
                AssertNotNull(postgres53, "PostgreSQL migrations must include v53");
                AssertContains("auto_dispatch_enabled", postgres53!.Statements[0]);

                SchemaMigration? sqlServer53 = SqlServerTableQueries.GetMigrations().Find(m => m.Version == 53);
                AssertNotNull(sqlServer53, "SQL Server migrations must include v53");
                AssertContains("auto_dispatch_enabled", sqlServer53!.Statements[0]);

                AssertEqual(1, MysqlTableQueries.MigrationV53Statements.Length, "MySQL v53 should define exactly one ALTER statement");
                AssertContains("auto_dispatch_enabled", MysqlTableQueries.MigrationV53Statements[0]);
                AssertContains("objectives", MysqlTableQueries.MigrationV53Statements[0]);
                return Task.CompletedTask;
            });
        }

        private static async Task EnsureTenantAndUserAsync(TestDatabase testDb, string tenantId, string userId)
        {
            TenantMetadata? existingTenant = await testDb.Driver.Tenants.ReadAsync(tenantId).ConfigureAwait(false);
            if (existingTenant == null)
            {
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata
                {
                    Id = tenantId,
                    Name = tenantId
                }).ConfigureAwait(false);
            }

            UserMaster? existingUser = await testDb.Driver.Users.ReadByIdAsync(userId).ConfigureAwait(false);
            if (existingUser == null)
            {
                await testDb.Driver.Users.CreateAsync(new UserMaster
                {
                    Id = userId,
                    TenantId = tenantId,
                    Email = userId + "@armada.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                    IsTenantAdmin = true
                }).ConfigureAwait(false);
            }
        }
    }
}
