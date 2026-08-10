namespace Armada.Server
{
    using System;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using PolyPrompt.Clients;
    using PolyPrompt.Models;
    using SyslogLogging;

    /// <summary>
    /// Runs an interactive chat turn directly against a captain's configured model using PolyPrompt,
    /// and reports the per-turn timing/token statistics (time to first token, generation time, and
    /// tokens per second) that PolyPrompt measures while streaming the reply.
    ///
    /// Direct chat requires a captain whose runtime exposes a model endpoint. Mux captains carry that
    /// (adapter type + base URL + model), so they are chattable; the CLI-only runtimes (Claude Code,
    /// Codex, Cursor) do not expose a direct chat API and return an explanatory error instead.
    /// </summary>
    public class CaptainChatService
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private readonly string _Header = "[CaptainChatService] ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        public CaptainChatService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Send one chat turn to a captain's model and return the reply plus per-turn metrics.
        /// </summary>
        /// <param name="captainId">Captain identifier (cpt_ prefix).</param>
        /// <param name="request">The new message and prior conversation.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The assistant reply and its timing/token statistics.</returns>
        public async Task<CaptainChatResponse> ChatAsync(string captainId, CaptainChatRequest request, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.Message)) return Fail("A message is required.");

            Captain? captain = await _Database.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
            if (captain == null) return Fail("Captain not found.");

            if (captain.Runtime != AgentRuntimeEnum.Mux)
                return Fail("Direct chat is available for Mux captains. The " + captain.Runtime + " runtime does not expose a chat endpoint.");

            MuxCaptainOptions? options = CaptainRuntimeOptions.GetMuxOptions(captain);
            string? baseUrl = options?.BaseUrl;
            if (String.IsNullOrWhiteSpace(baseUrl))
                return Fail("This captain has no model endpoint configured (set the Mux base URL to enable chat).");

            string model = captain.Model ?? "";
            string adapter = options?.AdapterType ?? "Ollama";

            CompletionClientBase? client = null;
            try
            {
                client = BuildClient(adapter, baseUrl!);
                if (client == null)
                    return Fail("Chat is not yet supported for the '" + adapter + "' adapter; only Ollama endpoints are chattable today.");

                if (!String.IsNullOrWhiteSpace(model)) client.Model = model;

                string prompt = BuildPrompt(captain, request);
                ChatCompletionOptions chatOptions = new ChatCompletionOptions();
                if (options?.Temperature != null) chatOptions.Temperature = options.Temperature;
                if (options?.MaxTokens != null) chatOptions.MaxTokens = options.MaxTokens;

                ChatStreamingResponse stream = await client.ChatStreamingAsync(prompt, chatOptions, token).ConfigureAwait(false);

                // Consuming the chunk stream is what populates the timing fields and accumulates the text.
                StringBuilder builder = new StringBuilder();
                await foreach (ChatStreamingChunk chunk in stream.Chunks.WithCancellation(token).ConfigureAwait(false))
                {
                    if (!String.IsNullOrEmpty(chunk.Text)) builder.Append(chunk.Text);
                }

                if (!stream.Success)
                    return Fail(stream.Error ?? "The model endpoint returned an error.");

                CaptainChatResponse response = new CaptainChatResponse
                {
                    Success = true,
                    Reply = builder.ToString().Trim(),
                    Model = String.IsNullOrEmpty(stream.Model) ? model : stream.Model,
                    Metrics = MapMetrics(stream),
                };

                _Logging.Debug(_Header + "chat turn for captain " + captainId + " (" + response.Model + "): " +
                    (response.Metrics.TokensPerSecond?.ToString("F1") ?? "?") + " tok/s, ttft " +
                    (response.Metrics.TimeToFirstTokenMs?.ToString("F0") ?? "?") + "ms");

                return response;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _Logging.Warn(_Header + "chat turn failed for captain " + captainId + ": " + e.Message);
                return Fail(e.Message);
            }
            finally
            {
                client?.Dispose();
            }
        }

        #endregion

        #region Private-Methods

        private CompletionClientBase? BuildClient(string adapter, string baseUrl)
        {
            // Only Ollama is wired for direct chat today; other adapters need credentials Armada does not
            // store on the captain, so they are declined with a clear message rather than failing opaquely.
            if (String.Equals(adapter, "Ollama", StringComparison.OrdinalIgnoreCase))
                return new OllamaClient(baseUrl, null, _Logging);

            return null;
        }

        private static string BuildPrompt(Captain captain, CaptainChatRequest request)
        {
            StringBuilder builder = new StringBuilder();

            if (!String.IsNullOrWhiteSpace(captain.SystemInstructions))
            {
                builder.AppendLine(captain.SystemInstructions.Trim());
                builder.AppendLine();
            }

            if (request.History != null && request.History.Count > 0)
            {
                foreach (CaptainChatMessage message in request.History)
                {
                    if (String.IsNullOrWhiteSpace(message.Content)) continue;
                    string speaker = String.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "Assistant" : "User";
                    builder.AppendLine(speaker + ": " + message.Content.Trim());
                }
                builder.AppendLine();
            }

            builder.Append("User: " + request.Message.Trim());
            return builder.ToString();
        }

        private static CaptainChatMetrics MapMetrics(ChatStreamingResponse stream)
        {
            CaptainChatMetrics metrics = new CaptainChatMetrics();

            if (stream.TimeToFirstTokenMs >= 0) metrics.TimeToFirstTokenMs = stream.TimeToFirstTokenMs;

            if (stream.TimeToLastTokenMs >= 0 && stream.TimeToFirstTokenMs >= 0)
                metrics.StreamingMs = stream.TimeToLastTokenMs - stream.TimeToFirstTokenMs;
            else if (stream.TimeToLastTokenMs >= 0)
                metrics.StreamingMs = stream.TimeToLastTokenMs;

            metrics.TotalMs = stream.OverallRuntimeMs;
            if (stream.OverallTokensPerSecond > 0) metrics.TokensPerSecond = stream.OverallTokensPerSecond;

            if (stream.Usage != null)
            {
                metrics.PromptTokens = stream.Usage.PromptTokens;
                metrics.CompletionTokens = stream.Usage.CompletionTokens;
                metrics.TotalTokens = stream.Usage.TotalTokens;

                // Fall back to a usage-derived tokens/sec if the streaming rate was not computed.
                if (metrics.TokensPerSecond == null && stream.Usage.CompletionTokens.HasValue &&
                    stream.Usage.EvalDurationNs.HasValue && stream.Usage.EvalDurationNs.Value > 0)
                {
                    metrics.TokensPerSecond = stream.Usage.CompletionTokens.Value /
                        (stream.Usage.EvalDurationNs.Value / 1_000_000_000.0);
                }
            }

            return metrics;
        }

        private static CaptainChatResponse Fail(string error)
        {
            return new CaptainChatResponse { Success = false, Error = error };
        }

        #endregion
    }
}
