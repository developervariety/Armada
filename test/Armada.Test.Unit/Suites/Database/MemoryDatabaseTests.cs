namespace Armada.Test.Unit.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Storage tests for native captain memory: field round-trip with tags, tag replacement, key lookup
    /// inside a tenant, tenant fencing, the version-guarded update, cascade delete, and the storage-level
    /// key uniqueness that stops two writers creating the same key at once.
    /// </summary>
    public class MemoryDatabaseTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Memory Database";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Create and read round-trips every field and its tags", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Memory memory = NewMemory("ten_a", "usr_a");
                    memory.Type = MemoryTypeEnum.Procedural;
                    memory.Scope = MemoryScopeEnum.UserSpecific;
                    memory.Topic = "build";
                    memory.Key = "build/quiet-window";
                    memory.Summary = "Run the suite in a quiet window";
                    memory.Content = "The suite is load sensitive. Re-run a single failure alone before triage.";
                    memory.Salience = 0.9;
                    memory.SourceKind = MemorySourceKindEnum.Voyage;
                    memory.SourceVoyageId = "vyg_example";
                    memory.SourceMissionId = "msn_example";
                    memory.SourceVesselId = "vsl_example";
                    memory.SourceDetail = "distilled from the review stage";
                    memory.VesselId = "vsl_example";
                    memory.Tags = new List<string> { "tests", "load" };

                    Memory created = await testDb.Driver.Memories.CreateAsync(memory).ConfigureAwait(false);
                    AssertStartsWith("mem_", created.Id);

                    Memory? stored = await testDb.Driver.Memories.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertNotNull(stored, "Memory should reload");
                    AssertEqual(MemoryTypeEnum.Procedural, stored!.Type, "Type");
                    AssertEqual(MemoryScopeEnum.UserSpecific, stored.Scope, "Scope");
                    AssertEqual("build", stored.Topic, "Topic");
                    AssertEqual("build/quiet-window", stored.Key, "Key");
                    AssertEqual("Run the suite in a quiet window", stored.Summary, "Summary");
                    AssertEqual(memory.Content, stored.Content, "Content");
                    AssertEqual(0.9, stored.Salience, "Salience");
                    AssertEqual(1, stored.Version, "Version starts at one");
                    AssertEqual(MemorySourceKindEnum.Voyage, stored.SourceKind, "Source kind");
                    AssertEqual("vyg_example", stored.SourceVoyageId, "Source voyage");
                    AssertEqual("msn_example", stored.SourceMissionId, "Source mission");
                    AssertEqual("vsl_example", stored.SourceVesselId, "Source vessel");
                    AssertEqual("distilled from the review stage", stored.SourceDetail, "Source detail");
                    AssertEqual("vsl_example", stored.VesselId, "Vessel association");
                    AssertEqual(2, stored.Tags.Count, "Tag count");
                    AssertEqual("load", stored.Tags[0], "Tags come back in ordinal order");
                    AssertEqual("tests", stored.Tags[1], "Tags come back in ordinal order");
                    AssertEqual(memory.CreatedUtc.ToString("O"), stored.CreatedUtc.ToString("O"), "Creation timestamp");
                }
            });

            await RunTest("Guarded update replaces fields and tags at the expected version", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Memory memory = NewMemory("ten_u", "usr_u");
                    memory.Tags = new List<string> { "one", "two" };
                    Memory created = await testDb.Driver.Memories.CreateAsync(memory).ConfigureAwait(false);

                    created.Content = "corrected content";
                    created.Salience = 0.25;
                    created.Version = 2;
                    created.Tags = new List<string> { "three" };
                    created.LastUpdateUtc = DateTime.UtcNow;
                    bool updated = await testDb.Driver.Memories.UpdateAsync(created, 1).ConfigureAwait(false);
                    AssertTrue(updated, "An update at the stored version applies");

                    Memory? stored = await testDb.Driver.Memories.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertNotNull(stored, "Memory should reload");
                    AssertEqual("corrected content", stored!.Content, "Content");
                    AssertEqual(0.25, stored.Salience, "Salience");
                    AssertEqual(2, stored.Version, "Version");
                    AssertEqual(1, stored.Tags.Count, "Tags are replaced, not merged");
                    AssertEqual("three", stored.Tags[0], "Replacement tag");
                }
            });

            await RunTest("Update at a stale version changes nothing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Memory created = await testDb.Driver.Memories.CreateAsync(NewMemory("ten_s", "usr_s")).ConfigureAwait(false);
                    created.Content = "first writer";
                    created.Version = 2;
                    AssertTrue(await testDb.Driver.Memories.UpdateAsync(created, 1).ConfigureAwait(false), "First writer applies");

                    created.Content = "second writer";
                    created.Version = 2;
                    bool second = await testDb.Driver.Memories.UpdateAsync(created, 1).ConfigureAwait(false);
                    AssertFalse(second, "A second update at the same read version is refused");

                    Memory? stored = await testDb.Driver.Memories.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertEqual("first writer", stored!.Content, "The refused update left the record alone");
                    AssertEqual(2, stored.Version, "Version did not move twice");
                }
            });

            await RunTest("Concurrent guarded updates let exactly one writer win", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Memory created = await testDb.Driver.Memories.CreateAsync(NewMemory("ten_c", "usr_c")).ConfigureAwait(false);

                    Memory first = Clone(created, "writer one");
                    Memory second = Clone(created, "writer two");
                    bool[] results = await Task.WhenAll(
                        testDb.Driver.Memories.UpdateAsync(first, 1),
                        testDb.Driver.Memories.UpdateAsync(second, 1)).ConfigureAwait(false);

                    AssertEqual(1, results.Count(applied => applied), "Exactly one concurrent update applies");
                    Memory? stored = await testDb.Driver.Memories.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertEqual(2, stored!.Version, "The losing writer did not advance the version");
                }
            });

            await RunTest("Key lookup and enumeration are fenced by tenant", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Memory mine = NewMemory("ten_x", "usr_x");
                    mine.Key = "deploy/rollback";
                    await testDb.Driver.Memories.CreateAsync(mine).ConfigureAwait(false);
                    await testDb.Driver.Memories.CreateAsync(NewMemory("ten_x", "usr_x")).ConfigureAwait(false);

                    Memory other = NewMemory("ten_y", "usr_y");
                    other.Key = "deploy/rollback";
                    await testDb.Driver.Memories.CreateAsync(other).ConfigureAwait(false);

                    Memory? found = await testDb.Driver.Memories.ReadByKeyAsync("ten_x", "deploy/rollback").ConfigureAwait(false);
                    AssertNotNull(found, "The key resolves inside its own tenant");
                    AssertEqual(mine.Id, found!.Id, "The key resolves to this tenant's record");
                    AssertNull(await testDb.Driver.Memories.ReadByKeyAsync("ten_x", "absent").ConfigureAwait(false), "An unknown key resolves to nothing");

                    List<Memory> tenantX = await testDb.Driver.Memories.EnumerateAsync("ten_x").ConfigureAwait(false);
                    AssertEqual(2, tenantX.Count, "Enumeration returns only this tenant");
                    AssertTrue(tenantX.TrueForAll(memory => memory.TenantId == "ten_x"), "No record from another tenant leaks");

                    AssertNull(await testDb.Driver.Memories.ReadAsync("ten_y", mine.Id).ConfigureAwait(false), "A record is not readable from another tenant");
                    AssertFalse(await testDb.Driver.Memories.DeleteAsync("ten_y", mine.Id).ConfigureAwait(false), "A record is not deletable from another tenant");
                    AssertNotNull(await testDb.Driver.Memories.ReadAsync(mine.Id).ConfigureAwait(false), "The record survives the foreign delete");

                    Memory stale = await testDb.Driver.Memories.ReadAsync(mine.Id).ConfigureAwait(false) ?? throw new Exception("record missing");
                    stale.TenantId = "ten_y";
                    stale.Version = 2;
                    AssertFalse(await testDb.Driver.Memories.UpdateAsync(stale, 1).ConfigureAwait(false), "A record is not updatable from another tenant");
                }
            });

            await RunTest("A key is unique inside a tenant and free in another", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Memory first = NewMemory("ten_k", "usr_k");
                    first.Key = "style/no-var";
                    await testDb.Driver.Memories.CreateAsync(first).ConfigureAwait(false);

                    Memory duplicate = NewMemory("ten_k", "usr_k");
                    duplicate.Key = "style/no-var";
                    await AssertThrowsAsync<DbException>(() => testDb.Driver.Memories.CreateAsync(duplicate));

                    Memory otherTenant = NewMemory("ten_k2", "usr_k2");
                    otherTenant.Key = "style/no-var";
                    await testDb.Driver.Memories.CreateAsync(otherTenant).ConfigureAwait(false);

                    Memory noKeyOne = NewMemory("ten_k", "usr_k");
                    Memory noKeyTwo = NewMemory("ten_k", "usr_k");
                    await testDb.Driver.Memories.CreateAsync(noKeyOne).ConfigureAwait(false);
                    await testDb.Driver.Memories.CreateAsync(noKeyTwo).ConfigureAwait(false);
                    List<Memory> stored = await testDb.Driver.Memories.EnumerateAsync("ten_k").ConfigureAwait(false);
                    AssertEqual(3, stored.Count, "Records without a key never collide");
                }
            });

            await RunTest("Delete removes the record and its tag rows", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Memory memory = NewMemory("ten_d", "usr_d");
                    memory.Tags = new List<string> { "alpha", "beta" };
                    Memory created = await testDb.Driver.Memories.CreateAsync(memory).ConfigureAwait(false);

                    Memory kept = NewMemory("ten_d", "usr_d");
                    kept.Tags = new List<string> { "alpha" };
                    await testDb.Driver.Memories.CreateAsync(kept).ConfigureAwait(false);
                    AssertEqual(3, await CountTagsAsync(testDb).ConfigureAwait(false), "Both records wrote their tags");

                    AssertTrue(await testDb.Driver.Memories.DeleteAsync("ten_d", created.Id).ConfigureAwait(false), "Delete reports the removal");
                    AssertNull(await testDb.Driver.Memories.ReadAsync(created.Id).ConfigureAwait(false), "The record is gone");
                    AssertEqual(1, await CountTagsAsync(testDb).ConfigureAwait(false), "Only the deleted record's tags are gone");
                    AssertFalse(await testDb.Driver.Memories.DeleteAsync("ten_d", created.Id).ConfigureAwait(false), "A repeated delete reports nothing removed");
                }
            });

            await RunTest("The model clamps salience and version", () =>
            {
                Memory memory = new Memory();
                memory.Salience = 5.0;
                AssertEqual(1.0, memory.Salience, "Salience clamps to one");
                memory.Salience = -1.0;
                AssertEqual(0.0, memory.Salience, "Salience clamps to zero");
                memory.Version = 0;
                AssertEqual(1, memory.Version, "Version never falls below one");
                memory.Content = null!;
                AssertEqual(String.Empty, memory.Content, "Content is never null");
                AssertThrows<ArgumentNullException>(() => memory.Id = "");
            });
        }

        private static Memory Clone(Memory source, string content)
        {
            Memory copy = new Memory();
            copy.Id = source.Id;
            copy.TenantId = source.TenantId;
            copy.UserId = source.UserId;
            copy.Content = content;
            copy.Version = source.Version + 1;
            copy.LastUpdateUtc = DateTime.UtcNow;
            return copy;
        }

        private static Memory NewMemory(string tenantId, string userId)
        {
            Memory memory = new Memory();
            memory.TenantId = tenantId;
            memory.UserId = userId;
            memory.Content = "a durable finding";
            return memory;
        }

        private static async Task<long> CountTagsAsync(TestDatabase testDb)
        {
            using (SqliteConnection conn = new SqliteConnection(testDb.ConnectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM memory_tags;";
                    return Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
                }
            }
        }
    }
}
