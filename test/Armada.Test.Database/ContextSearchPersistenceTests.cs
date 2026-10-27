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
    }
}
