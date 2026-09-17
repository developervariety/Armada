namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Builds the code index embedding client. When a registered, enabled Embedding model endpoint
    /// exists, the client uses that endpoint's base URL, model and server-side key, so the code
    /// index embedding provider is managed through the model-endpoints surface instead of the
    /// CodeIndex settings block. With no registered endpoint (or a pinned id that no longer
    /// resolves), the client falls back to the CodeIndex settings, so behaviour is unchanged until
    /// an endpoint is added.
    /// </summary>
    public static class EmbeddingClientFactory
    {
        private const string _Header = "[EmbeddingClientFactory] ";

        /// <summary>
        /// Create an embedding client, preferring a registered embedding endpoint over the settings.
        /// </summary>
        public static async Task<IEmbeddingClient> CreateAsync(
            ArmadaSettings settings,
            DatabaseDriver database,
            LoggingModule logging,
            HttpClient http,
            CancellationToken token = default)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (logging == null) throw new ArgumentNullException(nameof(logging));
            if (http == null) throw new ArgumentNullException(nameof(http));

            CodeIndexSettings source = settings.CodeIndex;
            List<ModelEndpoint> endpoints;
            try
            {
                endpoints = await database.ModelEndpoints.EnumerateAsync(token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logging.Warn(_Header + "could not read model endpoints, using CodeIndex settings: " + e.Message);
                endpoints = new List<ModelEndpoint>();
            }

            ModelEndpoint? endpoint = SelectEmbeddingEndpoint(settings, endpoints, logging);
            if (endpoint == null) return new VoyageEmbeddingClient(source, logging, http);

            // Clone the CodeIndex settings so every other embedding tunable (batch size, timeouts)
            // keeps the operator's values, then override only the provider fields from the endpoint.
            CodeIndexSettings effective;
            try
            {
                effective = JsonSerializer.Deserialize<CodeIndexSettings>(JsonSerializer.Serialize(source)) ?? new CodeIndexSettings();
            }
            catch (Exception e)
            {
                logging.Warn(_Header + "could not clone CodeIndex settings, using endpoint values only: " + e.Message);
                effective = new CodeIndexSettings();
            }

            effective.EmbeddingApiBaseUrl = endpoint.BaseUrl;
            effective.EmbeddingApiKey = EffectiveEmbeddingKey(endpoint, source);
            if (!string.IsNullOrWhiteSpace(endpoint.Model)) effective.EmbeddingModel = endpoint.Model!;
            logging.Info(_Header + "code index embeddings use registered endpoint '" + endpoint.Name + "' (" + endpoint.Id + ")");
            return new VoyageEmbeddingClient(effective, logging, http);
        }

        /// <summary>
        /// The key the embedding client should use: the endpoint's own key when it has one,
        /// otherwise the CodeIndex settings key, so an endpoint registered for its base URL and
        /// model still authenticates. When neither carries a key the client stays keyless, for a
        /// self-hosted provider that needs none.
        /// </summary>
        public static string EffectiveEmbeddingKey(ModelEndpoint endpoint, CodeIndexSettings settings)
        {
            if (endpoint != null && !string.IsNullOrWhiteSpace(endpoint.ApiKey)) return endpoint.ApiKey!;
            return settings?.EmbeddingApiKey ?? string.Empty;
        }

        /// <summary>
        /// Choose the embedding endpoint for the code index: the endpoint named by
        /// <c>codeIndex.embeddingEndpointId</c> when set and enabled; otherwise the single enabled
        /// Embedding endpoint, or the oldest when several are enabled. Returns null when none applies,
        /// which means the caller uses the CodeIndex settings. Pure and side-effect free so the
        /// selection is unit tested without a database.
        /// </summary>
        public static ModelEndpoint? SelectEmbeddingEndpoint(
            ArmadaSettings settings,
            IReadOnlyList<ModelEndpoint> endpoints,
            LoggingModule? logging = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (endpoints == null) return null;

            List<ModelEndpoint> embedding = endpoints
                .Where(e => e != null && e.Kind == ModelEndpointKindEnum.Embedding && e.Enabled && !string.IsNullOrWhiteSpace(e.BaseUrl))
                .ToList();
            if (embedding.Count == 0) return null;

            string? pin = settings.CodeIndex.EmbeddingEndpointId;
            if (!string.IsNullOrWhiteSpace(pin))
            {
                ModelEndpoint? pinned = embedding.FirstOrDefault(e => string.Equals(e.Id, pin, StringComparison.OrdinalIgnoreCase));
                if (pinned != null) return pinned;
                logging?.Warn(_Header + "codeIndex.embeddingEndpointId '" + pin + "' names no enabled embedding endpoint; using CodeIndex settings");
                return null;
            }

            // No pin: the single enabled embedding endpoint, or the oldest when several are enabled.
            return embedding.OrderBy(e => e.CreatedUtc).ThenBy(e => e.Id, StringComparer.Ordinal).First();
        }
    }
}
