namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Context;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>End-to-end recall, authority, scope and budget contracts over actual memory storage.</summary>
    public sealed class ContextSearchServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Unified Context Search";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Rules and facts share a query while mandatory guidance survives zero recall budget", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(db).ConfigureAwait(false);
                    MemoryService memories = new MemoryService(db.Driver);
                    Memory fact = await memories.CreateAsync(Caller(), new Memory { Key = "packet/checksum", Content = "Check packet integrity with its checksum.", VesselId = vessel.Id }).ConfigureAwait(false);
                    ContextSearchService search = new ContextSearchService(db.Driver, Guidance(vessel.Name));
                    ContextSearchResponse result = await search.SearchAsync(Caller(), new ContextSearchRequest { Query = "packet checksum", VesselId = vessel.Id, IncludeCode = false }).ConfigureAwait(false);
                    AssertEqual(2, result.Required.Count, "Core and applicable mandatory guidance both ship");
                    AssertTrue(result.Required.All(item => item.Required), "Mandatory authority is explicit");
                    AssertTrue(result.Results.Any(item => item.Source == ContextSearchSourceEnum.Rule), "Relevant optional rule returned");
                    ContextSearchItem recalled = result.Results.Single(item => item.Reference == fact.Id);
                    AssertEqual(ContextSearchSourceEnum.Memory, recalled.Source, "A fact never acquires rule authority");
                    AssertEqual("1", recalled.Version, "Native source version retained");
                    AssertEqual(fact.Content, recalled.Text, "Source text retained");

                    ContextSearchResponse bounded = await search.SearchAsync(Caller(), new ContextSearchRequest { Query = "packet checksum", VesselId = vessel.Id, MaxBytes = 0, Limit = 1, IncludeCode = false }).ConfigureAwait(false);
                    AssertEqual(2, bounded.Required.Count, "Recall limits cannot remove required guidance");
                    AssertEqual(0, bounded.Results.Count, "Zero optional budget");
                    AssertTrue(bounded.Truncated, "Omitted matches are reported");
                    AssertEqual(0, bounded.ResultBytes, "Optional byte count excludes required rules");

                    await memories.UpdateAsync(Caller(), fact.Id, new MemoryUpdate { Content = "A revised packet checksum finding.", ExpectedVersion = 1 }).ConfigureAwait(false);
                    ContextSearchResponse updated = await search.SearchAsync(Caller(), new ContextSearchRequest { Query = "revised checksum", VesselId = vessel.Id, IncludeCode = false }).ConfigureAwait(false);
                    AssertEqual("2", updated.Results.Single(item => item.Reference == fact.Id).Version, "Recall observes updates without a stale derived store");
                    await memories.DeleteAsync(Caller(), fact.Id).ConfigureAwait(false);
                    ContextSearchResponse deleted = await search.SearchAsync(Caller(), new ContextSearchRequest { Query = "revised checksum", VesselId = vessel.Id, IncludeCode = false }).ConfigureAwait(false);
                    AssertFalse(deleted.Results.Any(item => item.Reference == fact.Id), "Deleted record is not recalled");
                }
            });

            await RunTest("Visibility is applied before ranking and administrator mission tokens cannot widen vessel or owner", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel own = await CreateVesselAsync(db).ConfigureAwait(false);
                    Vessel other = await CreateVesselAsync(db).ConfigureAwait(false);
                    MemoryService memories = new MemoryService(db.Driver);
                    Memory visible = await memories.CreateAsync(Caller(), new Memory { Content = "packet checksum", VesselId = own.Id }).ConfigureAwait(false);
                    await memories.CreateAsync(Caller(), new Memory { Content = "packet checksum other vessel", VesselId = other.Id }).ConfigureAwait(false);
                    AuthContext another = AuthContext.Authenticated(Constants.DefaultTenantId, "other-owner", false, false, "test");
                    await memories.CreateAsync(another, new Memory { Content = "packet checksum private", VesselId = own.Id }).ConfigureAwait(false);
                    AuthContext foreign = AuthContext.Authenticated("other-tenant", "other-owner", true, false, "test");
                    await memories.CreateAsync(foreign, new Memory { Content = "packet checksum foreign", Scope = MemoryScopeEnum.TenantWide }).ConfigureAwait(false);
                    Mission mission = await db.Driver.Missions.CreateAsync(new Mission("Query scope", "Inspect")
                    {
                        TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = own.Id, Status = MissionStatusEnum.InProgress
                    }).ConfigureAwait(false);
                    AuthContext captain = Caller(); captain.IsAdmin = true; captain.MissionId = mission.Id;
                    ContextSearchService search = new ContextSearchService(db.Driver);
                    ContextSearchResponse result = await search.SearchAsync(captain, new ContextSearchRequest { Query = "packet checksum", VesselId = other.Id, IncludeCode = false }).ConfigureAwait(false);
                    AssertEqual(own.Id, result.VesselId, "Credential wins over supplied vessel");
                    AssertEqual(1, result.Results.Count, "Foreign tenant, private owner and other vessel excluded");
                    AssertEqual(visible.Id, result.Results[0].Reference, "Only authorized fact returned");
                    AssertTrue(result.Sources.Any(source => source.Source == "rules" && source.Reason == "not_configured"), "Missing rules are named rather than silently empty");

                    mission.Status = MissionStatusEnum.Complete;
                    await db.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
                    await AssertThrowsAsync<UnauthorizedAccessException>(() => search.SearchAsync(captain, new ContextSearchRequest { Query = "packet", VesselId = own.Id })).ConfigureAwait(false);
                    await AssertThrowsAsync<KeyNotFoundException>(() => search.SearchAsync(another, new ContextSearchRequest { Query = "packet", VesselId = own.Id })).ConfigureAwait(false);
                }
            });

            await RunTest("Unrelated queries return no optional facts and UTF8 budgets skip large matches", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(db).ConfigureAwait(false);
                    MemoryService memories = new MemoryService(db.Driver);
                    await memories.CreateAsync(Caller(), new Memory { Summary = "checksum checksum checksum", Content = "checksum " + new string('界', 100), VesselId = vessel.Id }).ConfigureAwait(false);
                    Memory small = await memories.CreateAsync(Caller(), new Memory { Content = "checksum 界", VesselId = vessel.Id }).ConfigureAwait(false);
                    ContextSearchService search = new ContextSearchService(db.Driver);
                    ContextSearchResponse absent = await search.SearchAsync(Caller(), new ContextSearchRequest { Query = "unrelatedxyz", VesselId = vessel.Id, IncludeCode = false }).ConfigureAwait(false);
                    AssertEqual(0, absent.Results.Count, "A valid empty match is empty");
                    AssertTrue(absent.Sources.Single(source => source.Source == "memory").Available, "Empty differs from unavailable");
                    ContextSearchResponse bounded = await search.SearchAsync(Caller(), new ContextSearchRequest { Query = "checksum", VesselId = vessel.Id, MaxBytes = Encoding.UTF8.GetByteCount(small.Content), IncludeCode = false }).ConfigureAwait(false);
                    AssertEqual(small.Id, bounded.Results.Single().Reference, "An oversized high match cannot starve a smaller result");
                    AssertEqual(Encoding.UTF8.GetByteCount(small.Content), bounded.ResultBytes, "Budget uses UTF8 bytes");
                    using (CancellationTokenSource canceled = new CancellationTokenSource())
                    {
                        canceled.Cancel();
                        await AssertThrowsAsync<OperationCanceledException>(() => search.SearchAsync(Caller(), new ContextSearchRequest { Query = "checksum", VesselId = vessel.Id }, canceled.Token)).ConfigureAwait(false);
                    }
                }
            });

            await RunTest("Registered tool applies server byte limits and mission call budget", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(db).ConfigureAwait(false);
                    await new MemoryService(db.Driver).CreateAsync(Caller(), new Memory { Content = "checksum", VesselId = vessel.Id }).ConfigureAwait(false);
                    Mission mission = await db.Driver.Missions.CreateAsync(new Mission("Recall", "Inspect") { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = vessel.Id, Status = MissionStatusEnum.InProgress }).ConfigureAwait(false);
                    Dictionary<string, Func<JsonElement?, Task<object>>> tools = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                    ContextRetrievalSettings limits = new ContextRetrievalSettings { MaxCallsPerMission = 1, MaxLeafBytesPerCall = 0 };
                    McpContextSearchTools.Register((name, description, schema, handler) => tools[name] = handler, new ContextSearchService(db.Driver, Guidance(vessel.Name)), limits);
                    AuthContext caller = Caller(); caller.MissionId = mission.Id;
                    AssertTrue(McpToolAccessPolicy.IsAllowed(caller, McpContextSearchTools.ToolName), "Mission catalog grants the scoped query");
                    using (McpCallerContext.Begin(caller))
                    {
                        JsonElement args = JsonSerializer.SerializeToElement(new { query = "checksum", maxBytes = 1000000 });
                        object first = await tools[McpContextSearchTools.ToolName](args).ConfigureAwait(false);
                        AssertTrue(first is ContextSearchResponse, "Real handler calls shared service");
                        AssertEqual(0, ((ContextSearchResponse)first).Results.Count, "Client cannot raise server byte cap");
                        AssertEqual(2, ((ContextSearchResponse)first).Required.Count, "Server cap does not trim core rules");
                        string second = JsonSerializer.Serialize(await tools[McpContextSearchTools.ToolName](args).ConfigureAwait(false));
                        AssertContains("budget", second, "Repeated mission calls hit the configured budget");
                        limits.FetchToolEnabled = false;
                        AssertContains("disabled", JsonSerializer.Serialize(await tools[McpContextSearchTools.ToolName](args).ConfigureAwait(false)), "Existing kill switch covers shared query");
                    }
                }
            });
        }

        private static AuthContext Caller() => AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "test");

        private static Task<Vessel> CreateVesselAsync(TestDatabase db) => db.Driver.Vessels.CreateAsync(new Vessel
        {
            Name = "ExampleVessel" + Guid.NewGuid().ToString("N"), TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId
        });

        private static ContextRetrievalService Guidance(string vessel) => new ContextRetrievalService(new[]
        {
            new ContextChunk { Topic = "core", Path = "AI-Memory/shared/rules.md", Tier = ContextTierEnum.Core, Text = "Required owner rules." },
            new ContextChunk { Topic = "mandatory", Path = "AI-Memory/repos/example/safety.md", MustRetrieve = new List<string> { vessel }, Text = "Mandatory vessel guidance." },
            new ContextChunk { Topic = "checksum", Path = "AI-Memory/repos/example/checksum.md", Summary = "Packet checksum", Text = "Follow the packet checksum rule." }
        });
    }
}
