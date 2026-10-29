namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for durable memory persistence over a live database. Positive cases assert create/read
    /// round-trips including the tag child rows and every typed column (key, summary, salience, version,
    /// provenance), tag replacement on update, tenant-scoped enumeration without cross-tenant leakage,
    /// read-by-key, cascade delete, and the paged enumeration's SQL visibility, filters, sort, and paging; negative cases assert the null-id/null-entity guards and the entity's
    /// value clamping.
    /// </summary>
    public sealed class MemoryDatabaseSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Memory database suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_read_roundtrips_all_fields", "CreateAsync/ReadAsync round-trips a memory with tags and all columns", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Memory memory = NewMemory("ten_mem", "usr_mem");
                memory.Type = MemoryTypeEnum.Semantic;
                memory.Topic = "code-style";
                memory.Key = "code-style/no-var";
                memory.Summary = "No var in C#";
                memory.Content = "This user dislikes the use of var in C# code.";
                memory.Salience = 0.9;
                memory.SourceKind = MemorySourceKindEnum.Voyage;
                memory.SourceVoyageId = "vyg_1";
                memory.SourceMissionId = "msn_1";
                memory.VesselId = "vsl_1";
                memory.SourceDetail = "distilled from the review";
                memory.Tags = new List<string> { "csharp", "style" };

                Memory created = await testDb.Driver.Memories.CreateAsync(memory).ConfigureAwait(false);
                AssertStartsWith("mem_", created.Id);

                Memory? reloaded = await testDb.Driver.Memories.ReadAsync(created.Id).ConfigureAwait(false);
                AssertNotNull(reloaded, "Expected memory to reload.");
                AssertEqual(MemoryTypeEnum.Semantic, reloaded!.Type);
                AssertEqual("code-style", reloaded.Topic);
                AssertEqual("code-style/no-var", reloaded.Key);
                AssertEqual("No var in C#", reloaded.Summary);
                AssertEqual("This user dislikes the use of var in C# code.", reloaded.Content);
                AssertEqual(0.9, reloaded.Salience);
                AssertEqual(1, reloaded.Version);
                AssertEqual(MemorySourceKindEnum.Voyage, reloaded.SourceKind);
                AssertEqual("vyg_1", reloaded.SourceVoyageId);
                AssertEqual("vsl_1", reloaded.VesselId);
                AssertEqual(2, reloaded.Tags.Count);
                AssertTrue(reloaded.Tags.Contains("csharp") && reloaded.Tags.Contains("style"), "Expected tags to round-trip.");
            }));

            cases.Add(CaseAsync("update_replaces_tags_and_fields", "UpdateAsync replaces tags and updates fields", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Memory memory = NewMemory("ten_mem_u", "usr_mem_u");
                memory.Tags = new List<string> { "a", "b" };
                Memory created = await testDb.Driver.Memories.CreateAsync(memory).ConfigureAwait(false);

                created.Content = "updated content";
                created.Salience = 0.2;
                created.Version = 2;
                created.Tags = new List<string> { "c" };
                await testDb.Driver.Memories.UpdateAsync(created).ConfigureAwait(false);

                Memory? reloaded = await testDb.Driver.Memories.ReadAsync(created.Id).ConfigureAwait(false);
                AssertNotNull(reloaded, "Expected memory to reload.");
                AssertEqual("updated content", reloaded!.Content);
                AssertEqual(0.2, reloaded.Salience);
                AssertEqual(2, reloaded.Version);
                AssertEqual(1, reloaded.Tags.Count);
                AssertEqual("c", reloaded.Tags[0]);
            }));

            cases.Add(CaseAsync("read_by_key_finds_within_tenant", "ReadByKeyAsync finds a memory by its key within a tenant", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Memory memory = NewMemory("ten_key", "usr_key");
                memory.Key = "deployment/rollback";
                await testDb.Driver.Memories.CreateAsync(memory).ConfigureAwait(false);

                Memory? found = await testDb.Driver.Memories.ReadByKeyAsync("ten_key", "deployment/rollback").ConfigureAwait(false);
                AssertNotNull(found, "Expected to find the memory by key.");
                AssertEqual("deployment/rollback", found!.Key);

                Memory? missing = await testDb.Driver.Memories.ReadByKeyAsync("ten_key", "nope").ConfigureAwait(false);
                AssertNull(missing, "Expected no memory for an unknown key.");
            }));

            cases.Add(CaseAsync("enumerate_is_tenant_scoped", "EnumerateAsync is tenant-scoped", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                await testDb.Driver.Memories.CreateAsync(NewMemory("ten_x", "usr_x")).ConfigureAwait(false);
                await testDb.Driver.Memories.CreateAsync(NewMemory("ten_x", "usr_x")).ConfigureAwait(false);
                await testDb.Driver.Memories.CreateAsync(NewMemory("ten_y", "usr_y")).ConfigureAwait(false);

                List<Memory> tenantX = await testDb.Driver.Memories.EnumerateAsync("ten_x").ConfigureAwait(false);
                AssertEqual(2, tenantX.Count);
                AssertTrue(tenantX.TrueForAll(m => m.TenantId == "ten_x"), "Expected no cross-tenant leakage.");
            }));

            cases.Add(CaseAsync("paged_enumerate_applies_visibility_in_sql", "Paged EnumerateAsync scopes by tenant, owner, and viewer and counts only matching rows", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Memory shared = await testDb.Driver.Memories.CreateAsync(Seeded("ten_v", "usr_a", ScopeEnum.TenantWide, 1)).ConfigureAwait(false);
                Memory mine = await testDb.Driver.Memories.CreateAsync(Seeded("ten_v", "usr_a", ScopeEnum.UserSpecific, 2)).ConfigureAwait(false);
                Memory theirs = await testDb.Driver.Memories.CreateAsync(Seeded("ten_v", "usr_b", ScopeEnum.UserSpecific, 3)).ConfigureAwait(false);
                Memory foreign = await testDb.Driver.Memories.CreateAsync(Seeded("ten_w", "usr_c", ScopeEnum.TenantWide, 4)).ConfigureAwait(false);
                MemoryEnumerateRequest ascending = new MemoryEnumerateRequest { Order = EnumerationOrderEnum.CreatedAscending };

                EnumerationResult<Memory> all = await testDb.Driver.Memories.EnumerateAsync(ascending, null, null, null).ConfigureAwait(false);
                AssertEqual(Ids(shared, mine, theirs, foreign), Ids(all), "unscoped");
                AssertEqual(4L, all.TotalRecords, "unscoped total");

                EnumerationResult<Memory> tenant = await testDb.Driver.Memories.EnumerateAsync(ascending, "ten_v", null, null).ConfigureAwait(false);
                AssertEqual(Ids(shared, mine, theirs), Ids(tenant), "tenant");

                EnumerationResult<Memory> owner = await testDb.Driver.Memories.EnumerateAsync(ascending, "ten_v", "usr_b", null).ConfigureAwait(false);
                AssertEqual(Ids(theirs), Ids(owner), "owner");

                EnumerationResult<Memory> viewer = await testDb.Driver.Memories.EnumerateAsync(ascending, "ten_v", null, "usr_a").ConfigureAwait(false);
                AssertEqual(Ids(shared, mine), Ids(viewer), "viewer sees tenant-wide plus own");
                AssertEqual(2L, viewer.TotalRecords, "viewer total");

                EnumerationResult<Memory> anonymousViewer = await testDb.Driver.Memories.EnumerateAsync(ascending, "ten_v", null, String.Empty).ConfigureAwait(false);
                AssertEqual(Ids(shared), Ids(anonymousViewer), "an empty viewer id sees only tenant-wide memories");
            }));

            cases.Add(CaseAsync("paged_enumerate_filters_sorts_and_pages", "Paged EnumerateAsync filters, sorts by column and order, pages, and loads tags", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Memory a = Seeded("ten_f", "usr_f", ScopeEnum.TenantWide, 1);
                a.Type = MemoryTypeEnum.Episodic;
                a.Topic = "Deploy";
                a.Salience = 0.2;
                a.SourceVoyageId = "vyg_f";
                a.Tags = new List<string> { "Rollback" };
                Memory b = Seeded("ten_f", "usr_f", ScopeEnum.UserSpecific, 2);
                b.Topic = "billing";
                b.Summary = "Webhook retries";
                b.Salience = 0.9;
                b.VesselId = "vsl_f";
                b.SourceMissionId = "msn_f";
                Memory c = Seeded("ten_f", "usr_f", ScopeEnum.TenantWide, 3);
                c.Topic = "ci_gate";
                c.Content = "Coverage stays at 100%";
                c.Salience = 0.5;
                c.SourceVesselId = "vsl_f";
                a = await testDb.Driver.Memories.CreateAsync(a).ConfigureAwait(false);
                b = await testDb.Driver.Memories.CreateAsync(b).ConfigureAwait(false);
                c = await testDb.Driver.Memories.CreateAsync(c).ConfigureAwait(false);

                AssertEqual(Ids(c, b, a), Ids(await Page(testDb, new MemoryEnumerateRequest()).ConfigureAwait(false)), "created descending by default");
                AssertEqual(Ids(b, c, a), Ids(await Page(testDb, new MemoryEnumerateRequest { SortBy = MemorySortEnum.Salience }).ConfigureAwait(false)), "salience descending");
                AssertEqual(Ids(b, c, a), Ids(await Page(testDb, new MemoryEnumerateRequest { SortBy = MemorySortEnum.Topic, Order = EnumerationOrderEnum.CreatedAscending }).ConfigureAwait(false)), "topic ascending (case-insensitive: billing, ci_gate, Deploy)");

                EnumerationResult<Memory> page2 = await Page(testDb, new MemoryEnumerateRequest { PageNumber = 2, PageSize = 2, Order = EnumerationOrderEnum.CreatedAscending }).ConfigureAwait(false);
                AssertEqual(Ids(c), Ids(page2), "second page");
                AssertEqual(3L, page2.TotalRecords, "total");
                AssertEqual(2, page2.TotalPages, "total pages");

                AssertEqual(Ids(c, b), Ids(await Page(testDb, new MemoryEnumerateRequest { VesselId = "vsl_f" }).ConfigureAwait(false)), "vessel or source vessel");
                AssertEqual(Ids(a), Ids(await Page(testDb, new MemoryEnumerateRequest { VoyageId = "vyg_f" }).ConfigureAwait(false)), "voyage");
                AssertEqual(Ids(b), Ids(await Page(testDb, new MemoryEnumerateRequest { MissionId = "msn_f" }).ConfigureAwait(false)), "mission");
                AssertEqual(Ids(a), Ids(await Page(testDb, new MemoryEnumerateRequest { Search = "rollback" }).ConfigureAwait(false)), "search matches tags");
                AssertEqual(Ids(c), Ids(await Page(testDb, new MemoryEnumerateRequest { Search = "%" }).ConfigureAwait(false)), "a literal % is not a wildcard");
                AssertEqual(Ids(c), Ids(await Page(testDb, new MemoryEnumerateRequest { TopicContains = "_" }).ConfigureAwait(false)), "a literal _ is not a wildcard");
                AssertEqual(Ids(a), Ids(await Page(testDb, new MemoryEnumerateRequest { Type = MemoryTypeEnum.Episodic }).ConfigureAwait(false)), "type");
                AssertEqual(Ids(a), Ids(await Page(testDb, new MemoryEnumerateRequest { Topic = "DEPLOY" }).ConfigureAwait(false)), "exact topic");
                AssertEqual(Ids(b), Ids(await Page(testDb, new MemoryEnumerateRequest { SummaryContains = "webhook" }).ConfigureAwait(false)), "summary");
                AssertEqual(Ids(b), Ids(await Page(testDb, new MemoryEnumerateRequest { Scope = ScopeEnum.UserSpecific }).ConfigureAwait(false)), "scope");
                AssertEqual(Ids(c, b), Ids(await Page(testDb, new MemoryEnumerateRequest { MinSalience = 0.5 }).ConfigureAwait(false)), "min salience");
                AssertEqual(Ids(b), Ids(await Page(testDb, new MemoryEnumerateRequest { CreatedAfter = a.CreatedUtc, CreatedBefore = c.CreatedUtc }).ConfigureAwait(false)), "exclusive date range");

                EnumerationResult<Memory> tagged = await Page(testDb, new MemoryEnumerateRequest { VoyageId = "vyg_f" }).ConfigureAwait(false);
                AssertEqual("Rollback", String.Join(",", tagged.Objects[0].Tags), "tags load with the page");
            }));

            cases.Add(CaseAsync("delete_cascades_tags", "DeleteAsync removes the memory and its tags", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Memory memory = NewMemory("ten_del", "usr_del");
                memory.Tags = new List<string> { "t1", "t2" };
                Memory created = await testDb.Driver.Memories.CreateAsync(memory).ConfigureAwait(false);

                await testDb.Driver.Memories.DeleteAsync(created.Id).ConfigureAwait(false);

                Memory? reloaded = await testDb.Driver.Memories.ReadAsync(created.Id).ConfigureAwait(false);
                AssertNull(reloaded, "Expected memory to be deleted.");
            }));

            cases.Add(CaseAsync("salience_clamped", "Salience clamps to [0,1]", TestTags.Positive, () =>
            {
                Memory memory = new Memory();
                memory.Salience = 5.0;
                AssertEqual(1.0, memory.Salience);
                memory.Salience = -1.0;
                AssertEqual(0.0, memory.Salience);
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("version_clamped", "Version clamps to a minimum of 1", TestTags.Positive, () =>
            {
                Memory memory = new Memory();
                memory.Version = 0;
                AssertEqual(1, memory.Version);
                memory.Version = 7;
                AssertEqual(7, memory.Version);
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("read_null_id_throws", "ReadAsync NullId Throws", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await AssertThrowsAsync<ArgumentNullException>(() => testDb.Driver.Memories.ReadAsync(null!));
            }));

            cases.Add(CaseAsync("create_null_throws", "CreateAsync NullEntity Throws", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await AssertThrowsAsync<ArgumentNullException>(() => testDb.Driver.Memories.CreateAsync(null!));
            }));

            cases.Add(CaseAsync("id_empty_throws", "Memory Id empty throws", TestTags.Negative, () =>
            {
                Memory memory = new Memory();
                AssertThrows<ArgumentNullException>(() => memory.Id = "");
                return Task.CompletedTask;
            }));

            return new TestSuiteDescriptor(
                suiteId: "Database.Memory",
                displayName: "Memory Database",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static Memory Seeded(string tenantId, string userId, ScopeEnum scope, int minute)
        {
            Memory memory = NewMemory(tenantId, userId);
            memory.Scope = scope;
            memory.CreatedUtc = new DateTime(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc);
            memory.LastUpdateUtc = memory.CreatedUtc;
            return memory;
        }

        private static Task<EnumerationResult<Memory>> Page(TestDatabase testDb, MemoryEnumerateRequest request)
        {
            return testDb.Driver.Memories.EnumerateAsync(request, "ten_f", null, null);
        }

        private static string Ids(EnumerationResult<Memory> result)
        {
            return String.Join(",", result.Objects.ConvertAll(m => m.Id));
        }

        private static string Ids(params Memory[] memories)
        {
            return String.Join(",", Array.ConvertAll(memories, m => m.Id));
        }

        private static Memory NewMemory(string tenantId, string userId)
        {
            return new Memory
            {
                TenantId = tenantId,
                UserId = userId,
                Type = MemoryTypeEnum.Semantic,
                Content = "a durable fact"
            };
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Database.Memory",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
