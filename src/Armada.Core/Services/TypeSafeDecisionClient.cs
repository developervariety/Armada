namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Typed-decision client for the TypeSafe Jev provider. POSTs one request to
    /// <c>{BaseUrl}/v1/systemone</c> with the Bearer key read from the environment, and returns
    /// typed answers. There are no retries in the hot path; every timeout, non-2xx, or parse
    /// failure returns <see cref="TypedDecisionResult.Available"/> false with a reason, and the
    /// client never throws into a caller. A decision slower than the settings timeout is
    /// unavailable, not late.
    /// </summary>
    public sealed class TypeSafeDecisionClient : ITypedDecisionClient
    {
        #region Private-Members

        private const string _Header = "[TypeSafeDecisionClient] ";
        private const int _MaxErrorBodyBytes = 4096;
        private const int _MaxErrorDetailChars = 300;
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly TypedDecisionSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly HttpClient _Http;
        private readonly Func<string?>? _ApiKeyProvider;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a TypeSafe typed-decision client.
        /// </summary>
        /// <param name="settings">Typed-decision settings (base URL, model, key env, timeout).</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="httpClient">HTTP client used for the single POST.</param>
        public TypeSafeDecisionClient(TypedDecisionSettings settings, LoggingModule logging, HttpClient httpClient)
            : this(settings, logging, httpClient, null)
        {
        }

        /// <summary>
        /// Create a TypeSafe typed-decision client whose Bearer key comes from a provider, read on every call.
        /// </summary>
        /// <param name="settings">Typed-decision settings (base URL, model, timeout).</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="httpClient">HTTP client used for the single POST.</param>
        /// <param name="apiKeyProvider">Returns the current key; null reads the environment variable named in settings.</param>
        public TypeSafeDecisionClient(TypedDecisionSettings settings, LoggingModule logging, HttpClient httpClient, Func<string?>? apiKeyProvider)
        {
            _ApiKeyProvider = apiKeyProvider;
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<TypedDecisionResult> DecideAsync(TypedDecisionRequest request, CancellationToken token)
        {
            if (request == null)
                return Unavailable("exception", 0);

            Stopwatch stopwatch = Stopwatch.StartNew();

            // The timeout is enforced by a linked, self-cancelling token so a slow provider never
            // stalls the caller beyond the settings budget. Cancellation the caller requested and
            // the timeout both return the reason "timeout"; only the elapsed timeout logs a warning.
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            linked.CancelAfter(TimeSpan.FromSeconds(_Settings.TimeoutSeconds));

            try
            {
                string endpoint = _Settings.BaseUrl.TrimEnd('/') + "/v1/systemone";
                WireRequest payload = BuildRequest(request);

                using HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, endpoint);
                string? apiKey = ResolveApiKey();
                if (!String.IsNullOrWhiteSpace(apiKey))
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                message.Content = new StringContent(
                    JsonSerializer.Serialize(payload, _JsonOptions),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response = await _Http.SendAsync(message, linked.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    string reason = "http_" + (int)response.StatusCode;
                    string? detail = await ReadErrorDetailAsync(response, apiKey, linked.Token).ConfigureAwait(false);
                    _Logging.Warn(_Header + "decision '" + request.DecisionPoint + "' unavailable: " + reason
                        + (detail != null ? " (" + detail + ")" : String.Empty));
                    return Unavailable(reason, stopwatch.ElapsedMilliseconds, detail);
                }

                using Stream responseStream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                WireResponse? parsed = await JsonSerializer.DeserializeAsync<WireResponse>(responseStream, _JsonOptions, linked.Token).ConfigureAwait(false);
                if (parsed == null)
                {
                    _Logging.Warn(_Header + "decision '" + request.DecisionPoint + "' unavailable: parse (null body)");
                    return Unavailable("parse", stopwatch.ElapsedMilliseconds);
                }

                return BuildResult(parsed, stopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The caller cancelled; surface it as unavailable (reason "timeout") rather than throwing into the caller.
                return Unavailable("timeout", stopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                // The linked token fired: the settings timeout elapsed.
                _Logging.Warn(_Header + "decision '" + request.DecisionPoint + "' unavailable: timeout after " + _Settings.TimeoutSeconds + "s");
                return Unavailable("timeout", stopwatch.ElapsedMilliseconds);
            }
            catch (JsonException)
            {
                _Logging.Warn(_Header + "decision '" + request.DecisionPoint + "' unavailable: parse");
                return Unavailable("parse", stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "decision '" + request.DecisionPoint + "' unavailable: exception: " + ex.Message);
                return Unavailable("exception", stopwatch.ElapsedMilliseconds);
            }
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Read the provider's explanation from a rejected response. A 422 names the invalid field, and
        /// without it a malformed question looks exactly like an outage. The body is read up to a small
        /// cap, reduced to its message text, stripped of the key, redacted, and truncated. Returns null
        /// when the body is empty or carries no recognised message; never throws.
        /// </summary>
        private static async Task<string?> ReadErrorDetailAsync(HttpResponseMessage response, string? apiKey, CancellationToken token)
        {
            try
            {
                string body;
                using (Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                {
                    byte[] buffer = new byte[_MaxErrorBodyBytes];
                    int total = 0;
                    int read;
                    while (total < buffer.Length && (read = await stream.ReadAsync(buffer, total, buffer.Length - total, token).ConfigureAwait(false)) > 0)
                        total += read;
                    body = Encoding.UTF8.GetString(buffer, 0, total);
                }

                if (String.IsNullOrWhiteSpace(body)) return null;

                string? text = ExtractErrorText(body);
                if (String.IsNullOrWhiteSpace(text)) return null;

                if (!String.IsNullOrWhiteSpace(apiKey)) text = text.Replace(apiKey, "<secret>", StringComparison.Ordinal);
                string redacted = DecisionStateRedactor.Redact(text.Trim(), _MaxErrorDetailChars);
                return String.IsNullOrWhiteSpace(redacted) ? null : redacted;
            }
            catch (Exception)
            {
                // The explanation is diagnostic only; failing to read it must not change the result.
                return null;
            }
        }

        private static string? ExtractErrorText(string body)
        {
            // Validation errors arrive as a list of { loc, msg }; other errors as a single string field.
            try
            {
                WireValidationError? validation = JsonSerializer.Deserialize<WireValidationError>(body, _JsonOptions);
                if (validation?.Detail != null && validation.Detail.Count > 0)
                {
                    List<string> parts = new List<string>();
                    foreach (WireValidationItem item in validation.Detail)
                    {
                        if (item == null || String.IsNullOrWhiteSpace(item.Msg)) continue;
                        // Joined with " > ", not ".", so the redactor's hostname rule does not read a
                        // dotted field path as a host and erase it.
                        string location = item.Loc != null && item.Loc.Count > 0
                            ? String.Join(" > ", item.Loc.Select(segment => segment?.ToString() ?? String.Empty)) + ": "
                            : String.Empty;
                        parts.Add(location + item.Msg);
                    }
                    if (parts.Count > 0) return String.Join("; ", parts);
                }
            }
            catch (JsonException)
            {
                // Not the validation shape; try the single-message shape.
            }

            try
            {
                WireErrorMessage? message = JsonSerializer.Deserialize<WireErrorMessage>(body, _JsonOptions);
                return message?.Detail ?? message?.Error ?? message?.Message;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private string? ResolveApiKey()
        {
            if (_ApiKeyProvider != null) return _ApiKeyProvider();
            if (String.IsNullOrWhiteSpace(_Settings.ApiKeyEnv)) return null;
            return Environment.GetEnvironmentVariable(_Settings.ApiKeyEnv);
        }

        private WireRequest BuildRequest(TypedDecisionRequest request)
        {
            Dictionary<string, WireQuestion> questions = new Dictionary<string, WireQuestion>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, TypedQuestion> entry in request.Questions)
            {
                questions[entry.Key] = ToWireQuestion(entry.Value);
            }

            return new WireRequest
            {
                Model = _Settings.Model,
                State = request.State,
                Questions = questions
            };
        }

        private static WireQuestion ToWireQuestion(TypedQuestion question)
        {
            switch (question)
            {
                case ChoiceQuestion choice:
                    return new WireQuestion
                    {
                        Type = "choice",
                        Instructions = choice.Instructions,
                        Criteria = choice.Criteria
                    };
                case ScoreQuestion score:
                    return new WireQuestion
                    {
                        Type = "score",
                        Instructions = score.Instructions,
                        Criteria = score.Levels
                    };
                case NoulQuestion noul:
                    Dictionary<string, string>? poles = null;
                    if (noul.TrueMeaning != null || noul.FalseMeaning != null)
                    {
                        poles = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (noul.TrueMeaning != null) poles["true"] = noul.TrueMeaning;
                        if (noul.FalseMeaning != null) poles["false"] = noul.FalseMeaning;
                    }
                    return new WireQuestion
                    {
                        Type = "noul",
                        Instructions = noul.Instructions,
                        Criteria = poles
                    };
                default:
                    return new WireQuestion
                    {
                        Type = "noul",
                        Instructions = question.Instructions,
                        Criteria = null
                    };
            }
        }

        private static TypedDecisionResult BuildResult(WireResponse parsed, long latencyMs)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal);
            if (parsed.Answers != null)
            {
                foreach (KeyValuePair<string, WireAnswer> entry in parsed.Answers)
                {
                    WireAnswer wire = entry.Value;
                    if (wire == null) continue;
                    answers[entry.Key] = new TypedAnswer
                    {
                        Type = String.IsNullOrWhiteSpace(wire.Type) ? "noul" : wire.Type,
                        Choice = wire.Choice,
                        Score = wire.Score,
                        Noul = wire.Noul,
                        Probabilities = wire.Probabilities,
                        Confidence = wire.Confidence
                    };
                }
            }

            return new TypedDecisionResult
            {
                Available = true,
                UnavailableReason = null,
                Answers = answers,
                Model = parsed.Model,
                InputTokens = parsed.Usage?.InputTokens ?? 0,
                OutputTokens = parsed.Usage?.OutputTokens ?? 0,
                LatencyMs = latencyMs
            };
        }

        private static TypedDecisionResult Unavailable(string reason, long latencyMs, string? detail = null)
        {
            return new TypedDecisionResult
            {
                Available = false,
                UnavailableReason = reason,
                UnavailableDetail = detail,
                Answers = new Dictionary<string, TypedAnswer>(),
                LatencyMs = latencyMs
            };
        }

        #endregion

        #region Private-Types

        private sealed class WireRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = String.Empty;

            [JsonPropertyName("state")]
            public object State { get; set; } = String.Empty;

            [JsonPropertyName("questions")]
            public Dictionary<string, WireQuestion> Questions { get; set; } = new Dictionary<string, WireQuestion>();
        }

        private sealed class WireQuestion
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = String.Empty;

            [JsonPropertyName("instructions")]
            public string Instructions { get; set; } = String.Empty;

            [JsonPropertyName("criteria")]
            public object? Criteria { get; set; }
        }

        private sealed class WireResponse
        {
            [JsonPropertyName("model")]
            public string? Model { get; set; }

            [JsonPropertyName("answers")]
            public Dictionary<string, WireAnswer>? Answers { get; set; }

            [JsonPropertyName("usage")]
            public WireUsage? Usage { get; set; }
        }

        private sealed class WireAnswer
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("choice")]
            public string? Choice { get; set; }

            [JsonPropertyName("probabilities")]
            public Dictionary<string, double>? Probabilities { get; set; }

            [JsonPropertyName("score")]
            public double? Score { get; set; }

            // The provider sends the score legend as an index-keyed object ({"0": "Low", ...}), not a
            // list. A list shape here makes every response that carries a score answer a parse failure.
            [JsonPropertyName("legend")]
            public Dictionary<string, string>? Legend { get; set; }

            [JsonPropertyName("noul")]
            public double? Noul { get; set; }

            [JsonPropertyName("confidence")]
            public double? Confidence { get; set; }
        }

        private sealed class WireValidationError
        {
            [JsonPropertyName("detail")]
            public List<WireValidationItem>? Detail { get; set; }
        }

        private sealed class WireValidationItem
        {
            [JsonPropertyName("loc")]
            public List<object>? Loc { get; set; }

            [JsonPropertyName("msg")]
            public string? Msg { get; set; }
        }

        private sealed class WireErrorMessage
        {
            [JsonPropertyName("detail")]
            public string? Detail { get; set; }

            [JsonPropertyName("error")]
            public string? Error { get; set; }

            [JsonPropertyName("message")]
            public string? Message { get; set; }
        }

        private sealed class WireUsage
        {
            [JsonPropertyName("input_tokens")]
            public int InputTokens { get; set; }

            [JsonPropertyName("output_tokens")]
            public int OutputTokens { get; set; }
        }

        #endregion
    }
}
