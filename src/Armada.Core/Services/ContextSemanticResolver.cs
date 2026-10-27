namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Resolves the semantic index for unified context search from the live settings on every query, so
    /// pinning, unpinning or disabling the endpoint takes effect without a restart.
    /// </summary>
    public sealed class ContextSemanticResolver
    {
        #region Private-Members

        private const string _Header = "[ContextSemanticResolver] ";
        private static readonly HttpClient _SharedHttp = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        private readonly DatabaseDriver _Database;
        private readonly ContextRetrievalSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly HttpClient _Http;
        private readonly Func<CodeIndexSettings, IEmbeddingClient>? _ClientFactory;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create a resolver over the live retrieval settings.</summary>
        /// <param name="database">Database holding endpoints and vectors.</param>
        /// <param name="settings">Live retrieval settings; read on every query.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="http">HTTP client; null uses a shared client.</param>
        /// <param name="clientFactory">Builds the embedding client from endpoint values; null uses the OpenAI-compatible client.</param>
        public ContextSemanticResolver(DatabaseDriver database, ContextRetrievalSettings settings, LoggingModule logging,
            HttpClient? http = null, Func<CodeIndexSettings, IEmbeddingClient>? clientFactory = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Http = http ?? _SharedHttp;
            _ClientFactory = clientFactory;
        }

        #endregion

        #region Public-Methods

        /// <summary>Resolve the index, or the reason semantic ranking is off.</summary>
        /// <param name="token">Cancellation token.</param>
        public async Task<ContextSemanticResolution> ResolveAsync(CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(_Settings.SemanticEndpointId))
                return new ContextSemanticResolution { Reason = ContextSemanticEndpointPolicy.NotConfigured };
            List<ModelEndpoint> endpoints = await _Database.ModelEndpoints.EnumerateAsync(token).ConfigureAwait(false);
            ModelEndpoint? endpoint = ContextSemanticEndpointPolicy.Select(_Settings.SemanticEndpointId, endpoints, out string? reason);
            if (endpoint == null)
            {
                if (reason == ContextSemanticEndpointPolicy.EndpointNotLocal)
                    _Logging.Warn(_Header + "semantic endpoint " + _Settings.SemanticEndpointId + " is not local; context search stays lexical");
                return new ContextSemanticResolution { Reason = reason };
            }

            CodeIndexSettings values = new CodeIndexSettings
            {
                EmbeddingApiBaseUrl = OpenAiCompatibleBase(endpoint.BaseUrl),
                EmbeddingModel = endpoint.Model!,
                EmbeddingApiKey = endpoint.ApiKey ?? ""
            };
            IEmbeddingClient client = _ClientFactory != null ? _ClientFactory(values) : new VoyageEmbeddingClient(values, _Logging, _Http);
            return new ContextSemanticResolution
            {
                Index = new ContextSemanticIndex(_Database.ContextEmbeddings, new TimedEmbeddingClient(client, endpoint.TimeoutMs), endpoint.Model!)
            };
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Registered endpoints store the server root. Both accepted protocols serve the OpenAI-compatible
        /// embeddings route under <c>/v1</c>, which the client extends with <c>/embeddings</c>.
        /// </summary>
        private static string OpenAiCompatibleBase(string baseUrl)
        {
            string trimmed = baseUrl.Trim().TrimEnd('/');
            return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "/v1";
        }

        #endregion

        #region Private-Types

        private sealed class TimedEmbeddingClient : IEmbeddingClient
        {
            private readonly IEmbeddingClient _Inner;
            private readonly int _TimeoutMs;

            internal TimedEmbeddingClient(IEmbeddingClient inner, int timeoutMs)
            {
                _Inner = inner;
                _TimeoutMs = Math.Max(1000, timeoutMs);
            }

            public async Task<float[]> EmbedAsync(string text, CancellationToken token = default)
            {
                using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    limit.CancelAfter(_TimeoutMs);
                    return await _Inner.EmbedAsync(text, limit.Token).ConfigureAwait(false);
                }
            }

            public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken token = default)
            {
                using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    limit.CancelAfter(_TimeoutMs);
                    return await _Inner.EmbedBatchAsync(texts, limit.Token).ConfigureAwait(false);
                }
            }
        }

        #endregion
    }
}
