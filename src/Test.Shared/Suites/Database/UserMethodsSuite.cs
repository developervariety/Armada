namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for the user database methods: create, tenant-scoped read, tenant-less
    /// read-by-id, read-by-email (tenant-scoped and cross-tenant), update, delete, tenant-scoped
    /// enumeration, and existence checks. Each case runs against its own fresh SQLite store.
    /// Negative cases cover cross-tenant reads and lookups of nonexistent identifiers returning null.
    /// </summary>
    public sealed class UserMethodsSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.UserMethods";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the User Methods suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_async_returns_user", "CreateAsync returns user", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string tenantId = await CreateTestTenantAsync(db);

                    UserMaster user = new UserMaster(tenantId, "alice@example.com", "password123");
                    UserMaster result = await db.Users.CreateAsync(user);

                    AssertNotNull(result);
                    AssertEqual("alice@example.com", result.Email);
                    AssertEqual(tenantId, result.TenantId);
                }
            }));

            cases.Add(CaseAsync("read_async_returns_user_by_tenant_and_id", "ReadAsync returns user by tenant and id", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string tenantId = await CreateTestTenantAsync(db);

                    UserMaster user = new UserMaster(tenantId, "bob@example.com", "pass");
                    await db.Users.CreateAsync(user);

                    UserMaster? result = await db.Users.ReadAsync(tenantId, user.Id);
                    AssertNotNull(result);
                    AssertEqual(user.Id, result!.Id);
                    AssertEqual("bob@example.com", result.Email);
                }
            }));

            cases.Add(CaseAsync("read_async_wrong_tenant_returns_null", "ReadAsync wrong tenant returns null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string tenantId = await CreateTestTenantAsync(db);
                    string otherTenantId = await CreateTestTenantAsync(db, "Other Tenant");

                    UserMaster user = new UserMaster(tenantId, "carol@example.com", "pass");
                    await db.Users.CreateAsync(user);

                    // Try to read with wrong tenant
                    UserMaster? result = await db.Users.ReadAsync(otherTenantId, user.Id);
                    AssertNull(result);
                }
            }));

            cases.Add(CaseAsync("read_by_id_async_returns_user_without_tenant_filter", "ReadByIdAsync returns user without tenant filter", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string tenantId = await CreateTestTenantAsync(db);

                    UserMaster user = new UserMaster(tenantId, "dan@example.com", "pass");
                    await db.Users.CreateAsync(user);

                    UserMaster? result = await db.Users.ReadByIdAsync(user.Id);
                    AssertNotNull(result);
                    AssertEqual(user.Id, result!.Id);
                }
            }));

            cases.Add(CaseAsync("read_by_email_async_returns_user_within_tenant", "ReadByEmailAsync returns user within tenant", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string tenantId = await CreateTestTenantAsync(db);

                    UserMaster user = new UserMaster(tenantId, "eve@example.com", "pass");
                    await db.Users.CreateAsync(user);

                    UserMaster? result = await db.Users.ReadByEmailAsync(tenantId, "eve@example.com");
                    AssertNotNull(result);
                    AssertEqual(user.Id, result!.Id);
                }
            }));

            cases.Add(CaseAsync("read_by_email_async_wrong_tenant_returns_null", "ReadByEmailAsync wrong tenant returns null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string tenantId = await CreateTestTenantAsync(db);
                    string otherTenantId = await CreateTestTenantAsync(db, "Other Tenant");

                    UserMaster user = new UserMaster(tenantId, "frank@example.com", "pass");
                    await db.Users.CreateAsync(user);

                    UserMaster? result = await db.Users.ReadByEmailAsync(otherTenantId, "frank@example.com");
                    AssertNull(result);
                }
            }));

            cases.Add(CaseAsync("read_by_email_any_tenant_async_returns_users_across_tenants", "ReadByEmailAnyTenantAsync returns users across tenants", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string tenant1 = await CreateTestTenantAsync(db, "Tenant 1");
                    string tenant2 = await CreateTestTenantAsync(db, "Tenant 2");

                    string sharedEmail = "shared@example.com";
                    await db.Users.CreateAsync(new UserMaster(tenant1, sharedEmail, "pass1"));
                    await db.Users.CreateAsync(new UserMaster(tenant2, sharedEmail, "pass2"));

                    List<UserMaster> results = await db.Users.ReadByEmailAnyTenantAsync(sharedEmail);
                    AssertEqual(2, results.Count);
                }
            }));

            cases.Add(CaseAsync("update_async_modifies_user", "UpdateAsync modifies user", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string tenantId = await CreateTestTenantAsync(db);

                    UserMaster user = new UserMaster(tenantId, "gina@example.com", "pass");
                    await db.Users.CreateAsync(user);

                    user.FirstName = "Gina";
                    user.LastName = "Smith";
                    user.IsAdmin = true;
                    await db.Users.UpdateAsync(user);

                    UserMaster? result = await db.Users.ReadAsync(tenantId, user.Id);
                    AssertEqual("Gina", result!.FirstName);
                    AssertEqual("Smith", result.LastName);
                    AssertTrue(result.IsAdmin);
                }
            }));

            cases.Add(CaseAsync("delete_async_removes_user", "DeleteAsync removes user", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string tenantId = await CreateTestTenantAsync(db);

                    UserMaster user = new UserMaster(tenantId, "henry@example.com", "pass");
                    await db.Users.CreateAsync(user);

                    await db.Users.DeleteAsync(tenantId, user.Id);
                    AssertNull(await db.Users.ReadAsync(tenantId, user.Id));
                }
            }));

            cases.Add(CaseAsync("enumerate_async_returns_tenant_scoped_users", "EnumerateAsync returns tenant-scoped users", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string tenant1 = await CreateTestTenantAsync(db, "Enum Tenant 1");
                    string tenant2 = await CreateTestTenantAsync(db, "Enum Tenant 2");

                    await db.Users.CreateAsync(new UserMaster(tenant1, "a@t1.com", "pass"));
                    await db.Users.CreateAsync(new UserMaster(tenant1, "b@t1.com", "pass"));
                    await db.Users.CreateAsync(new UserMaster(tenant2, "c@t2.com", "pass"));

                    List<UserMaster> t1Users = await db.Users.EnumerateAsync(tenant1);
                    AssertEqual(2, t1Users.Count);

                    List<UserMaster> t2Users = await db.Users.EnumerateAsync(tenant2);
                    AssertEqual(1, t2Users.Count);
                }
            }));

            cases.Add(CaseAsync("exists_async_correct_for_tenant_scoped_lookup", "ExistsAsync correct for tenant-scoped lookup", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string tenantId = await CreateTestTenantAsync(db);
                    string otherTenantId = await CreateTestTenantAsync(db, "Other");

                    UserMaster user = new UserMaster(tenantId, "exists@example.com", "pass");
                    await db.Users.CreateAsync(user);

                    AssertTrue(await db.Users.ExistsAsync(tenantId, user.Id));
                    AssertFalse(await db.Users.ExistsAsync(otherTenantId, user.Id));
                    AssertFalse(await db.Users.ExistsAsync(tenantId, "usr_nonexistent"));
                }
            }));

            cases.Add(CaseAsync("read_async_nonexistent_returns_null_audit", "ReadAsync nonexistent returns null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string tenantId = await CreateTestTenantAsync(db);

                    UserMaster? result = await db.Users.ReadAsync(tenantId, "usr_nonexistent");
                    AssertNull(result);
                }
            }));

            cases.Add(CaseAsync("read_by_id_async_nonexistent_returns_null_audit", "ReadByIdAsync nonexistent returns null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    UserMaster? result = await db.Users.ReadByIdAsync("usr_nonexistent");
                    AssertNull(result);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "User Database Methods",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<string> CreateTestTenantAsync(DatabaseDriver db, string name = "Test Tenant")
        {
            TenantMetadata tenant = new TenantMetadata(name);
            await db.Tenants.CreateAsync(tenant);
            return tenant.Id;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
