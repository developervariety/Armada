namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for tenant-scoped paginated enumeration of User, Credential, and Tenant
    /// entities. Positive cases cover page counts and totals, cross-tenant scoping (a request
    /// for one tenant returns only that tenant's rows), CreatedAfter filtering, ascending vs
    /// descending ordering, and full property read-back; negative cases cover beyond-range
    /// pages returning an empty result set. Each case runs against its own fresh SQLite store.
    /// </summary>
    public sealed class TenantScopedEnumerationSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.TenantScopedEnumeration";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Tenant-Scoped Paginated Enumeration suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            // User tenant-scoped paginated enumeration

            cases.Add(CaseAsync("user_enumerate_page1", "User enumerate page 1: Objects.Count==2, TotalRecords==5, TotalPages==3", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTestTenantAsync(db, "T1");
                    string t2 = await CreateTestTenantAsync(db, "T2");

                    for (int i = 0; i < 5; i++)
                    {
                        await db.Users.CreateAsync(new UserMaster(t1, "t1user" + i + "@example.com", "pass"));
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        await db.Users.CreateAsync(new UserMaster(t2, "t2user" + i + "@example.com", "pass"));
                    }

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 1;
                    EnumerationResult<UserMaster> result = await db.Users.EnumerateAsync(t1, query);

                    AssertEqual(2, result.Objects.Count, "Objects.Count");
                    AssertEqual(5L, result.TotalRecords, "TotalRecords");
                    AssertEqual(3, result.TotalPages, "TotalPages");
                    AssertEqual(2, result.PageSize, "PageSize");
                    AssertEqual(1, result.PageNumber, "PageNumber");
                }
            }));

            cases.Add(CaseAsync("user_enumerate_page2", "User enumerate page 2: Objects.Count==2, TotalRecords==5", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTestTenantAsync(db, "T1");
                    string t2 = await CreateTestTenantAsync(db, "T2");

                    for (int i = 0; i < 5; i++)
                    {
                        await db.Users.CreateAsync(new UserMaster(t1, "t1user" + i + "@example.com", "pass"));
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        await db.Users.CreateAsync(new UserMaster(t2, "t2user" + i + "@example.com", "pass"));
                    }

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 2;
                    EnumerationResult<UserMaster> result = await db.Users.EnumerateAsync(t1, query);

                    AssertEqual(2, result.Objects.Count, "Objects.Count");
                    AssertEqual(5L, result.TotalRecords, "TotalRecords");
                    AssertEqual(3, result.TotalPages, "TotalPages");
                    AssertEqual(2, result.PageNumber, "PageNumber");
                }
            }));

            cases.Add(CaseAsync("user_enumerate_page3_remainder", "User enumerate page 3 (remainder): Objects.Count==1", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTestTenantAsync(db, "T1");
                    string t2 = await CreateTestTenantAsync(db, "T2");

                    for (int i = 0; i < 5; i++)
                    {
                        await db.Users.CreateAsync(new UserMaster(t1, "t1user" + i + "@example.com", "pass"));
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        await db.Users.CreateAsync(new UserMaster(t2, "t2user" + i + "@example.com", "pass"));
                    }

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 3;
                    EnumerationResult<UserMaster> result = await db.Users.EnumerateAsync(t1, query);

                    AssertEqual(1, result.Objects.Count, "Objects.Count");
                    AssertEqual(5L, result.TotalRecords, "TotalRecords");
                    AssertEqual(3, result.TotalPages, "TotalPages");
                }
            }));

            cases.Add(CaseAsync("user_enumerate_beyond_range_empty", "User enumerate beyond-range page: Objects.Count==0, TotalRecords==5", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTestTenantAsync(db, "T1");
                    string t2 = await CreateTestTenantAsync(db, "T2");

                    for (int i = 0; i < 5; i++)
                    {
                        await db.Users.CreateAsync(new UserMaster(t1, "t1user" + i + "@example.com", "pass"));
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        await db.Users.CreateAsync(new UserMaster(t2, "t2user" + i + "@example.com", "pass"));
                    }

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 10;
                    EnumerationResult<UserMaster> result = await db.Users.EnumerateAsync(t1, query);

                    AssertEqual(0, result.Objects.Count, "Objects.Count");
                    AssertEqual(5L, result.TotalRecords, "TotalRecords");
                    AssertEqual(3, result.TotalPages, "TotalPages");
                }
            }));

            cases.Add(CaseAsync("user_enumerate_tenant_t2", "User enumerate tenant t2 paginated: Objects.Count==2, TotalRecords==2", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTestTenantAsync(db, "T1");
                    string t2 = await CreateTestTenantAsync(db, "T2");

                    for (int i = 0; i < 5; i++)
                    {
                        await db.Users.CreateAsync(new UserMaster(t1, "t1user" + i + "@example.com", "pass"));
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        await db.Users.CreateAsync(new UserMaster(t2, "t2user" + i + "@example.com", "pass"));
                    }

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 10;
                    query.PageNumber = 1;
                    EnumerationResult<UserMaster> result = await db.Users.EnumerateAsync(t2, query);

                    AssertEqual(2, result.Objects.Count, "Objects.Count");
                    AssertEqual(2L, result.TotalRecords, "TotalRecords");
                    AssertEqual(1, result.TotalPages, "TotalPages");
                }
            }));

            cases.Add(CaseAsync("user_enumerate_created_after_filter", "User enumerate CreatedAfter filter returns subset", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTestTenantAsync(db, "T1");

                    // Create users with time gaps via explicit CreatedUtc
                    DateTime baseTime = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
                    for (int i = 0; i < 5; i++)
                    {
                        UserMaster user = new UserMaster(t1, "timeuser" + i + "@example.com", "pass");
                        user.CreatedUtc = baseTime.AddHours(i);
                        await db.Users.CreateAsync(user);
                    }

                    // Filter: CreatedAfter the 2nd user (index 1), so users 2,3,4 should match
                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 10;
                    query.CreatedAfter = baseTime.AddHours(1);
                    EnumerationResult<UserMaster> result = await db.Users.EnumerateAsync(t1, query);

                    AssertEqual(3, result.Objects.Count, "Objects.Count for CreatedAfter filter");
                    AssertEqual(3L, result.TotalRecords, "TotalRecords for CreatedAfter filter");
                }
            }));

            cases.Add(CaseAsync("user_enumerate_order_asc_vs_desc", "User enumerate CreatedAscending vs default order: first result differs", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTestTenantAsync(db, "T1");

                    DateTime baseTime = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
                    for (int i = 0; i < 3; i++)
                    {
                        UserMaster user = new UserMaster(t1, "orderuser" + i + "@example.com", "pass");
                        user.CreatedUtc = baseTime.AddHours(i);
                        await db.Users.CreateAsync(user);
                    }

                    EnumerationQuery descQuery = new EnumerationQuery();
                    descQuery.PageSize = 10;
                    descQuery.Order = EnumerationOrderEnum.CreatedDescending;
                    EnumerationResult<UserMaster> descResult = await db.Users.EnumerateAsync(t1, descQuery);

                    EnumerationQuery ascQuery = new EnumerationQuery();
                    ascQuery.PageSize = 10;
                    ascQuery.Order = EnumerationOrderEnum.CreatedAscending;
                    EnumerationResult<UserMaster> ascResult = await db.Users.EnumerateAsync(t1, ascQuery);

                    AssertEqual(3, descResult.Objects.Count, "descending count");
                    AssertEqual(3, ascResult.Objects.Count, "ascending count");
                    AssertNotEqual(descResult.Objects[0].Id, ascResult.Objects[0].Id, "first result should differ between asc/desc");
                }
            }));

            cases.Add(CaseAsync("user_enumerate_full_property_validation", "User enumerate full property validation", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTestTenantAsync(db, "T1");

                    UserMaster user = new UserMaster(t1, "fullprop@example.com", "securepass");
                    user.FirstName = "Alice";
                    user.LastName = "Smith";
                    user.IsAdmin = true;
                    user.Active = true;
                    await db.Users.CreateAsync(user);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 10;
                    EnumerationResult<UserMaster> result = await db.Users.EnumerateAsync(t1, query);

                    AssertEqual(1, result.Objects.Count, "Objects.Count");
                    AssertEqual(1L, result.TotalRecords, "TotalRecords");

                    UserMaster fetched = result.Objects[0];
                    AssertEqual(user.Id, fetched.Id, "Id");
                    AssertEqual(t1, fetched.TenantId, "TenantId");
                    AssertEqual("fullprop@example.com", fetched.Email, "Email");
                    AssertEqual("Alice", fetched.FirstName, "FirstName");
                    AssertEqual("Smith", fetched.LastName, "LastName");
                    AssertTrue(fetched.IsAdmin, "IsAdmin");
                    AssertTrue(fetched.Active, "Active");
                    AssertTrue(fetched.CreatedUtc != default(DateTime), "CreatedUtc is not default");
                    AssertTrue(fetched.LastUpdateUtc != default(DateTime), "LastUpdateUtc is not default");
                    AssertNotNull(fetched.PasswordSha256, "PasswordSha256 is not null");
                    AssertTrue(fetched.PasswordSha256.Length > 0, "PasswordSha256 is not empty");
                }
            }));

            // Credential tenant-scoped paginated enumeration

            cases.Add(CaseAsync("credential_enumerate_page1", "Credential enumerate page 1: Objects.Count==2, TotalRecords==4, TotalPages==2", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTestTenantAsync(db, "T1");
                    string u1 = (await CreateTestUserAsync(db, t1)).Id;
                    string t2 = await CreateTestTenantAsync(db, "T2");
                    string u2 = (await CreateTestUserAsync(db, t2)).Id;

                    for (int i = 0; i < 4; i++)
                    {
                        Credential cred = new Credential(t1, u1);
                        cred.Name = "T1-Cred-" + i;
                        await db.Credentials.CreateAsync(cred);
                    }
                    Credential t2Cred = new Credential(t2, u2);
                    t2Cred.Name = "T2-Cred-0";
                    await db.Credentials.CreateAsync(t2Cred);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 1;
                    EnumerationResult<Credential> result = await db.Credentials.EnumerateAsync(t1, query);

                    AssertEqual(2, result.Objects.Count, "Objects.Count");
                    AssertEqual(4L, result.TotalRecords, "TotalRecords");
                    AssertEqual(2, result.TotalPages, "TotalPages");
                    AssertEqual(2, result.PageSize, "PageSize");
                    AssertEqual(1, result.PageNumber, "PageNumber");
                }
            }));

            cases.Add(CaseAsync("credential_enumerate_page2", "Credential enumerate page 2: Objects.Count==2, TotalRecords==4", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTestTenantAsync(db, "T1");
                    string u1 = (await CreateTestUserAsync(db, t1)).Id;
                    string t2 = await CreateTestTenantAsync(db, "T2");
                    string u2 = (await CreateTestUserAsync(db, t2)).Id;

                    for (int i = 0; i < 4; i++)
                    {
                        Credential cred = new Credential(t1, u1);
                        cred.Name = "T1-Cred-" + i;
                        await db.Credentials.CreateAsync(cred);
                    }
                    Credential t2Cred = new Credential(t2, u2);
                    t2Cred.Name = "T2-Cred-0";
                    await db.Credentials.CreateAsync(t2Cred);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 2;
                    EnumerationResult<Credential> result = await db.Credentials.EnumerateAsync(t1, query);

                    AssertEqual(2, result.Objects.Count, "Objects.Count");
                    AssertEqual(4L, result.TotalRecords, "TotalRecords");
                    AssertEqual(2, result.TotalPages, "TotalPages");
                    AssertEqual(2, result.PageNumber, "PageNumber");
                }
            }));

            cases.Add(CaseAsync("credential_enumerate_beyond_range_empty", "Credential enumerate beyond-range page: Objects.Count==0", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTestTenantAsync(db, "T1");
                    string u1 = (await CreateTestUserAsync(db, t1)).Id;
                    string t2 = await CreateTestTenantAsync(db, "T2");
                    string u2 = (await CreateTestUserAsync(db, t2)).Id;

                    for (int i = 0; i < 4; i++)
                    {
                        Credential cred = new Credential(t1, u1);
                        cred.Name = "T1-Cred-" + i;
                        await db.Credentials.CreateAsync(cred);
                    }
                    Credential t2Cred = new Credential(t2, u2);
                    t2Cred.Name = "T2-Cred-0";
                    await db.Credentials.CreateAsync(t2Cred);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 10;
                    EnumerationResult<Credential> result = await db.Credentials.EnumerateAsync(t1, query);

                    AssertEqual(0, result.Objects.Count, "Objects.Count");
                    AssertEqual(4L, result.TotalRecords, "TotalRecords");
                    AssertEqual(2, result.TotalPages, "TotalPages");
                }
            }));

            cases.Add(CaseAsync("credential_enumerate_tenant_t2", "Credential enumerate tenant t2: TotalRecords==1", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTestTenantAsync(db, "T1");
                    string u1 = (await CreateTestUserAsync(db, t1)).Id;
                    string t2 = await CreateTestTenantAsync(db, "T2");
                    string u2 = (await CreateTestUserAsync(db, t2)).Id;

                    for (int i = 0; i < 4; i++)
                    {
                        Credential cred = new Credential(t1, u1);
                        cred.Name = "T1-Cred-" + i;
                        await db.Credentials.CreateAsync(cred);
                    }
                    Credential t2Cred = new Credential(t2, u2);
                    t2Cred.Name = "T2-Cred-0";
                    await db.Credentials.CreateAsync(t2Cred);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 10;
                    query.PageNumber = 1;
                    EnumerationResult<Credential> result = await db.Credentials.EnumerateAsync(t2, query);

                    AssertEqual(1, result.Objects.Count, "Objects.Count");
                    AssertEqual(1L, result.TotalRecords, "TotalRecords");
                    AssertEqual(1, result.TotalPages, "TotalPages");
                }
            }));

            cases.Add(CaseAsync("credential_enumerate_full_property_validation", "Credential enumerate full property validation", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    string t1 = await CreateTestTenantAsync(db, "T1");
                    string u1 = (await CreateTestUserAsync(db, t1)).Id;

                    Credential cred = new Credential(t1, u1);
                    cred.Name = "My API Key";
                    await db.Credentials.CreateAsync(cred);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 10;
                    EnumerationResult<Credential> result = await db.Credentials.EnumerateAsync(t1, query);

                    AssertEqual(1, result.Objects.Count, "Objects.Count");
                    AssertEqual(1L, result.TotalRecords, "TotalRecords");

                    Credential fetched = result.Objects[0];
                    AssertEqual(cred.Id, fetched.Id, "Id");
                    AssertEqual(t1, fetched.TenantId, "TenantId");
                    AssertEqual(u1, fetched.UserId, "UserId");
                    AssertEqual("My API Key", fetched.Name, "Name");
                    AssertEqual(64, fetched.BearerToken.Length, "BearerToken length");
                    AssertTrue(fetched.Active, "Active");
                    AssertTrue(fetched.CreatedUtc != default(DateTime), "CreatedUtc is not default");
                    AssertTrue(fetched.LastUpdateUtc != default(DateTime), "LastUpdateUtc is not default");
                }
            }));

            // Tenant paginated enumeration (stronger assertions)

            cases.Add(CaseAsync("tenant_enumerate_paginated", "Tenant enumerate paginated: 5 created + 1 seeded default", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;

                    for (int i = 0; i < 5; i++)
                    {
                        await db.Tenants.CreateAsync(new TenantMetadata("Tenant-" + i));
                    }

                    // Total should be 5 created + 1 default from seeding = 6
                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 2;
                    query.PageNumber = 1;
                    EnumerationResult<TenantMetadata> result = await db.Tenants.EnumerateAsync(query);

                    AssertEqual(2, result.Objects.Count, "Page 1 Objects.Count");
                    AssertEqual(6L, result.TotalRecords, "TotalRecords (5 created + 1 default)");
                    AssertEqual(3, result.TotalPages, "TotalPages ceil(6/2)");
                    AssertEqual(2, result.PageSize, "PageSize");
                    AssertEqual(1, result.PageNumber, "PageNumber");
                }
            }));

            cases.Add(CaseAsync("tenant_enumerate_read_back_property_validation", "Tenant enumerate read-back property validation", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;

                    TenantMetadata tenant = new TenantMetadata("PropCheck Tenant");
                    await db.Tenants.CreateAsync(tenant);

                    EnumerationQuery query = new EnumerationQuery();
                    query.PageSize = 100;
                    EnumerationResult<TenantMetadata> result = await db.Tenants.EnumerateAsync(query);

                    // Find our tenant in the results (there is also the default seeded tenant)
                    TenantMetadata? fetched = null;
                    foreach (TenantMetadata t in result.Objects)
                    {
                        if (t.Id == tenant.Id)
                        {
                            fetched = t;
                            break;
                        }
                    }

                    AssertNotNull(fetched, "Tenant found in enumeration results");
                    AssertEqual("PropCheck Tenant", fetched!.Name, "Name");
                    AssertTrue(fetched.Active, "Active");
                    AssertTrue(fetched.CreatedUtc != default(DateTime), "CreatedUtc is not default");
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Tenant-Scoped Paginated Enumeration",
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

        private static async Task<UserMaster> CreateTestUserAsync(DatabaseDriver db, string tenantId)
        {
            UserMaster user = new UserMaster(tenantId, "user_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@example.com", "password");
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
