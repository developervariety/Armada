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
    /// Descriptors for the credential database methods: create, tenant-scoped read, tenant-less
    /// read-by-id, read-by-bearer-token, update, delete, and tenant- and user-scoped enumeration.
    /// Each case runs against its own fresh SQLite store. Negative cases cover cross-tenant reads,
    /// unknown bearer tokens, and lookups of nonexistent identifiers returning null.
    /// </summary>
    public sealed class CredentialMethodsSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.CredentialMethods";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Credential Methods suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_async_returns_credential", "CreateAsync returns credential", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    UserMaster tenantUser = await CreateTestTenantAndUserAsync(db);
                    string tenantId = tenantUser.TenantId;
                    string userId = tenantUser.Id;

                    Credential cred = new Credential(tenantId, userId);
                    cred.Name = "Test Key";
                    Credential result = await db.Credentials.CreateAsync(cred);

                    AssertNotNull(result);
                    AssertEqual(tenantId, result.TenantId);
                    AssertEqual(userId, result.UserId);
                    AssertEqual("Test Key", result.Name);
                }
            }));

            cases.Add(CaseAsync("read_async_returns_credential_by_tenant_and_id", "ReadAsync returns credential by tenant and id", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    UserMaster tenantUser = await CreateTestTenantAndUserAsync(db);
                    string tenantId = tenantUser.TenantId;
                    string userId = tenantUser.Id;

                    Credential cred = new Credential(tenantId, userId);
                    await db.Credentials.CreateAsync(cred);

                    Credential? result = await db.Credentials.ReadAsync(tenantId, cred.Id);
                    AssertNotNull(result);
                    AssertEqual(cred.Id, result!.Id);
                    AssertEqual(cred.BearerToken, result.BearerToken);
                }
            }));

            cases.Add(CaseAsync("read_async_wrong_tenant_returns_null", "ReadAsync wrong tenant returns null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    UserMaster tenantUser = await CreateTestTenantAndUserAsync(db);
                    string tenantId = tenantUser.TenantId;
                    string userId = tenantUser.Id;
                    UserMaster otherTenantUser = await CreateTestTenantAndUserAsync(db, "Other Tenant");
                    string otherTenantId = otherTenantUser.TenantId;

                    Credential cred = new Credential(tenantId, userId);
                    await db.Credentials.CreateAsync(cred);

                    Credential? result = await db.Credentials.ReadAsync(otherTenantId, cred.Id);
                    AssertNull(result);
                }
            }));

            cases.Add(CaseAsync("read_by_id_async_returns_credential_without_tenant_filter", "ReadByIdAsync returns credential without tenant filter", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    UserMaster tenantUser = await CreateTestTenantAndUserAsync(db);
                    string tenantId = tenantUser.TenantId;
                    string userId = tenantUser.Id;

                    Credential cred = new Credential(tenantId, userId);
                    await db.Credentials.CreateAsync(cred);

                    Credential? result = await db.Credentials.ReadByIdAsync(cred.Id);
                    AssertNotNull(result);
                    AssertEqual(cred.Id, result!.Id);
                }
            }));

            cases.Add(CaseAsync("read_by_bearer_token_async_returns_matching_credential", "ReadByBearerTokenAsync returns matching credential", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    UserMaster tenantUser = await CreateTestTenantAndUserAsync(db);
                    string tenantId = tenantUser.TenantId;
                    string userId = tenantUser.Id;

                    Credential cred = new Credential(tenantId, userId);
                    await db.Credentials.CreateAsync(cred);

                    Credential? result = await db.Credentials.ReadByBearerTokenAsync(cred.BearerToken);
                    AssertNotNull(result);
                    AssertEqual(cred.Id, result!.Id);
                    AssertEqual(tenantId, result.TenantId);
                }
            }));

            cases.Add(CaseAsync("read_by_bearer_token_async_nonexistent_returns_null", "ReadByBearerTokenAsync nonexistent returns null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Credential? result = await db.Credentials.ReadByBearerTokenAsync("nonexistent_token_value");
                    AssertNull(result);
                }
            }));

            cases.Add(CaseAsync("update_async_modifies_credential", "UpdateAsync modifies credential", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    UserMaster tenantUser = await CreateTestTenantAndUserAsync(db);
                    string tenantId = tenantUser.TenantId;
                    string userId = tenantUser.Id;

                    Credential cred = new Credential(tenantId, userId);
                    await db.Credentials.CreateAsync(cred);

                    cred.Name = "Updated Name";
                    cred.Active = false;
                    await db.Credentials.UpdateAsync(cred);

                    Credential? result = await db.Credentials.ReadAsync(tenantId, cred.Id);
                    AssertEqual("Updated Name", result!.Name);
                    AssertFalse(result.Active);
                }
            }));

            cases.Add(CaseAsync("delete_async_removes_credential", "DeleteAsync removes credential", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    UserMaster tenantUser = await CreateTestTenantAndUserAsync(db);
                    string tenantId = tenantUser.TenantId;
                    string userId = tenantUser.Id;

                    Credential cred = new Credential(tenantId, userId);
                    await db.Credentials.CreateAsync(cred);

                    await db.Credentials.DeleteAsync(tenantId, cred.Id);
                    AssertNull(await db.Credentials.ReadAsync(tenantId, cred.Id));
                }
            }));

            cases.Add(CaseAsync("enumerate_async_returns_tenant_scoped_credentials", "EnumerateAsync returns tenant-scoped credentials", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    UserMaster tenantUser1 = await CreateTestTenantAndUserAsync(db, "Tenant 1");
                    string tenant1 = tenantUser1.TenantId;
                    string user1 = tenantUser1.Id;
                    UserMaster tenantUser2 = await CreateTestTenantAndUserAsync(db, "Tenant 2");
                    string tenant2 = tenantUser2.TenantId;
                    string user2 = tenantUser2.Id;

                    await db.Credentials.CreateAsync(new Credential(tenant1, user1));
                    await db.Credentials.CreateAsync(new Credential(tenant1, user1));
                    await db.Credentials.CreateAsync(new Credential(tenant2, user2));

                    List<Credential> t1Creds = await db.Credentials.EnumerateAsync(tenant1);
                    AssertEqual(2, t1Creds.Count);

                    List<Credential> t2Creds = await db.Credentials.EnumerateAsync(tenant2);
                    AssertEqual(1, t2Creds.Count);
                }
            }));

            cases.Add(CaseAsync("enumerate_by_user_async_returns_user_scoped_credentials", "EnumerateByUserAsync returns user-scoped credentials", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    UserMaster tenantUser = await CreateTestTenantAndUserAsync(db);
                    string tenantId = tenantUser.TenantId;
                    string userId1 = tenantUser.Id;

                    UserMaster user2 = new UserMaster(tenantId, "user2@example.com", "pass");
                    await db.Users.CreateAsync(user2);

                    await db.Credentials.CreateAsync(new Credential(tenantId, userId1));
                    await db.Credentials.CreateAsync(new Credential(tenantId, userId1));
                    await db.Credentials.CreateAsync(new Credential(tenantId, user2.Id));

                    List<Credential> u1Creds = await db.Credentials.EnumerateByUserAsync(tenantId, userId1);
                    AssertEqual(2, u1Creds.Count);

                    List<Credential> u2Creds = await db.Credentials.EnumerateByUserAsync(tenantId, user2.Id);
                    AssertEqual(1, u2Creds.Count);
                }
            }));

            cases.Add(CaseAsync("read_async_nonexistent_returns_null_audit", "ReadAsync nonexistent returns null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    UserMaster tenantUser = await CreateTestTenantAndUserAsync(db);
                    string tenantId = tenantUser.TenantId;

                    Credential? result = await db.Credentials.ReadAsync(tenantId, "cred_nonexistent");
                    AssertNull(result);
                }
            }));

            cases.Add(CaseAsync("read_by_id_async_nonexistent_returns_null_audit", "ReadByIdAsync nonexistent returns null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Credential? result = await db.Credentials.ReadByIdAsync("cred_nonexistent");
                    AssertNull(result);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Credential Database Methods",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<UserMaster> CreateTestTenantAndUserAsync(
            DatabaseDriver db, string tenantName = "Test Tenant")
        {
            TenantMetadata tenant = new TenantMetadata(tenantName);
            await db.Tenants.CreateAsync(tenant);

            UserMaster user = new UserMaster(tenant.Id, "user_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@example.com", "password");
            await db.Users.CreateAsync(user);

            return user;
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
