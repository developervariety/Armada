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
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>End-to-end recall, authority, scope and budget contracts over actual memory storage.</summary>
    public sealed class ContextSearchServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Unified Context Search";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Compatibility recall tools and a new registration share one durable mission budget", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(db).ConfigureAwait(false);
                    Memory fact = await new MemoryService(db.Driver).CreateAsync(Caller(), new Memory { Content = "checksum fact", VesselId = vessel.Id }).ConfigureAwait(false);
                    Mission mission = await db.Driver.Missions.CreateAsync(new Mission("Recall", "Inspect") { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = vessel.Id, Status = MissionStatusEnum.InProgress }).ConfigureAwait(false);
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.CodeIndex.Enabled = true;
                    settings.ContextRetrieval.MaxCallsPerMission = 3;
                    settings.CodeIndex.CaptainSearchMaxCallsPerMission = 3;
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                    RegisterToolDelegate register = (name, description, schema, handler) => handlers[name] = handler;
                    ContextRecallBudget budget = new ContextRecallBudget(db.Driver, settings);
                    ContextSearchService search = new ContextSearchService(db.Driver, Guidance(vessel.Name));
                    McpContextSearchTools.Register(register, search, settings.ContextRetrieval, db.Driver, budget);
                    McpContextTools.Register(register, Guidance(vessel.Name), db.Driver, settings, sharedBudget: budget);
                    McpMemoryTools.Register(register, db.Driver, settings: settings, sharedBudget: budget);
                    LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                    McpMissionCodeSearchTools.Register(register, new CodeIndexService(logging, db.Driver, settings, new GitService(logging)), db.Driver, settings, sharedBudget: budget);
                    AuthContext caller = Caller(); caller.MissionId = mission.Id;
                    using (McpCallerContext.Begin(caller))
                    {
                        JsonElement args = JsonSerializer.SerializeToElement(new { query = "checksum", includeCode = false });
                        AssertTrue(await handlers[McpContextSearchTools.ToolName](args).ConfigureAwait(false) is ContextSearchResponse, "Canonical recall spends the first call");
                        AssertContains("\"Available\":true", JsonSerializer.Serialize(await handlers[McpContextTools.FetchContextToolName](args).ConfigureAwait(false)), "Compatibility guidance spends the second call");
                        JsonElement listing = JsonSerializer.SerializeToElement(new { search = "checksum" });
                        AssertContains(fact.Id, JsonSerializer.Serialize(await handlers["search_memory"](listing).ConfigureAwait(false)), "Compatibility native listing spends the third call");
                        McpContextSearchTools.Register(register, search, settings.ContextRetrieval, db.Driver, new ContextRecallBudget(db.Driver, settings));
                        AssertContains("budget", JsonSerializer.Serialize(await handlers[McpContextSearchTools.ToolName](args).ConfigureAwait(false)), "A new service registration cannot reset usage");
                        JsonElement code = JsonSerializer.SerializeToElement(new { query = "checksum", missionId = mission.Id });
                        AssertContains("budget", JsonSerializer.Serialize(await handlers[McpMissionCodeSearchTools.ToolName](code).ConfigureAwait(false)), "The code alias cannot bypass the common counter");
                    }
                }
            });

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
                    McpContextSearchTools.Register((name, description, schema, handler) => tools[name] = handler, new ContextSearchService(db.Driver, Guidance(vessel.Name)), limits, db.Driver, new ContextRecallBudget(db.Driver, new ArmadaSettings { ContextRetrieval = limits }));
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

            await RunTest("Local semantic ranking recalls a reworded fact and keeps vectors current with the source", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(db).ConfigureAwait(false);
                    MemoryService memories = new MemoryService(db.Driver);
                    Memory contention = await memories.CreateAsync(Caller(), new Memory { Content = "Timing failures appear under host contention.", VesselId = vessel.Id }).ConfigureAwait(false);
                    Memory checksum = await memories.CreateAsync(Caller(), new Memory { Content = "Verify the packet checksum.", VesselId = vessel.Id }).ConfigureAwait(false);
                    ConceptEmbeddingClient client = new ConceptEmbeddingClient();
                    ContextSearchService search = new ContextSearchService(db.Driver, logging: Quiet(), semantic: await SemanticAsync(db, client).ConfigureAwait(false));
                    ContextSearchRequest reworded = new ContextSearchRequest { Query = "suites break while the machine is busy", VesselId = vessel.Id, IncludeCode = false };

                    ContextSearchResponse lexicalOnly = await new ContextSearchService(db.Driver).SearchAsync(Caller(), reworded).ConfigureAwait(false);
                    AssertEqual(0, lexicalOnly.Results.Count, "Lexical ranking alone shares no term with the reworded query");
                    ContextSearchResponse first = await search.SearchAsync(Caller(), reworded).ConfigureAwait(false);
                    AssertEqual(contention.Id, first.Results[0].Reference, "The semantically nearest fact ranks first");
                    AssertTrue(first.Sources.Single(source => source.Source == "semantic").Available, "Semantic ranking reports itself available");
                    AssertEqual("hybrid", first.Sources.Single(source => source.Source == "memory").SearchMode, "Memory reports the fused mode");
                    AssertEqual(3, client.Texts, "The query and each fact are embedded once");

                    await search.SearchAsync(Caller(), reworded).ConfigureAwait(false);
                    AssertEqual(4, client.Texts, "A repeated query embeds only itself and reuses stored vectors");
                    await memories.UpdateAsync(Caller(), checksum.Id, new MemoryUpdate { Content = "Builds stall when the host is overloaded.", ExpectedVersion = 1 }).ConfigureAwait(false);
                    ContextSearchResponse updated = await search.SearchAsync(Caller(), reworded).ConfigureAwait(false);
                    AssertEqual(6, client.Texts, "Changed content is embedded again");
                    AssertTrue(updated.Results.Take(2).Any(item => item.Reference == checksum.Id), "The updated fact ranks by its new content");

                    string key = ContextEmbedding.MemorySourceKey(contention.Id);
                    AssertEqual(1, (await db.Driver.ContextEmbeddings.ReadAsync(Constants.DefaultTenantId, ConceptEmbeddingClient.ModelName, new[] { key }).ConfigureAwait(false)).Count, "Vector stored before delete");
                    await memories.DeleteAsync(Caller(), contention.Id).ConfigureAwait(false);
                    AssertEqual(0, (await db.Driver.ContextEmbeddings.ReadAsync(Constants.DefaultTenantId, ConceptEmbeddingClient.ModelName, new[] { key }).ConfigureAwait(false)).Count, "Deleting a fact deletes its derived vector");
                }
            });

            await RunTest("Semantic ranking refuses hosted endpoints and falls back to lexical with a named reason", async () =>
            {
                AssertTrue(ContextSemanticEndpointPolicy.IsLocalHost("http://127.0.0.1:11434"), "Loopback");
                AssertTrue(ContextSemanticEndpointPolicy.IsLocalHost("http://[::1]:11434"), "IPv6 loopback");
                AssertTrue(ContextSemanticEndpointPolicy.IsLocalHost("http://10.2.3.4"), "Private class A");
                AssertTrue(ContextSemanticEndpointPolicy.IsLocalHost("http://172.20.0.5:8080"), "Private class B");
                AssertTrue(ContextSemanticEndpointPolicy.IsLocalHost("http://192.168.1.9"), "Private class C");
                AssertTrue(ContextSemanticEndpointPolicy.IsLocalHost("http://ollama:11434"), "Single-label container name");
                AssertTrue(ContextSemanticEndpointPolicy.IsLocalHost("http://host.docker.internal:11434"), "Internal suffix");
                AssertFalse(ContextSemanticEndpointPolicy.IsLocalHost("https://api.example.com/v1"), "Public name");
                AssertFalse(ContextSemanticEndpointPolicy.IsLocalHost("http://172.32.0.1"), "Outside the private class B range");
                AssertFalse(ContextSemanticEndpointPolicy.IsLocalHost("http://8.8.8.8"), "Public address");
                AssertFalse(ContextSemanticEndpointPolicy.IsLocalHost("ftp://127.0.0.1"), "Non-HTTP scheme");

                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(db).ConfigureAwait(false);
                    Memory fact = await new MemoryService(db.Driver).CreateAsync(Caller(), new Memory { Content = "Verify the packet checksum.", VesselId = vessel.Id }).ConfigureAwait(false);
                    ContextSearchRequest request = new ContextSearchRequest { Query = "packet checksum", VesselId = vessel.Id, IncludeCode = false };
                    ContextRetrievalSettings settings = new ContextRetrievalSettings();
                    int built = 0;
                    ConceptEmbeddingClient client = new ConceptEmbeddingClient();
                    ContextSearchService search = new ContextSearchService(db.Driver, logging: Quiet(),
                        semantic: new ContextSemanticResolver(db.Driver, settings, Quiet(), clientFactory: _ => { built++; return client; }));

                    ContextSearchResponse unpinned = await search.SearchAsync(Caller(), request).ConfigureAwait(false);
                    AssertEqual(ContextSemanticEndpointPolicy.NotConfigured, unpinned.Sources.Single(source => source.Source == "semantic").Reason, "No pin keeps search lexical");
                    AssertEqual(fact.Id, unpinned.Results.Single().Reference, "Lexical result still returned");

                    ModelEndpoint hosted = await CreateEndpointAsync(db, ModelProviderEnum.OpenAI, "https://api.example.com").ConfigureAwait(false);
                    settings.SemanticEndpointId = hosted.Id;
                    ContextSearchResponse refused = await search.SearchAsync(Caller(), request).ConfigureAwait(false);
                    AssertEqual(ContextSemanticEndpointPolicy.EndpointNotLocal, refused.Sources.Single(source => source.Source == "semantic").Reason, "A hosted endpoint is refused");
                    ModelEndpoint publicHost = await CreateEndpointAsync(db, ModelProviderEnum.Ollama, "http://embeddings.example.com:11434").ConfigureAwait(false);
                    settings.SemanticEndpointId = publicHost.Id;
                    AssertEqual(ContextSemanticEndpointPolicy.EndpointNotLocal, (await search.SearchAsync(Caller(), request).ConfigureAwait(false)).Sources.Single(source => source.Source == "semantic").Reason, "A local protocol on a public host is refused");
                    settings.SemanticEndpointId = "mep_absent";
                    AssertEqual(ContextSemanticEndpointPolicy.EndpointUnavailable, (await search.SearchAsync(Caller(), request).ConfigureAwait(false)).Sources.Single(source => source.Source == "semantic").Reason, "An absent pin is named");
                    AssertEqual(0, built, "No refused endpoint ever receives text");

                    ModelEndpoint local = await CreateEndpointAsync(db, ModelProviderEnum.Ollama, "http://127.0.0.1:11434").ConfigureAwait(false);
                    settings.SemanticEndpointId = local.Id;
                    client.Fail = true;
                    ContextSearchResponse failed = await search.SearchAsync(Caller(), request).ConfigureAwait(false);
                    AssertEqual("embedding_failed", failed.Sources.Single(source => source.Source == "semantic").Reason, "An endpoint failure is named");
                    AssertEqual("bm25", failed.Sources.Single(source => source.Source == "memory").SearchMode, "A failed endpoint leaves the lexical mode");
                    AssertEqual(fact.Id, failed.Results.Single().Reference, "A failed endpoint still returns lexical results");

                    client.Fail = false;
                    client.DocumentDimensions = 2;
                    ContextSearchResponse mismatched = await search.SearchAsync(Caller(), request).ConfigureAwait(false);
                    AssertEqual(fact.Id, mismatched.Results.Single().Reference, "Vectors of another dimension are ignored, not compared");
                    AssertEqual(0, (await db.Driver.ContextEmbeddings.ReadAsync(Constants.DefaultTenantId, ConceptEmbeddingClient.ModelName, new[] { ContextEmbedding.MemorySourceKey(fact.Id) }).ConfigureAwait(false)).Count, "A vector of the wrong dimension is never stored");
                }
            });
        }

        private static AuthContext Caller() => AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "test");

        private static Task<Vessel> CreateVesselAsync(TestDatabase db) => db.Driver.Vessels.CreateAsync(new Vessel
        {
            Name = "ExampleVessel" + Guid.NewGuid().ToString("N"), RepoUrl = "https://github.com/example/sample.git",
            TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId
        });

        private static LoggingModule Quiet()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static async Task<ContextSemanticResolver> SemanticAsync(TestDatabase db, ConceptEmbeddingClient client)
        {
            ModelEndpoint endpoint = await CreateEndpointAsync(db, ModelProviderEnum.Ollama, "http://127.0.0.1:11434").ConfigureAwait(false);
            ContextRetrievalSettings settings = new ContextRetrievalSettings { SemanticEndpointId = endpoint.Id };
            return new ContextSemanticResolver(db.Driver, settings, Quiet(), clientFactory: _ => client);
        }

        private static Task<ModelEndpoint> CreateEndpointAsync(TestDatabase db, ModelProviderEnum provider, string baseUrl) => db.Driver.ModelEndpoints.CreateAsync(new ModelEndpoint
        {
            TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, Name = "embeddings-" + Guid.NewGuid().ToString("N"),
            Kind = ModelEndpointKindEnum.Embedding, Provider = provider, BaseUrl = baseUrl, Model = ConceptEmbeddingClient.ModelName, Enabled = true
        });

        /// <summary>
        /// Embeds text into fixed concept axes, so two wordings of one idea share a vector while sharing no
        /// word. It counts every text it embeds, query or document.
        /// </summary>
        private sealed class ConceptEmbeddingClient : IEmbeddingClient
        {
            internal const string ModelName = "concept-test";
            private static readonly string[][] _Concepts =
            {
                new[] { "contention", "busy", "overloaded", "stall", "timing", "break" },
                new[] { "checksum", "packet", "integrity" }
            };

            internal int Texts { get; private set; }
            internal bool Fail { get; set; }
            internal int? DocumentDimensions { get; set; }

            public Task<float[]> EmbedAsync(string text, CancellationToken token = default)
            {
                Texts++;
                return Task.FromResult(Fail ? Array.Empty<float>() : Vector(text, null));
            }

            public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken token = default)
            {
                Texts += texts.Count;
                IReadOnlyList<float[]> vectors = texts.Select(text => Fail ? Array.Empty<float>() : Vector(text, DocumentDimensions)).ToList();
                return Task.FromResult(vectors);
            }

            private static float[] Vector(string text, int? dimensions)
            {
                string lower = text.ToLowerInvariant();
                float[] vector = new float[dimensions ?? _Concepts.Length + 1];
                for (int axis = 0; axis < _Concepts.Length && axis < vector.Length; axis++)
                    vector[axis] = _Concepts[axis].Count(word => lower.Contains(word, StringComparison.Ordinal));
                vector[vector.Length - 1] = 0.1f;
                return vector;
            }
        }

        private static ContextRetrievalService Guidance(string vessel) => new ContextRetrievalService(new[]
        {
            new ContextChunk { Topic = "core", Path = "AI-Memory/shared/rules.md", Tier = ContextTierEnum.Core, Text = "Required owner rules." },
            new ContextChunk { Topic = "mandatory", Path = "AI-Memory/repos/example/safety.md", MustRetrieve = new List<string> { vessel }, Text = "Mandatory vessel guidance." },
            new ContextChunk { Topic = "checksum", Path = "AI-Memory/repos/example/checksum.md", Summary = "Packet checksum", Text = "Follow the packet checksum rule." }
        });
    }
}
