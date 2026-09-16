namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Selection rules for the code-index embedding endpoint: an enabled Embedding endpoint is
    /// preferred over the CodeIndex settings; a pinned id wins; disabled, wrong-kind, and
    /// base-URL-less endpoints are ignored; the oldest wins when several are enabled.
    /// </summary>
    public class EmbeddingClientFactorySelectionTests : TestSuite
    {
        public override string Name => "Embedding Client Factory Selection";

        private static ModelEndpoint Endpoint(string id, ModelEndpointKindEnum kind, bool enabled, string baseUrl, DateTime created)
            => new ModelEndpoint { Id = id, Name = id, Kind = kind, Enabled = enabled, BaseUrl = baseUrl, CreatedUtc = created };

        protected override async Task RunTestsAsync()
        {
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            await RunTest("No endpoints selects nothing (settings fallback)", () =>
            {
                ArmadaSettings s = new ArmadaSettings();
                AssertNull(EmbeddingClientFactory.SelectEmbeddingEndpoint(s, new List<ModelEndpoint>()));
            });

            await RunTest("A single enabled embedding endpoint is selected", () =>
            {
                ArmadaSettings s = new ArmadaSettings();
                ModelEndpoint e = Endpoint("emb-1", ModelEndpointKindEnum.Embedding, true, "https://api.voyageai.com/v1", t0);
                ModelEndpoint? chosen = EmbeddingClientFactory.SelectEmbeddingEndpoint(s, new List<ModelEndpoint> { e });
                AssertNotNull(chosen);
                AssertEqual("emb-1", chosen!.Id);
            });

            await RunTest("A disabled embedding endpoint is ignored", () =>
            {
                ArmadaSettings s = new ArmadaSettings();
                ModelEndpoint e = Endpoint("emb-off", ModelEndpointKindEnum.Embedding, false, "https://api.voyageai.com/v1", t0);
                AssertNull(EmbeddingClientFactory.SelectEmbeddingEndpoint(s, new List<ModelEndpoint> { e }));
            });

            await RunTest("An inference endpoint is not selected for embeddings", () =>
            {
                ArmadaSettings s = new ArmadaSettings();
                ModelEndpoint e = Endpoint("inf", ModelEndpointKindEnum.Inference, true, "https://api.example/v1", t0);
                AssertNull(EmbeddingClientFactory.SelectEmbeddingEndpoint(s, new List<ModelEndpoint> { e }));
            });

            await RunTest("An enabled embedding endpoint with no base URL is ignored", () =>
            {
                ArmadaSettings s = new ArmadaSettings();
                ModelEndpoint e = Endpoint("emb-nourl", ModelEndpointKindEnum.Embedding, true, "", t0);
                AssertNull(EmbeddingClientFactory.SelectEmbeddingEndpoint(s, new List<ModelEndpoint> { e }));
            });

            await RunTest("With several enabled the oldest wins", () =>
            {
                ArmadaSettings s = new ArmadaSettings();
                ModelEndpoint older = Endpoint("emb-old", ModelEndpointKindEnum.Embedding, true, "https://a/v1", t0);
                ModelEndpoint newer = Endpoint("emb-new", ModelEndpointKindEnum.Embedding, true, "https://b/v1", t0.AddHours(1));
                ModelEndpoint? chosen = EmbeddingClientFactory.SelectEmbeddingEndpoint(s, new List<ModelEndpoint> { newer, older });
                AssertNotNull(chosen);
                AssertEqual("emb-old", chosen!.Id);
            });

            await RunTest("A pinned id wins over the oldest", () =>
            {
                ArmadaSettings s = new ArmadaSettings();
                s.CodeIndex.EmbeddingEndpointId = "emb-new";
                ModelEndpoint older = Endpoint("emb-old", ModelEndpointKindEnum.Embedding, true, "https://a/v1", t0);
                ModelEndpoint newer = Endpoint("emb-new", ModelEndpointKindEnum.Embedding, true, "https://b/v1", t0.AddHours(1));
                ModelEndpoint? chosen = EmbeddingClientFactory.SelectEmbeddingEndpoint(s, new List<ModelEndpoint> { older, newer });
                AssertNotNull(chosen);
                AssertEqual("emb-new", chosen!.Id);
            });

            await RunTest("The endpoint key wins, else the settings key, else keyless", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings { EmbeddingApiKey = "settings-key" };
                ModelEndpoint withKey = new ModelEndpoint { Id = "e1", Name = "e1", Kind = ModelEndpointKindEnum.Embedding, BaseUrl = "https://a/v1", ApiKey = "endpoint-key" };
                ModelEndpoint noKey = new ModelEndpoint { Id = "e2", Name = "e2", Kind = ModelEndpointKindEnum.Embedding, BaseUrl = "https://a/v1" };
                AssertEqual("endpoint-key", EmbeddingClientFactory.EffectiveEmbeddingKey(withKey, settings), "the endpoint's own key wins");
                AssertEqual("settings-key", EmbeddingClientFactory.EffectiveEmbeddingKey(noKey, settings), "a keyless endpoint falls back to the settings key");
                AssertEqual(string.Empty, EmbeddingClientFactory.EffectiveEmbeddingKey(noKey, new CodeIndexSettings()), "keyless endpoint and no settings key stays keyless");
            });

            await RunTest("A pinned id that matches nothing enabled falls back to settings", () =>
            {
                ArmadaSettings s = new ArmadaSettings();
                s.CodeIndex.EmbeddingEndpointId = "does-not-exist";
                ModelEndpoint e = Endpoint("emb-1", ModelEndpointKindEnum.Embedding, true, "https://a/v1", t0);
                AssertNull(EmbeddingClientFactory.SelectEmbeddingEndpoint(s, new List<ModelEndpoint> { e }));
            });
        }
    }
}
