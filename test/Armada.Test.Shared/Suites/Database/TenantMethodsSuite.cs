namespace Armada.Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for the tenant database methods: create, read-by-id, read-by-name, update,
    /// delete, existence checks (single and any), and enumeration (list and paginated query).
    /// Each case runs against its own fresh SQLite store. Negative cases cover lookups of
    /// nonexistent identifiers and names returning null and existence returning false.
    /// </summary>
    public sealed class TenantMethodsSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.TenantMethods";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Tenant Methods suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_async_returns_tenant", "CreateAsync returns tenant", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantMetadata tenant = new TenantMetadata("Test Tenant");
                    TenantMetadata result = await db.Tenants.CreateAsync(tenant);

                    AssertNotNull(result);
                    AssertEqual("Test Tenant", result.Name);
                    AssertTrue(result.Active);
                }
            }));

            cases.Add(CaseAsync("read_async_returns_created_tenant", "ReadAsync returns created tenant", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantMetadata tenant = new TenantMetadata("Read Test");
                    await db.Tenants.CreateAsync(tenant);

                    TenantMetadata? result = await db.Tenants.ReadAsync(tenant.Id);
                    AssertNotNull(result);
                    AssertEqual(tenant.Id, result!.Id);
                    AssertEqual("Read Test", result.Name);
                }
            }));

            cases.Add(CaseAsync("read_async_nonexistent_returns_null", "ReadAsync nonexistent returns null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantMetadata? result = await db.Tenants.ReadAsync("ten_nonexistent");
                    AssertNull(result);
                }
            }));

            cases.Add(CaseAsync("read_by_name_async_returns_correct_tenant", "ReadByNameAsync returns correct tenant", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantMetadata tenant = new TenantMetadata("Unique Name Lookup");
                    await db.Tenants.CreateAsync(tenant);

                    TenantMetadata? result = await db.Tenants.ReadByNameAsync("Unique Name Lookup");
                    AssertNotNull(result);
                    AssertEqual(tenant.Id, result!.Id);
                }
            }));

            cases.Add(CaseAsync("read_by_name_async_nonexistent_returns_null", "ReadByNameAsync nonexistent returns null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantMetadata? result = await db.Tenants.ReadByNameAsync("Does Not Exist");
                    AssertNull(result);
                }
            }));

            cases.Add(CaseAsync("update_async_modifies_tenant", "UpdateAsync modifies tenant", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantMetadata tenant = new TenantMetadata("Before Update");
                    await db.Tenants.CreateAsync(tenant);

                    tenant.Name = "After Update";
                    tenant.Active = false;
                    await db.Tenants.UpdateAsync(tenant);

                    TenantMetadata? result = await db.Tenants.ReadAsync(tenant.Id);
                    AssertEqual("After Update", result!.Name);
                    AssertFalse(result.Active);
                }
            }));

            cases.Add(CaseAsync("delete_async_removes_tenant", "DeleteAsync removes tenant", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantMetadata tenant = new TenantMetadata("To Delete");
                    await db.Tenants.CreateAsync(tenant);

                    await db.Tenants.DeleteAsync(tenant.Id);
                    AssertNull(await db.Tenants.ReadAsync(tenant.Id));
                }
            }));

            cases.Add(CaseAsync("exists_async_true_for_existing_tenant", "ExistsAsync true for existing tenant", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantMetadata tenant = new TenantMetadata("Exists Test");
                    await db.Tenants.CreateAsync(tenant);

                    AssertTrue(await db.Tenants.ExistsAsync(tenant.Id));
                }
            }));

            cases.Add(CaseAsync("exists_async_false_for_nonexistent_tenant", "ExistsAsync false for nonexistent tenant", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AssertFalse(await db.Tenants.ExistsAsync("ten_nonexistent"));
                }
            }));

            cases.Add(CaseAsync("exists_any_async_true_after_seeding", "ExistsAnyAsync true after seeding", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    // InitializeAsync seeds a default tenant
                    AssertTrue(await db.Tenants.ExistsAnyAsync());
                }
            }));

            cases.Add(CaseAsync("enumerate_async_returns_created_tenants", "EnumerateAsync returns created tenants", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    TenantMetadata t1 = new TenantMetadata("Alpha Corp");
                    TenantMetadata t2 = new TenantMetadata("Beta Corp");
                    await db.Tenants.CreateAsync(t1);
                    await db.Tenants.CreateAsync(t2);

                    List<TenantMetadata> results = await db.Tenants.EnumerateAsync();
                    // Should include t1, t2, and the seeded default tenant
                    AssertTrue(results.Count >= 2, "Should have at least 2 tenants");
                }
            }));

            cases.Add(CaseAsync("enumerate_async_with_query_supports_pagination", "EnumerateAsync with query supports pagination", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    for (int i = 0; i < 5; i++)
                    {
                        await db.Tenants.CreateAsync(new TenantMetadata("Paginated " + i));
                    }

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    EnumerationResult<TenantMetadata> result = await db.Tenants.EnumerateAsync(query);

                    AssertNotNull(result);
                    AssertTrue(result.Objects.Count <= 2, "Should respect page size");
                    AssertTrue(result.TotalRecords >= 5, "Total count should reflect all tenants");
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Tenant Database Methods",
                cases: cases);
        }

        #endregion

        #region Private-Methods

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
