namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Creates, updates, enumerates, validates, and health-checks managed model endpoints (embedding and
    /// inference). A probe is bounded by the endpoint timeout and always uses that endpoint's own credentials.
    /// </summary>
    public class ModelEndpointService
    {
        #region Private-Members

        private readonly string _Header = "[ModelEndpointService] ";
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private readonly Func<ModelEndpoint, CancellationToken, Task<ModelEndpointProbeResult>>? _Probe;
        private readonly Func<string, CancellationToken, Task<bool>>? _IsInUse;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="probe">Optional probe used by tests; production validation uses provider requests and health uses bounded connectivity.</param>
        /// <param name="isInUse">Optional captain reference check, supplied when runtime endpoint links are enabled.</param>
        public ModelEndpointService(
            DatabaseDriver database,
            LoggingModule logging,
            Func<ModelEndpoint, CancellationToken, Task<ModelEndpointProbeResult>>? probe = null,
            Func<string, CancellationToken, Task<bool>>? isInUse = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Probe = probe;
            _IsInUse = isInUse;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Enumerate model endpoints visible to the caller (all endpoints for an admin, otherwise the caller's
        /// tenant), newest first.
        /// </summary>
        /// <param name="auth">Authentication context.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of model endpoints.</returns>
        public async Task<List<ModelEndpoint>> EnumerateAsync(AuthContext auth, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));

            RequireAuthenticated(auth, auth.IsAdmin);
            if (auth.IsAdmin)
                return await _Database.ModelEndpoints.EnumerateAsync(token).ConfigureAwait(false);

            List<ModelEndpoint> tenantEndpoints = await _Database.ModelEndpoints.EnumerateAsync(auth.TenantId!, token).ConfigureAwait(false);
            return tenantEndpoints.Where(endpoint => IsVisible(auth, endpoint)).ToList();
        }

        /// <summary>
        /// Read a single model endpoint within the caller scope, or null when not found or not visible.
        /// </summary>
        /// <param name="auth">Authentication context.</param>
        /// <param name="id">Endpoint identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The endpoint or null.</returns>
        public async Task<ModelEndpoint?> ReadAsync(AuthContext auth, string id, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            RequireAuthenticated(auth, auth.IsAdmin);

            ModelEndpoint? endpoint = await _Database.ModelEndpoints.ReadAsync(id, token).ConfigureAwait(false);
            if (endpoint == null) return null;
            if (!IsVisible(auth, endpoint)) return null;
            return endpoint;
        }

        /// <summary>
        /// Create a model endpoint. The caller's tenant and user own the record. The provider/kind combination
        /// is validated (Anthropic cannot embed, Voyage AI cannot do inference) and a base URL is required.
        /// </summary>
        /// <param name="auth">Authentication context.</param>
        /// <param name="endpoint">Endpoint to create.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created endpoint (with the API key redacted from serialization).</returns>
        public async Task<ModelEndpoint> CreateAsync(AuthContext auth, ModelEndpoint endpoint, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));

            RequireTenantUser(auth);
            ValidateShape(endpoint);
            if (endpoint.Scope == ScopeEnum.TenantWide && !auth.IsAdmin && !auth.IsTenantAdmin)
                throw new UnauthorizedAccessException("Tenant-wide model endpoints require tenant administrator access.");

            endpoint.TenantId = auth.TenantId;
            endpoint.UserId = auth.UserId;
            endpoint.CreatedUtc = DateTime.UtcNow;
            endpoint.LastUpdateUtc = DateTime.UtcNow;
            endpoint.HealthStatus = EndpointHealthStatusEnum.Unknown;
            endpoint.LastHealthCheckUtc = null;
            endpoint.LastHealthError = null;
            endpoint.LastLatencyMs = null;
            endpoint.HealthHistory = new List<ModelEndpointHealthRecord>();
            if (await _Database.ModelEndpoints.ExistsAsync(endpoint.Id, token).ConfigureAwait(false))
                throw new InvalidOperationException("A model endpoint with this identifier already exists.");

            _Logging.Info(_Header + "creating endpoint " + endpoint.Id + " (" + endpoint.Provider + "/" + endpoint.Kind + ")");
            return await _Database.ModelEndpoints.CreateAsync(endpoint, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Update a model endpoint. Editable fields are copied from the supplied instance onto the stored
        /// record. The API key is preserved unless the caller explicitly supplied a new value.
        /// </summary>
        /// <param name="auth">Authentication context.</param>
        /// <param name="endpoint">Endpoint carrying updated fields (its Id selects the record).</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The updated endpoint.</returns>
        public async Task<ModelEndpoint> UpdateAsync(AuthContext auth, ModelEndpoint endpoint, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));

            RequireTenantUser(auth);
            ModelEndpoint? existing = await _Database.ModelEndpoints.ReadAsync(endpoint.Id, token).ConfigureAwait(false);
            if (existing == null) throw new KeyNotFoundException("Model endpoint not found: " + endpoint.Id);
            if (!CanEdit(auth, existing)) throw new UnauthorizedAccessException("Not permitted to modify model endpoint.");

            ValidateShape(endpoint);

            existing.Name = endpoint.Name;
            existing.Kind = endpoint.Kind;
            existing.Provider = endpoint.Provider;
            existing.BaseUrl = endpoint.BaseUrl;
            existing.Model = endpoint.Model;
            existing.Dimensionality = endpoint.Dimensionality;
            existing.TimeoutMs = endpoint.TimeoutMs;
            existing.Enabled = endpoint.Enabled;
            // Scope is an ownership boundary. It is immutable after creation, so an update request cannot
            // move a private endpoint to another visibility class or silently broaden its audience.
            if (endpoint.ApiKeySpecified) existing.ApiKey = endpoint.ApiKey;
            existing.LastUpdateUtc = DateTime.UtcNow;

            _Logging.Info(_Header + "updating endpoint " + existing.Id);
            return await _Database.ModelEndpoints.UpdateAsync(existing, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a model endpoint within the caller scope.
        /// </summary>
        /// <param name="auth">Authentication context.</param>
        /// <param name="id">Endpoint identifier.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task DeleteAsync(AuthContext auth, string id, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            RequireTenantUser(auth);
            ModelEndpoint? existing = await _Database.ModelEndpoints.ReadAsync(id, token).ConfigureAwait(false);
            if (existing == null) throw new KeyNotFoundException("Model endpoint not found: " + id);
            if (!CanEdit(auth, existing)) throw new UnauthorizedAccessException("Not permitted to delete model endpoint.");
            if (_IsInUse != null && await _IsInUse(id, token).ConfigureAwait(false))
                throw new InvalidOperationException("Model endpoint is in use by a captain.");

            _Logging.Info(_Header + "deleting endpoint " + id);
            try
            {
                await _Database.ModelEndpoints.DeleteAsync(id, token).ConfigureAwait(false);
            }
            catch (Exception e) when (IsReferenceConstraintViolation(e))
            {
                // The captain-link foreign key is the atomic backstop for a link created after the
                // advisory callback above. Keep provider exception text and credentials out of the API.
                throw new InvalidOperationException("Model endpoint is in use by a captain.");
            }
        }

        /// <summary>
        /// Validate the configured model with a bounded, provider-specific request and persist the result.
        /// </summary>
        /// <param name="auth">Authentication context.</param>
        /// <param name="id">Endpoint identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The probe result.</returns>
        public async Task<ModelEndpointProbeResult> ValidateAsync(AuthContext auth, string id, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            RequireTenantUser(auth);
            ModelEndpoint? endpoint = await _Database.ModelEndpoints.ReadAsync(id, token).ConfigureAwait(false);
            if (endpoint == null) throw new KeyNotFoundException("Model endpoint not found: " + id);
            if (!IsVisible(auth, endpoint)) throw new UnauthorizedAccessException("Not permitted to validate endpoint " + id);

            ModelEndpointProbeResult result = _Probe == null
                ? await ValidateEndpointAsync(endpoint, token).ConfigureAwait(false)
                : await _Probe(endpoint, token).ConfigureAwait(false);
            await PersistProbeAsync(endpoint, result, token).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Run a system-wide health sweep across all enabled endpoints. Every endpoint is probed independently,
        /// with its own credentials and timeout, even when endpoints share a base URL.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of endpoints probed.</returns>
        public async Task<int> CheckHealthAllAsync(CancellationToken token = default)
        {
            List<ModelEndpoint> all = await _Database.ModelEndpoints.EnumerateAsync(token).ConfigureAwait(false);
            List<ModelEndpoint> enabled = all.Where(e => e.Enabled).ToList();

            int probed = 0;
            foreach (ModelEndpoint endpoint in enabled)
            {
                token.ThrowIfCancellationRequested();
                // The health sweep runs the same real provider request as a manual validation, so a
                // provider whose base URL rejects a bare GET (VoyageAI, OpenAI) is not read as Unhealthy
                // while its model endpoint answers correctly.
                ModelEndpointProbeResult result = _Probe == null
                    ? await ValidateEndpointAsync(endpoint, token).ConfigureAwait(false)
                    : await _Probe(endpoint, token).ConfigureAwait(false);
                probed++;
                await PersistProbeAsync(endpoint, result, token).ConfigureAwait(false);
            }

            _Logging.Debug(_Header + "health sweep probed " + probed + " endpoint(s)");
            return probed;
        }

        /// <summary>
        /// Run a health sweep requested through the API. Only a global administrator may observe or trigger a
        /// sweep because it probes endpoints across all tenants.
        /// </summary>
        /// <param name="auth">Authentication context.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of endpoints probed.</returns>
        public Task<int> CheckHealthAllAsync(AuthContext auth, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            RequireAuthenticated(auth, true);
            if (!auth.IsAdmin) throw new UnauthorizedAccessException("Global administrator access is required for a health sweep.");
            return CheckHealthAllAsync(token);
        }

        /// <summary>
        /// Normalize a base URL for display and compatibility with existing callers. Health probes never use
        /// this value to share clients or credentials.
        /// </summary>
        /// <param name="baseUrl">Raw base URL.</param>
        /// <returns>Normalized key.</returns>
        public static string NormalizeBaseUrl(string? baseUrl)
        {
            if (String.IsNullOrWhiteSpace(baseUrl)) return String.Empty;
            string trimmed = baseUrl.Trim();
            Uri? uri;
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out uri))
            {
                string scheme = uri.Scheme.ToLowerInvariant();
                string host = uri.Host.ToLowerInvariant();
                string port = uri.IsDefaultPort ? String.Empty : ":" + uri.Port;
                string path = uri.AbsolutePath.TrimEnd('/');
                return scheme + "://" + host + port + path;
            }

            return trimmed.TrimEnd('/').ToLowerInvariant();
        }

        #endregion

        #region Private-Methods

        private static void RequireAuthenticated(AuthContext auth, bool allowGlobalAdminWithoutTenant)
        {
            if (!auth.IsAuthenticated) throw new UnauthorizedAccessException("Authentication required.");
            if (allowGlobalAdminWithoutTenant && auth.IsAdmin) return;
            if (String.IsNullOrWhiteSpace(auth.TenantId) || String.IsNullOrWhiteSpace(auth.UserId))
                throw new UnauthorizedAccessException("Authenticated tenant and user are required.");
        }

        private static void RequireTenantUser(AuthContext auth)
        {
            RequireAuthenticated(auth, false);
        }

        private static bool IsVisible(AuthContext auth, ModelEndpoint endpoint)
        {
            if (auth.IsAdmin) return true;
            if (!String.Equals(auth.TenantId, endpoint.TenantId, StringComparison.Ordinal)) return false;
            if (endpoint.Scope == ScopeEnum.TenantWide) return true;
            if (auth.IsTenantAdmin) return true;
            return String.Equals(auth.UserId, endpoint.UserId, StringComparison.Ordinal);
        }

        private static bool CanEdit(AuthContext auth, ModelEndpoint endpoint)
        {
            if (!IsVisible(auth, endpoint)) return false;
            if (auth.IsAdmin || auth.IsTenantAdmin) return true;
            return endpoint.Scope == ScopeEnum.UserSpecific
                && String.Equals(auth.UserId, endpoint.UserId, StringComparison.Ordinal);
        }

        private static void ValidateShape(ModelEndpoint endpoint)
        {
            if (String.IsNullOrWhiteSpace(endpoint.Name)) throw new ArgumentException("Endpoint name is required.");
            if (String.IsNullOrWhiteSpace(endpoint.BaseUrl)) throw new ArgumentException("Endpoint base URL is required.");
            if (!Enum.IsDefined(endpoint.Provider)) throw new ArgumentException("Unknown model endpoint provider.");
            if (!Enum.IsDefined(endpoint.Kind)) throw new ArgumentException("Unknown model endpoint kind.");
            if (!Enum.IsDefined(endpoint.Scope)) throw new ArgumentException("Unknown model endpoint scope.");
            if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !String.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("Endpoint base URL must be an absolute HTTP(S) URL without user information.");

            string? reason = ModelEndpointClientFactory.UnsupportedReason(endpoint.Provider, endpoint.Kind);
            if (reason != null) throw new ArgumentException(reason);
        }

        private async Task<ModelEndpointProbeResult> ValidateEndpointAsync(ModelEndpoint endpoint, CancellationToken token)
        {
            ModelEndpointProbeResult result = new ModelEndpointProbeResult();
            result.BaseUrl = endpoint.BaseUrl;
            Stopwatch sw = Stopwatch.StartNew();

            try { result = await ValidateModelHttpAsync(endpoint, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                sw.Stop();
                result.LatencyMs = sw.ElapsedMilliseconds;
                result.Success = false;
                result.Error = SafeProbeError(e);
                _Logging.Warn(_Header + "validation of endpoint " + endpoint.Id + " failed");
            }

            return result;
        }

        private async Task PersistProbeAsync(ModelEndpoint endpoint, ModelEndpointProbeResult result, CancellationToken token)
        {
            // The probe ran against the snapshot supplied by the caller. If configuration changed while the
            // request was in flight, its result belongs to the old URL/credential/model and must not be
            // attached to the new configuration.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                ModelEndpoint? latest = await _Database.ModelEndpoints.ReadAsync(endpoint.Id, token).ConfigureAwait(false);
                if (latest == null) return;
                if (HasConfigurationChanged(endpoint, latest))
                {
                    _Logging.Debug(_Header + "discarding stale health result for endpoint " + endpoint.Id);
                    return;
                }
                if (latest.LastHealthCheckUtc.HasValue && result.TimestampUtc < latest.LastHealthCheckUtc.Value)
                {
                    _Logging.Debug(_Header + "discarding out-of-order health result for endpoint " + endpoint.Id);
                    return;
                }
                DateTime expectedLastUpdateUtc = latest.LastUpdateUtc;
                latest.HealthStatus = result.Success ? EndpointHealthStatusEnum.Healthy : EndpointHealthStatusEnum.Unhealthy;
                latest.LastHealthCheckUtc = result.TimestampUtc;
                latest.LastHealthError = result.Success ? null : SanitizeProbeError(result.Error);
                latest.LastLatencyMs = result.LatencyMs;
                latest.HealthHistory.Add(new ModelEndpointHealthRecord
                {
                    TimestampUtc = result.TimestampUtc,
                    Success = result.Success
                });
                while (latest.HealthHistory.Count > 24) latest.HealthHistory.RemoveAt(0);
                latest.LastUpdateUtc = DateTime.UtcNow;
                if (await _Database.ModelEndpoints.UpdateHealthAsync(latest, expectedLastUpdateUtc, token).ConfigureAwait(false)) return;
            }

            _Logging.Warn(_Header + "health result was superseded by a configuration update for endpoint " + endpoint.Id);
        }

        private static bool HasConfigurationChanged(ModelEndpoint before, ModelEndpoint after)
        {
            return !String.Equals(before.TenantId, after.TenantId, StringComparison.Ordinal)
                || !String.Equals(before.UserId, after.UserId, StringComparison.Ordinal)
                || !String.Equals(before.Name, after.Name, StringComparison.Ordinal)
                || before.Kind != after.Kind
                || before.Scope != after.Scope
                || before.Provider != after.Provider
                || !String.Equals(before.BaseUrl, after.BaseUrl, StringComparison.Ordinal)
                || !String.Equals(before.ApiKey, after.ApiKey, StringComparison.Ordinal)
                || !String.Equals(before.Model, after.Model, StringComparison.Ordinal)
                || before.Dimensionality != after.Dimensionality
                || before.TimeoutMs != after.TimeoutMs
                || before.Enabled != after.Enabled;
        }

        private static string SafeProbeError(Exception exception)
        {
            return "Provider probe failed: " + exception.GetType().Name;
        }

        private static string? SanitizeProbeError(string? error)
        {
            // The probe builds its own safe, provider-agnostic reason strings (an HTTP status, a parse
            // outcome, or an exception type name) and never puts a URL or key in them, so the specific
            // reason is surfaced instead of a generic message. The length cap guards a future source.
            if (String.IsNullOrWhiteSpace(error)) return "Provider probe failed.";
            string trimmed = error.Trim();
            return trimmed.Length > 200 ? trimmed.Substring(0, 200) : trimmed;
        }

        private static bool IsReferenceConstraintViolation(Exception exception)
        {
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                // Match provider error codes. Provider messages are localized and can contain
                // unrelated text, so they are not a reliable deletion-in-use signal.
                if (current is Microsoft.Data.Sqlite.SqliteException sqlite
                    && sqlite.SqliteErrorCode == 19
                    && (sqlite.SqliteExtendedErrorCode == 787
                        || sqlite.SqliteExtendedErrorCode == 1811))
                    return true;
                if (current is Npgsql.PostgresException postgres && postgres.SqlState == "23503")
                    return true;
                if (current is MySqlConnector.MySqlException mysql && mysql.Number == 1451)
                    return true;
                if (current is Microsoft.Data.SqlClient.SqlException sqlServer
                    && sqlServer.Number == 547)
                    return true;
            }

            return false;
        }

        private static async Task<ModelEndpointProbeResult> ValidateModelHttpAsync(ModelEndpoint endpoint, CancellationToken token)
        {
            ModelEndpointProbeResult result = new ModelEndpointProbeResult { BaseUrl = endpoint.BaseUrl };
            using (HttpClient client = ModelEndpointClientFactory.CreateHttpClient(endpoint))
            using (HttpRequestMessage request = ModelEndpointClientFactory.CreateValidationRequest(endpoint))
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(endpoint.TimeoutMs);
                Stopwatch stopwatch = Stopwatch.StartNew();
                using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                {
                    result.LatencyMs = stopwatch.ElapsedMilliseconds;
                    result.StatusCode = (int)response.StatusCode;
                    BoundedResponseBody body = await ReadResponseBodyAsync(response.Content, 65536, timeout.Token).ConfigureAwait(false);
                    if (body.TooLarge)
                    {
                        result.Error = "Provider validation response was too large.";
                        return result;
                    }
                    if (body.Text == null)
                    {
                        result.Error = "Provider returned an invalid validation response.";
                        return result;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        result.Error = "Provider returned HTTP status " + (int)response.StatusCode + ".";
                        return result;
                    }

                    try
                    {
                        JsonSerializerOptions options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        if (endpoint.Kind == ModelEndpointKindEnum.Embedding)
                        {
                            int dimensions;
                            if (endpoint.Provider == ModelProviderEnum.Gemini)
                            {
                                GeminiEmbeddingResponseDto? embedding = JsonSerializer.Deserialize<GeminiEmbeddingResponseDto>(body.Text, options);
                                dimensions = embedding?.Embedding?.Values?.Count ?? 0;
                            }
                            else
                            {
                                NumericEmbeddingResponseDto? embedding = JsonSerializer.Deserialize<NumericEmbeddingResponseDto>(body.Text, options);
                                dimensions = ReadNumericEmbeddingDimensions(embedding);
                            }
                            if (dimensions == 0)
                            {
                                result.Error = "Provider response did not contain an embedding vector.";
                                return result;
                            }
                            if (endpoint.Dimensionality > 0 && endpoint.Dimensionality != dimensions)
                            {
                                result.Error = "Provider returned an embedding with an unexpected dimensionality.";
                                return result;
                            }
                            result.EmbeddingDimensions = dimensions;
                        }
                        else
                        {
                            string? text = DeserializeCompletionText(endpoint.Provider, body.Text, options);
                            if (String.IsNullOrWhiteSpace(text))
                            {
                                result.Error = "Provider response did not contain completion text.";
                                return result;
                            }
                            result.SampleText = text!.Length > 200 ? text.Substring(0, 200) : text;
                        }
                    }
                    catch (JsonException)
                    {
                        result.Error = "Provider returned an invalid validation response.";
                        return result;
                    }

                    result.Success = true;
                    return result;
                }
            }
        }

        private static async Task<BoundedResponseBody> ReadResponseBodyAsync(HttpContent content, int maximumBytes, CancellationToken token)
        {
            using (Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false))
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[8192];
                while (true)
                {
                    int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (buffer.Length + read > maximumBytes)
                        return new BoundedResponseBody(null, true);
                    buffer.Write(chunk, 0, read);
                }

                try { return new BoundedResponseBody(new UTF8Encoding(false, true).GetString(buffer.ToArray()), false); }
                catch (DecoderFallbackException) { return new BoundedResponseBody(null, false); }
            }
        }

        private static int ReadNumericEmbeddingDimensions(NumericEmbeddingResponseDto? response)
        {
            List<double>? values = response?.Embedding
                ?? response?.Data?.FirstOrDefault()?.Embedding;
            return values?.Count ?? 0;
        }

        private static string? DeserializeCompletionText(ModelProviderEnum provider, string body, JsonSerializerOptions options)
        {
            return provider switch
            {
                ModelProviderEnum.Ollama => JsonSerializer.Deserialize<OllamaCompletionResponseDto>(body, options)?.Message?.Content,
                ModelProviderEnum.OpenAI or ModelProviderEnum.OpenAICompatible => JsonSerializer.Deserialize<OpenAiCompletionResponseDto>(body, options)?.Choices?.FirstOrDefault()?.Message?.Content,
                ModelProviderEnum.Anthropic => JsonSerializer.Deserialize<AnthropicCompletionResponseDto>(body, options)?.Content?.Select(block => block.Text).FirstOrDefault(value => !String.IsNullOrWhiteSpace(value)),
                ModelProviderEnum.Gemini => JsonSerializer.Deserialize<GeminiCompletionResponseDto>(body, options)?.Candidates?.SelectMany(candidate => candidate.Content?.Parts ?? Enumerable.Empty<CompletionPartDto>()).Select(part => part.Text).FirstOrDefault(value => !String.IsNullOrWhiteSpace(value)),
                _ => null
            };
        }

        private sealed class BoundedResponseBody
        {
            internal BoundedResponseBody(string? text, bool tooLarge) { Text = text; TooLarge = tooLarge; }
            internal string? Text { get; }
            internal bool TooLarge { get; }
        }

        private sealed class NumericEmbeddingResponseDto
        {
            public List<double>? Embedding { get; set; }
            public List<EmbeddingDataDto>? Data { get; set; }
        }

        private sealed class GeminiEmbeddingResponseDto
        {
            public GeminiEmbeddingDto? Embedding { get; set; }
        }

        private sealed class GeminiEmbeddingDto { public List<double>? Values { get; set; } }

        private sealed class EmbeddingDataDto
        {
            public List<double>? Embedding { get; set; }
        }

        private sealed class OllamaCompletionResponseDto { public CompletionMessageDto? Message { get; set; } }
        private sealed class OpenAiCompletionResponseDto { public List<CompletionChoiceDto>? Choices { get; set; } }
        private sealed class AnthropicCompletionResponseDto { public List<CompletionBlockDto>? Content { get; set; } }
        private sealed class GeminiCompletionResponseDto { public List<CompletionCandidateDto>? Candidates { get; set; } }

        private sealed class CompletionChoiceDto { public CompletionMessageDto? Message { get; set; } }
        private sealed class CompletionMessageDto { public string? Content { get; set; } }
        private sealed class CompletionBlockDto { public string? Text { get; set; } }
        private sealed class CompletionCandidateDto { public CompletionContentDto? Content { get; set; } }
        private sealed class CompletionContentDto { public List<CompletionPartDto>? Parts { get; set; } }
        private sealed class CompletionPartDto { public string? Text { get; set; } }

        #endregion
    }
}
