namespace Armada.Client
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Client.Models;
    using Armada.Core.Models;

    /// <summary>
    /// Calls the TUI needs beyond the dashboard's <c>api/client.ts</c> (the shared i18n catalog and the OpenAPI
    /// document), plus private helpers shared by the area files.
    /// </summary>
    public partial class ArmadaClient
    {
        #region Private-Members

        private static readonly HashSet<string> _EntityEndpoints = new HashSet<string>(StringComparer.Ordinal)
        {
            "fleets", "vessels", "captains", "missions", "voyages", "signals", "events", "docks", "merge-queue",
            "playbooks", "objectives", "releases", "environments", "deployments"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Fetch the shared i18n catalog the dashboard uses (<c>/dashboard/i18n/armada.json</c>). Unauthenticated.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The catalog, or null when the server returned an empty body.</returns>
        /// <exception cref="ArmadaApiException">Thrown for a non-success response, timeout, or transport failure.</exception>
        public Task<I18nCatalog?> GetI18nCatalogAsync(CancellationToken token = default)
        {
            return GetAsync<I18nCatalog>("/dashboard/i18n/armada.json", null, token);
        }

        /// <summary>
        /// Fetch the server's OpenAPI document as raw JSON (the API Explorer's source).
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The document.</returns>
        /// <exception cref="ArmadaApiException">Thrown for a non-success response, timeout, or transport failure.</exception>
        public Task<ArmadaRawJson?> GetOpenApiDocumentAsync(CancellationToken token = default)
        {
            return GetAsync<ArmadaRawJson>("/openapi.json", null, token);
        }

        #endregion

        #region Private-Methods

        private static JsonObject WithApiKey(ModelEndpoint data, string? apiKey)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            JsonNode? node = JsonSerializer.SerializeToNode(data, ArmadaJson.Options);
            JsonObject obj = node as JsonObject ?? new JsonObject();
            if (apiKey != null) obj["apiKey"] = apiKey;
            // A newly constructed update that does not mention inference settings must preserve the stored policy.
            // Also honor callers that mutate a nondefault setting through the options getter.
            if (!data.InferenceOptionsSpecified && !data.InferenceOptions.HasNonDefaultSettings) obj.Remove("inferenceOptions");

            // Header values are write-only, so the model never serializes them: send the list (values included)
            // only when the caller set it, and otherwise leave it out so the server keeps the stored headers.
            obj.Remove("headers");
            if (data.HeadersSpecified)
            {
                JsonArray headers = new JsonArray();
                foreach (ModelEndpointHeader header in data.Headers)
                {
                    JsonObject entry = new JsonObject();
                    entry["name"] = header.Name;
                    entry["value"] = header.Value;
                    headers.Add(entry);
                }

                obj["headers"] = headers;
            }

            return obj;
        }

        #endregion
    }
}
