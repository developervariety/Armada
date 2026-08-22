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
    /// Descriptors verifying tenant isolation (fencing) across user and credential access paths:
    /// tenant-scoped reads, existence checks, email lookups, enumerations, and deletes must never
    /// cross tenant boundaries. Negative cases assert that data owned by one tenant is invisible or
    /// untouched from another. The one positive case confirms the global bearer-token lookup used for
    /// authentication is intentionally not tenant-fenced. Each case runs against its own fresh store.
    /// </summary>
    public sealed class TenantFencingSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.TenantFencing";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Tenant Fencing suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("user_in_tenant_a_not_visible_via_read_async_in_tenant_b", "User in TenantA not visible via ReadAsync in TenantB", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    List<string> tenants = await CreateTwoTenantsAsync(db);
                    string tenantA = tenants[0];
                    string tenantB = tenants[1];

                    UserMaster user = new UserMaster(tenantA, "alice@a.com", "pass");
                    await db.Users.CreateAsync(user);

                    // Should find it in TenantA
                    AssertNotNull(await db.Users.ReadAsync(tenantA, user.Id));

                    // Should NOT find it in TenantB
                    AssertNull(await db.Users.ReadAsync(tenantB, user.Id));
                }
            }));

            cases.Add(CaseAsync("user_in_tenant_a_not_visible_via_exists_async_in_tenant_b", "User in TenantA not visible via ExistsAsync in TenantB", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    List<string> tenants = await CreateTwoTenantsAsync(db);
                    string tenantA = tenants[0];
                    string tenantB = tenants[1];

                    UserMaster user = new UserMaster(tenantA, "bob@a.com", "pass");
                    await db.Users.CreateAsync(user);

                    AssertTrue(await db.Users.ExistsAsync(tenantA, user.Id));
                    AssertFalse(await db.Users.ExistsAsync(tenantB, user.Id));
                }
            }));

            cases.Add(CaseAsync("user_in_tenant_a_not_visible_via_read_by_email_async_in_tenant_b", "User in TenantA not visible via ReadByEmailAsync in TenantB", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    List<string> tenants = await CreateTwoTenantsAsync(db);
                    string tenantA = tenants[0];
                    string tenantB = tenants[1];

                    UserMaster user = new UserMaster(tenantA, "carol@a.com", "pass");
                    await db.Users.CreateAsync(user);

                    AssertNotNull(await db.Users.ReadByEmailAsync(tenantA, "carol@a.com"));
                    AssertNull(await db.Users.ReadByEmailAsync(tenantB, "carol@a.com"));
                }
            }));

            cases.Add(CaseAsync("enumerate_async_only_returns_users_in_requested_tenant", "EnumerateAsync only returns users in requested tenant", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    List<string> tenants = await CreateTwoTenantsAsync(db);
                    string tenantA = tenants[0];
                    string tenantB = tenants[1];

                    await db.Users.CreateAsync(new UserMaster(tenantA, "a1@a.com", "pass"));
                    await db.Users.CreateAsync(new UserMaster(tenantA, "a2@a.com", "pass"));
                    await db.Users.CreateAsync(new UserMaster(tenantB, "b1@b.com", "pass"));

                    List<UserMaster> aUsers = await db.Users.EnumerateAsync(tenantA);
                    AssertEqual(2, aUsers.Count);
                    foreach (UserMaster u in aUsers)
                    {
                        AssertEqual(tenantA, u.TenantId, "All enumerated users should belong to TenantA");
                    }

                    List<UserMaster> bUsers = await db.Users.EnumerateAsync(tenantB);
                    AssertEqual(1, bUsers.Count);
                    AssertEqual(tenantB, bUsers[0].TenantId);
                }
            }));

            cases.Add(CaseAsync("delete_async_in_tenant_b_does_not_affect_tenant_a_user", "DeleteAsync in TenantB does not affect TenantA user", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    List<string> tenants = await CreateTwoTenantsAsync(db);
                    string tenantA = tenants[0];
                    string tenantB = tenants[1];

                    UserMaster user = new UserMaster(tenantA, "dan@a.com", "pass");
                    await db.Users.CreateAsync(user);

                    // Try to delete from wrong tenant
                    await db.Users.DeleteAsync(tenantB, user.Id);

                    // User should still exist in TenantA
                    AssertNotNull(await db.Users.ReadAsync(tenantA, user.Id));
                }
            }));

            cases.Add(CaseAsync("credential_in_tenant_a_not_visible_via_read_async_in_tenant_b", "Credential in TenantA not visible via ReadAsync in TenantB", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    List<string> tenants = await CreateTwoTenantsAsync(db);
                    string tenantA = tenants[0];
                    string tenantB = tenants[1];

                    UserMaster userA = new UserMaster(tenantA, "cred-user@a.com", "pass");
                    await db.Users.CreateAsync(userA);

                    Credential cred = new Credential(tenantA, userA.Id);
                    await db.Credentials.CreateAsync(cred);

                    AssertNotNull(await db.Credentials.ReadAsync(tenantA, cred.Id));
                    AssertNull(await db.Credentials.ReadAsync(tenantB, cred.Id));
                }
            }));

            cases.Add(CaseAsync("credential_enumerate_async_only_returns_tenant_scoped_results", "Credential EnumerateAsync only returns tenant-scoped results", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    List<string> tenants = await CreateTwoTenantsAsync(db);
                    string tenantA = tenants[0];
                    string tenantB = tenants[1];

                    UserMaster userA = new UserMaster(tenantA, "enum-user@a.com", "pass");
                    await db.Users.CreateAsync(userA);
                    UserMaster userB = new UserMaster(tenantB, "enum-user@b.com", "pass");
                    await db.Users.CreateAsync(userB);

                    await db.Credentials.CreateAsync(new Credential(tenantA, userA.Id));
                    await db.Credentials.CreateAsync(new Credential(tenantA, userA.Id));
                    await db.Credentials.CreateAsync(new Credential(tenantB, userB.Id));

                    List<Credential> aCreds = await db.Credentials.EnumerateAsync(tenantA);
                    AssertEqual(2, aCreds.Count);
                    foreach (Credential c in aCreds)
                    {
                        AssertEqual(tenantA, c.TenantId, "All credentials should belong to TenantA");
                    }

                    List<Credential> bCreds = await db.Credentials.EnumerateAsync(tenantB);
                    AssertEqual(1, bCreds.Count);
                }
            }));

            cases.Add(CaseAsync("credential_delete_async_in_tenant_b_does_not_affect_tenant_a_credential", "Credential DeleteAsync in TenantB does not affect TenantA credential", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    List<string> tenants = await CreateTwoTenantsAsync(db);
                    string tenantA = tenants[0];
                    string tenantB = tenants[1];

                    UserMaster userA = new UserMaster(tenantA, "del-cred@a.com", "pass");
                    await db.Users.CreateAsync(userA);

                    Credential cred = new Credential(tenantA, userA.Id);
                    await db.Credentials.CreateAsync(cred);

                    // Try to delete from wrong tenant
                    await db.Credentials.DeleteAsync(tenantB, cred.Id);

                    // Should still exist
                    AssertNotNull(await db.Credentials.ReadAsync(tenantA, cred.Id));
                }
            }));

            cases.Add(CaseAsync("read_by_bearer_token_async_is_global_not_tenant_fenced", "ReadByBearerTokenAsync is global (not tenant-fenced)", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    List<string> tenants = await CreateTwoTenantsAsync(db);
                    string tenantA = tenants[0];

                    UserMaster userA = new UserMaster(tenantA, "bearer@a.com", "pass");
                    await db.Users.CreateAsync(userA);

                    Credential cred = new Credential(tenantA, userA.Id);
                    await db.Credentials.CreateAsync(cred);

                    // ReadByBearerTokenAsync is global lookup (for auth)
                    Credential? result = await db.Credentials.ReadByBearerTokenAsync(cred.BearerToken);
                    AssertNotNull(result);
                    AssertEqual(tenantA, result!.TenantId);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Tenant Fencing (Isolation)",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<List<string>> CreateTwoTenantsAsync(DatabaseDriver db)
        {
            TenantMetadata tA = new TenantMetadata("Tenant A " + Guid.NewGuid().ToString("N").Substring(0, 6));
            TenantMetadata tB = new TenantMetadata("Tenant B " + Guid.NewGuid().ToString("N").Substring(0, 6));
            await db.Tenants.CreateAsync(tA);
            await db.Tenants.CreateAsync(tB);
            return new List<string> { tA.Id, tB.Id };
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
