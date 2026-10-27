namespace Armada.Test.Database
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Context;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>Runs the same unified recall contract against each configured database provider.</summary>
    internal sealed class ContextSearchPersistenceTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        internal ContextSearchPersistenceTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver;
            _Settings = settings;
            _NoCleanup = noCleanup;
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? memoryId = null;
            string? tenantId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("context", token: token).ConfigureAwait(false);
                tenantId = tenant.Id;
                UserMaster owner = await fixture.CreateUserAsync(tenant.Id, "context-owner", token: token).ConfigureAwait(false);
                UserMaster other = await fixture.CreateUserAsync(tenant.Id, "context-other", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, owner.Id, "context", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, owner.Id, fleet.Id, "context", token).ConfigureAwait(false);
                AuthContext caller = AuthContext.Authenticated(tenant.Id, owner.Id, false, false, "test");
                MemoryService memories = new MemoryService(_Driver);
                Memory fact = await memories.CreateAsync(caller, new Memory { Key = "context/checksum", Content = "Packet checksum 界", VesselId = vessel.Id }, token).ConfigureAwait(false);
                memoryId = fact.Id;
                ContextRetrievalService rules = new ContextRetrievalService(new[]
                {
                    new ContextChunk { Topic = "core", Path = "AI-Memory/shared/example.md", Tier = ContextTierEnum.Core, Text = "Required guidance." }
                });
                ContextSearchRequest query = new ContextSearchRequest { Query = "checksum", VesselId = vessel.Id, IncludeCode = false };
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                using (DatabaseDriver reopened = DatabaseDriverFactory.Create(_Settings, logging))
                {
                    await reopened.InitializeAsync(token).ConfigureAwait(false);
                    ContextSearchService search = new ContextSearchService(reopened, rules);
                    ContextSearchResponse first = await search.SearchAsync(caller, query, token).ConfigureAwait(false);
                    DatabaseAssert.Equal(1, first.Required.Count, "Required guidance survives database reopen");
                    DatabaseAssert.Equal(fact.Content, first.Results.Single().Text, "Unicode source fidelity");
                    DatabaseAssert.Equal(ContextSearchSourceEnum.Memory, first.Results.Single().Source, "Facts retain their authority class");
                    await memories.UpdateAsync(caller, fact.Id, new MemoryUpdate { Content = "Updated packet checksum 界", ExpectedVersion = 1 }, token).ConfigureAwait(false);
                    ContextSearchResponse updated = await search.SearchAsync(caller, query, token).ConfigureAwait(false);
                    DatabaseAssert.Equal("2", updated.Results.Single().Version, "Live update version");
                    DatabaseAssert.Equal("Updated packet checksum 界", updated.Results.Single().Text, "Live update text");
                    AuthContext stranger = AuthContext.Authenticated(tenant.Id, other.Id, false, false, "test");
                    bool rejected = false;
                    try { await search.SearchAsync(stranger, query, token).ConfigureAwait(false); }
                    catch (System.Collections.Generic.KeyNotFoundException) { rejected = true; }
                    DatabaseAssert.True(rejected, "An unrelated owner cannot query the vessel");
                    await memories.DeleteAsync(caller, fact.Id, token).ConfigureAwait(false);
                    DatabaseAssert.Equal(0, (await search.SearchAsync(caller, query, token).ConfigureAwait(false)).Results.Count, "Deleted fact is absent without reindexing");
                }
            }
            finally
            {
                if (!_NoCleanup && memoryId != null && tenantId != null) await _Driver.Memories.DeleteAsync(tenantId, memoryId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyEmbeddingsAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? tenantId = null;
            string? memoryId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("vectors", token: token).ConfigureAwait(false);
                tenantId = tenant.Id;
                UserMaster owner = await fixture.CreateUserAsync(tenant.Id, "vectors-owner", token: token).ConfigureAwait(false);
                AuthContext caller = AuthContext.Authenticated(tenant.Id, owner.Id, false, false, "test");
                Memory fact = await new MemoryService(_Driver).CreateAsync(caller, new Memory { Content = "Vector source 界" }, token).ConfigureAwait(false);
                memoryId = fact.Id;
                string memoryKey = ContextEmbedding.MemorySourceKey(fact.Id);
                string ruleKey = "context:AI-Memory/repos/例/" + new string('r', 300) + ".md#1";
                float[] precise = { 0.1f, -2.5f, 3.25e-7f, Single.Epsilon, 1e30f };
                await _Driver.ContextEmbeddings.UpsertAsync(new ContextEmbedding { TenantId = tenant.Id, SourceKey = memoryKey, Model = "model-a", ContentSha256 = "aa", Vector = precise }, token).ConfigureAwait(false);
                await _Driver.ContextEmbeddings.UpsertAsync(new ContextEmbedding { TenantId = tenant.Id, SourceKey = memoryKey, Model = "model-b", ContentSha256 = "bb", Vector = new float[] { 1, 2 } }, token).ConfigureAwait(false);
                await _Driver.ContextEmbeddings.UpsertAsync(new ContextEmbedding { TenantId = tenant.Id, SourceKey = ruleKey, Model = "model-a", ContentSha256 = "cc", Vector = new float[] { 4 } }, token).ConfigureAwait(false);
                await _Driver.ContextEmbeddings.UpsertAsync(new ContextEmbedding { TenantId = tenant.Id, SourceKey = ruleKey, Model = "model-a", ContentSha256 = "dd", Vector = new float[] { 5, 6, 7 } }, token).ConfigureAwait(false);

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                using (DatabaseDriver reopened = DatabaseDriverFactory.Create(_Settings, logging))
                {
                    await reopened.InitializeAsync(token).ConfigureAwait(false);
                    System.Collections.Generic.List<ContextEmbedding> read = await reopened.ContextEmbeddings.ReadAsync(tenant.Id, "model-a", new[] { memoryKey, ruleKey, "context:absent" }, token).ConfigureAwait(false);
                    DatabaseAssert.Equal(2, read.Count, "Only stored sources under the requested model are read");
                    ContextEmbedding memoryVector = read.Single(item => item.SourceKey == memoryKey);
                    DatabaseAssert.Equal("aa", memoryVector.ContentSha256, "Content hash round trip");
                    DatabaseAssert.True(precise.SequenceEqual(memoryVector.Vector), "Float32 values round trip bit for bit after reopen");
                    ContextEmbedding ruleVector = read.Single(item => item.SourceKey == ruleKey);
                    DatabaseAssert.Equal("dd", ruleVector.ContentSha256, "An upsert replaces the earlier vector for the same source and model");
                    DatabaseAssert.Equal(3, ruleVector.Vector.Length, "A replacement may change the dimension");
                    DatabaseAssert.Equal(0, (await reopened.ContextEmbeddings.ReadAsync("vectors-other-tenant", "model-a", new[] { memoryKey, ruleKey }, token).ConfigureAwait(false)).Count, "Another tenant reads no vectors");

                    await new MemoryService(reopened).DeleteAsync(caller, fact.Id, token).ConfigureAwait(false);
                    memoryId = null;
                    DatabaseAssert.Equal(0, (await reopened.ContextEmbeddings.ReadAsync(tenant.Id, "model-a", new[] { memoryKey }, token).ConfigureAwait(false)).Count, "Deleting a memory deletes its vectors in the same transaction");
                    DatabaseAssert.Equal(0, (await reopened.ContextEmbeddings.ReadAsync(tenant.Id, "model-b", new[] { memoryKey }, token).ConfigureAwait(false)).Count, "Every model's vector of the deleted memory is gone");
                    DatabaseAssert.Equal(1, await reopened.ContextEmbeddings.DeleteSourceAsync(tenant.Id, ruleKey, token).ConfigureAwait(false), "Source delete reports the rows it removed");
                    DatabaseAssert.Equal(0, (await reopened.ContextEmbeddings.ReadAsync(tenant.Id, "model-a", new[] { ruleKey }, token).ConfigureAwait(false)).Count, "Source delete is durable");
                }
            }
            finally
            {
                if (!_NoCleanup && memoryId != null && tenantId != null) await _Driver.Memories.DeleteAsync(tenantId, memoryId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task VerifyBudgetAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("recall", token: token).ConfigureAwait(false);
                UserMaster owner = await fixture.CreateUserAsync(tenant.Id, "recall-owner", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, owner.Id, "recall", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, owner.Id, fleet.Id, "recall", token).ConfigureAwait(false);
                Mission mission = await fixture.CreateMissionAsync(tenant.Id, owner.Id, null!, vessel.Id, null!, "recall", token,
                    configure: value => value.Status = MissionStatusEnum.InProgress).ConfigureAwait(false);
                DatabaseAssert.True(!(await _Driver.Missions.TryConsumeContextRecallAsync("foreign", owner.Id, mission.Id, 7, token).ConfigureAwait(false)).HasValue, "Foreign tenant cannot spend calls");
                DatabaseAssert.True(!(await _Driver.Missions.TryConsumeContextRecallAsync(tenant.Id, "foreign", mission.Id, 7, token).ConfigureAwait(false)).HasValue, "Foreign owner cannot spend calls");
                Task<int?>[] attempts = Enumerable.Range(0, 20).Select(index => _Driver.Missions.TryConsumeContextRecallAsync(tenant.Id, owner.Id, mission.Id, 7, token)).ToArray();
                int?[] spent = await Task.WhenAll(attempts).ConfigureAwait(false);
                int[] accepted = spent.Where(value => value.HasValue).Select(value => value!.Value).OrderBy(value => value).ToArray();
                DatabaseAssert.Equal("1,2,3,4,5,6,7", String.Join(",", accepted), "Concurrent calls commit exactly the available budget");
                mission.Title = "Updated from a stale mission snapshot";
                await _Driver.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                using (DatabaseDriver reopened = DatabaseDriverFactory.Create(_Settings, logging))
                {
                    await reopened.InitializeAsync(token).ConfigureAwait(false);
                    DatabaseAssert.True(!(await reopened.Missions.TryConsumeContextRecallAsync(tenant.Id, owner.Id, mission.Id, 7, token).ConfigureAwait(false)).HasValue, "Mission snapshot update and reopen cannot reset usage");
                    DatabaseAssert.Equal(8, (await reopened.Missions.TryConsumeContextRecallAsync(tenant.Id, owner.Id, mission.Id, 8, token).ConfigureAwait(false))!.Value, "A raised limit preserves the existing count");
                    mission.Status = MissionStatusEnum.Complete;
                    await _Driver.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    DatabaseAssert.True(!(await reopened.Missions.TryConsumeContextRecallAsync(tenant.Id, owner.Id, mission.Id, 100, token).ConfigureAwait(false)).HasValue, "A terminal mission cannot spend calls");
                }
            }
            finally { await fixture.CleanupAsync(token).ConfigureAwait(false); }
        }
    }
}
