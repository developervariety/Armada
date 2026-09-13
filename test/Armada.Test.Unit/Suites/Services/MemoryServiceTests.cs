namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for MemoryService: caller scoping inside and across tenants, key-driven consolidation,
    /// refusal of a write that would overwrite a newer record, recall ordering and filtering, and the
    /// validation that keeps a record a distilled finding instead of a log.
    /// </summary>
    public class MemoryServiceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Memory Service";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("A regular user creates a user-specific record owned by itself", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MemoryService service = new MemoryService(testDb.Driver);
                    Memory created = await service.CreateAsync(User("ten_a", "usr_a"), new Memory { Content = "a finding", Scope = MemoryScopeEnum.TenantWide }).ConfigureAwait(false);

                    AssertEqual(MemoryScopeEnum.UserSpecific, created.Scope, "A regular user cannot create a tenant-wide record");
                    AssertEqual("ten_a", created.TenantId, "Tenant owner");
                    AssertEqual("usr_a", created.UserId, "User owner");
                    AssertEqual(1, created.Version, "Version starts at one");
                }
            });

            await RunTest("A tenant administrator may create a tenant-wide record", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MemoryService service = new MemoryService(testDb.Driver);
                    Memory created = await service.CreateAsync(TenantAdmin("ten_a", "usr_admin"), new Memory { Content = "a shared finding", Scope = MemoryScopeEnum.TenantWide }).ConfigureAwait(false);
                    AssertEqual(MemoryScopeEnum.TenantWide, created.Scope, "Scope");
                }
            });

            await RunTest("Writing the same key updates that record instead of duplicating it", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MemoryService service = new MemoryService(testDb.Driver);
                    AuthContext caller = User("ten_up", "usr_up");

                    Memory first = await service.UpsertAsync(caller, new Memory { Key = "Style/No-Var", Content = "dislikes var" }).ConfigureAwait(false);
                    AssertEqual("style/no-var", first.Key, "A key is stored as a lowercase slug");

                    Memory second = await service.UpsertAsync(caller, new Memory { Key = "style/no-var", Content = "dislikes var and tuples" }).ConfigureAwait(false);
                    AssertEqual(first.Id, second.Id, "The same key writes the same record");
                    AssertEqual(2, second.Version, "The version advanced once");

                    EnumerationResult<Memory> all = await service.EnumerateAsync(caller, new EnumerationQuery()).ConfigureAwait(false);
                    AssertEqual(1, (int)all.TotalRecords, "No duplicate was created");
                    AssertEqual("dislikes var and tuples", all.Objects[0].Content, "The record carries the newer content");
                }
            });

            await RunTest("A keyed write at a stale version is refused", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MemoryService service = new MemoryService(testDb.Driver);
                    AuthContext caller = User("ten_v", "usr_v");
                    Memory created = await service.UpsertAsync(caller, new Memory { Key = "deploy/rollback", Content = "first" }).ConfigureAwait(false);
                    await service.UpsertAsync(caller, new Memory { Key = "deploy/rollback", Content = "second" }, created.Version).ConfigureAwait(false);

                    MemoryConflictException conflict = await AssertConflictAsync(() =>
                        service.UpsertAsync(caller, new Memory { Key = "deploy/rollback", Content = "third" }, created.Version)).ConfigureAwait(false);
                    AssertEqual("version", conflict.Kind, "Conflict kind");

                    Memory? stored = await service.ReadAsync(caller, created.Id).ConfigureAwait(false);
                    AssertEqual("second", stored!.Content, "The refused write changed nothing");
                }
            });

            await RunTest("Two writers of one record: the second is refused, not silently dropped", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MemoryService service = new MemoryService(testDb.Driver);
                    AuthContext caller = User("ten_w", "usr_w");
                    Memory created = await service.CreateAsync(caller, new Memory { Content = "original" }).ConfigureAwait(false);

                    await service.UpdateAsync(caller, created.Id, new MemoryUpdate { Content = "writer one", ExpectedVersion = 1 }).ConfigureAwait(false);
                    await AssertConflictAsync(() => service.UpdateAsync(caller, created.Id, new MemoryUpdate { Content = "writer two", ExpectedVersion = 1 })).ConfigureAwait(false);

                    Memory? stored = await service.ReadAsync(caller, created.Id).ConfigureAwait(false);
                    AssertEqual("writer one", stored!.Content, "The first writer kept the record");
                    AssertEqual(2, stored.Version, "One update, one version step");
                }
            });

            await RunTest("A partial update keeps the fields it does not name", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MemoryService service = new MemoryService(testDb.Driver);
                    AuthContext caller = User("ten_p", "usr_p");
                    Memory created = await service.CreateAsync(caller, new Memory
                    {
                        Content = "original",
                        Topic = "build",
                        Summary = "summary",
                        Tags = new List<string> { "one" },
                        VesselId = "vsl_example"
                    }).ConfigureAwait(false);

                    Memory updated = await service.UpdateAsync(caller, created.Id, new MemoryUpdate { Salience = 0.9 }).ConfigureAwait(false);
                    AssertEqual("original", updated.Content, "Content kept");
                    AssertEqual("build", updated.Topic, "Topic kept");
                    AssertEqual("summary", updated.Summary, "Summary kept");
                    AssertEqual(1, updated.Tags.Count, "Tags kept");
                    AssertEqual("vsl_example", updated.VesselId, "Vessel kept");
                    AssertEqual(0.9, updated.Salience, "Salience changed");

                    Memory cleared = await service.UpdateAsync(caller, created.Id, new MemoryUpdate { Topic = "", Tags = new List<string>() }).ConfigureAwait(false);
                    AssertNull(cleared.Topic, "An empty string clears a field");
                    AssertEqual(0, cleared.Tags.Count, "An empty list clears the tags");
                }
            });

            await RunTest("Recall orders by salience, then by recency, and pages", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MemoryService service = new MemoryService(testDb.Driver);
                    AuthContext caller = User("ten_o", "usr_o");
                    await service.CreateAsync(caller, new Memory { Content = "incidental", Salience = 0.2 }).ConfigureAwait(false);
                    await service.CreateAsync(caller, new Memory { Content = "load-bearing", Salience = 0.9 }).ConfigureAwait(false);
                    await service.CreateAsync(caller, new Memory { Content = "middle", Salience = 0.5 }).ConfigureAwait(false);

                    EnumerationResult<Memory> page = await service.EnumerateAsync(caller, new EnumerationQuery { PageNumber = 1, PageSize = 2 }).ConfigureAwait(false);
                    AssertEqual(3, (int)page.TotalRecords, "Total counts every visible record");
                    AssertEqual(2, page.Objects.Count, "Page size");
                    AssertEqual("load-bearing", page.Objects[0].Content, "Highest salience first");
                    AssertEqual("middle", page.Objects[1].Content, "Then the next salience");

                    EnumerationResult<Memory> second = await service.EnumerateAsync(caller, new EnumerationQuery { PageNumber = 2, PageSize = 2 }).ConfigureAwait(false);
                    AssertEqual(1, second.Objects.Count, "The second page carries the rest");
                    AssertEqual("incidental", second.Objects[0].Content, "Lowest salience last");
                }
            });

            await RunTest("Recall filters by type, topic, vessel and free text", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MemoryService service = new MemoryService(testDb.Driver);
                    AuthContext caller = User("ten_f", "usr_f");
                    await service.CreateAsync(caller, new Memory { Content = "how to run the gate", Type = MemoryTypeEnum.Procedural, Topic = "build", VesselId = "vsl_example", Tags = new List<string> { "gate" } }).ConfigureAwait(false);
                    await service.CreateAsync(caller, new Memory { Content = "the gate failed once", Type = MemoryTypeEnum.Episodic, Topic = "incident", SourceVesselId = "vsl_other", Summary = "an outage" }).ConfigureAwait(false);

                    AssertEqual(1, (int)(await service.EnumerateAsync(caller, new EnumerationQuery(), null, MemoryTypeEnum.Procedural).ConfigureAwait(false)).TotalRecords, "Type filter");
                    AssertEqual(1, (int)(await service.EnumerateAsync(caller, new EnumerationQuery(), null, null, "BUILD").ConfigureAwait(false)).TotalRecords, "Topic filter ignores case");
                    AssertEqual(1, (int)(await service.EnumerateAsync(caller, new EnumerationQuery { VesselId = "vsl_example" }).ConfigureAwait(false)).TotalRecords, "Vessel filter");
                    AssertEqual(1, (int)(await service.EnumerateAsync(caller, new EnumerationQuery { VesselId = "vsl_other" }).ConfigureAwait(false)).TotalRecords, "Vessel filter also matches provenance");
                    AssertEqual(2, (int)(await service.EnumerateAsync(caller, new EnumerationQuery(), "GATE").ConfigureAwait(false)).TotalRecords, "Search covers content and tags, ignoring case");
                    AssertEqual(1, (int)(await service.EnumerateAsync(caller, new EnumerationQuery(), "outage").ConfigureAwait(false)).TotalRecords, "Search covers the summary");
                }
            });

            await RunTest("A user-specific record stays private to its owner", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MemoryService service = new MemoryService(testDb.Driver);
                    AuthContext owner = User("ten_iso", "usr_a");
                    AuthContext peer = User("ten_iso", "usr_b");
                    AuthContext admin = TenantAdmin("ten_iso", "usr_admin");

                    Memory mine = await service.CreateAsync(owner, new Memory { Content = "my private note" }).ConfigureAwait(false);
                    Memory shared = await service.CreateAsync(admin, new Memory { Content = "a tenant fact", Scope = MemoryScopeEnum.TenantWide }).ConfigureAwait(false);

                    AssertEqual(2, (int)(await service.EnumerateAsync(owner, new EnumerationQuery()).ConfigureAwait(false)).TotalRecords, "The owner sees its own record and the shared one");
                    AssertEqual(1, (int)(await service.EnumerateAsync(peer, new EnumerationQuery()).ConfigureAwait(false)).TotalRecords, "A peer sees only the shared record");
                    AssertNull(await service.ReadAsync(peer, mine.Id).ConfigureAwait(false), "A peer cannot read it");
                    await AssertThrowsAsync<UnauthorizedAccessException>(() => service.UpdateAsync(peer, shared.Id, new MemoryUpdate { Content = "rewritten" }));
                    await AssertThrowsAsync<KeyNotFoundException>(() => service.DeleteAsync(peer, mine.Id));
                    AssertEqual(2, (int)(await service.EnumerateAsync(admin, new EnumerationQuery()).ConfigureAwait(false)).TotalRecords, "A tenant administrator sees every record in the tenant");
                }
            });

            await RunTest("No read, write or delete crosses a tenant", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MemoryService service = new MemoryService(testDb.Driver);
                    AuthContext here = TenantAdmin("ten_here", "usr_here");
                    AuthContext there = TenantAdmin("ten_there", "usr_there");

                    Memory mine = await service.CreateAsync(here, new Memory { Content = "our finding", Key = "shared/key", Scope = MemoryScopeEnum.TenantWide }).ConfigureAwait(false);

                    AssertEqual(0, (int)(await service.EnumerateAsync(there, new EnumerationQuery()).ConfigureAwait(false)).TotalRecords, "Another tenant sees nothing");
                    AssertNull(await service.ReadAsync(there, mine.Id).ConfigureAwait(false), "Another tenant cannot read it");
                    await AssertThrowsAsync<KeyNotFoundException>(() => service.UpdateAsync(there, mine.Id, new MemoryUpdate { Content = "rewritten" }));
                    await AssertThrowsAsync<KeyNotFoundException>(() => service.DeleteAsync(there, mine.Id));

                    Memory theirs = await service.CreateAsync(there, new Memory { Content = "their finding", Key = "shared/key", Scope = MemoryScopeEnum.TenantWide }).ConfigureAwait(false);
                    AssertNotEqual(mine.Id, theirs.Id, "The same key is free in another tenant");

                    Memory? stored = await service.ReadAsync(here, mine.Id).ConfigureAwait(false);
                    AssertEqual("our finding", stored!.Content, "The first record is untouched");
                }
            });

            await RunTest("A key held by another user's private record is a conflict, not an overwrite", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MemoryService service = new MemoryService(testDb.Driver);
                    AuthContext owner = User("ten_key", "usr_a");
                    AuthContext peer = User("ten_key", "usr_b");
                    Memory mine = await service.CreateAsync(owner, new Memory { Content = "private", Key = "style/no-var" }).ConfigureAwait(false);

                    MemoryConflictException conflict = await AssertConflictAsync(() =>
                        service.UpsertAsync(peer, new Memory { Content = "mine now", Key = "style/no-var" })).ConfigureAwait(false);
                    AssertEqual("key", conflict.Kind, "Conflict kind");
                    AssertFalse(conflict.Message.Contains("private", StringComparison.Ordinal), "The refusal does not leak the other record's content");

                    Memory? stored = await service.ReadAsync(owner, mine.Id).ConfigureAwait(false);
                    AssertEqual("private", stored!.Content, "The other record is untouched");
                }
            });

            await RunTest("Renaming a key onto an existing key is a conflict", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MemoryService service = new MemoryService(testDb.Driver);
                    AuthContext caller = User("ten_rename", "usr_rename");
                    await service.CreateAsync(caller, new Memory { Content = "first", Key = "one" }).ConfigureAwait(false);
                    Memory second = await service.CreateAsync(caller, new Memory { Content = "second", Key = "two" }).ConfigureAwait(false);

                    MemoryConflictException conflict = await AssertConflictAsync(() =>
                        service.UpdateAsync(caller, second.Id, new MemoryUpdate { Key = "one" })).ConfigureAwait(false);
                    AssertEqual("key", conflict.Kind, "Conflict kind");

                    Memory? stored = await service.ReadAsync(caller, second.Id).ConfigureAwait(false);
                    AssertEqual("two", stored!.Key, "The record kept its key");
                }
            });

            await RunTest("Validation keeps a record a finding, not a log", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MemoryService service = new MemoryService(testDb.Driver);
                    AuthContext caller = User("ten_val", "usr_val");

                    await AssertThrowsAsync<ArgumentException>(() => service.CreateAsync(caller, new Memory { Content = "   " }));
                    await AssertThrowsAsync<ArgumentException>(() => service.CreateAsync(caller, new Memory { Content = "x", Key = "not a slug" }));
                    await AssertThrowsAsync<ArgumentException>(() => service.CreateAsync(caller, new Memory { Content = "x", Tags = new List<string> { "not a slug" } }));
                    await AssertThrowsAsync<ArgumentException>(() => service.CreateAsync(caller, new Memory { Content = new string('x', MemoryService.MaximumContentLength + 1) }));

                    Memory created = await service.CreateAsync(caller, new Memory { Content = "x", Tags = new List<string> { "Build", "build", " release " } }).ConfigureAwait(false);
                    AssertEqual(2, created.Tags.Count, "Tags are lowercased and de-duplicated");
                    AssertTrue(created.Tags.Contains("build") && created.Tags.Contains("release"), "Tags keep their meaning");
                }
            });

            await RunTest("Delete removes the record once", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MemoryService service = new MemoryService(testDb.Driver);
                    AuthContext caller = User("ten_del", "usr_del");
                    Memory created = await service.CreateAsync(caller, new Memory { Content = "stale finding" }).ConfigureAwait(false);

                    await service.DeleteAsync(caller, created.Id).ConfigureAwait(false);
                    AssertNull(await service.ReadAsync(caller, created.Id).ConfigureAwait(false), "The record is gone");
                    await AssertThrowsAsync<KeyNotFoundException>(() => service.DeleteAsync(caller, created.Id));
                }
            });

            await RunTest("An unauthenticated caller reaches no record", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MemoryService service = new MemoryService(testDb.Driver);
                    await AssertThrowsAsync<UnauthorizedAccessException>(() => service.EnumerateAsync(new AuthContext(), new EnumerationQuery()));
                    await AssertThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(new AuthContext(), new Memory { Content = "x" }));
                }
            });
        }

        private async Task<MemoryConflictException> AssertConflictAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (MemoryConflictException conflict)
            {
                return conflict;
            }

            throw new Exception("Expected a memory conflict, but the write was accepted.");
        }

        private static AuthContext User(string tenantId, string userId)
        {
            return AuthContext.Authenticated(tenantId, userId, false, false, "test");
        }

        private static AuthContext TenantAdmin(string tenantId, string userId)
        {
            return AuthContext.Authenticated(tenantId, userId, false, true, "test");
        }
    }
}
