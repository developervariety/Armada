namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// Best-effort recording of token usage. Every conversational and mission surface funnels through
    /// <see cref="CaptureAsync"/>, which normalizes the counts (using real provider-reported numbers when
    /// available and estimating from text length otherwise, flagging estimates), then writes one
    /// <see cref="TokenUsageRecord"/>. Recording never throws: a persistence failure is logged and
    /// swallowed so token accounting can never break a chat turn or a mission run.
    ///
    /// Token semantics: <c>input</c> covers prompt tokens, <c>output</c> covers completion tokens,
    /// <c>cached</c> is the cache-read subset of input (informational, shown as its own series), and
    /// <c>total</c> is input + output.
    /// </summary>
    public static class TokenUsageCapture
    {
        #region Private-Members

        // Approximate characters per token (~3.5), matching ChatTurnMetricsBuilder, used only to estimate
        // counts when a runtime does not report real usage.
        private const double _CharsPerToken = 3.5;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record one token-usage observation. Provide real counts where a runtime reports them; pass null
        /// for a count to have it estimated from the corresponding text. No record is written when the
        /// observation carries no tokens at all.
        /// </summary>
        /// <param name="database">Database driver used to persist the record.</param>
        /// <param name="logging">Logging module for best-effort failure reporting.</param>
        /// <param name="source">Work surface: "mission", "chat", or "planning".</param>
        /// <param name="model">Model identifier; falls back to the runtime name and then "unknown".</param>
        /// <param name="runtime">Agent runtime name.</param>
        /// <param name="tenantId">Owning tenant, when known.</param>
        /// <param name="userId">User the work was for, when known.</param>
        /// <param name="vesselId">Vessel, when known.</param>
        /// <param name="captainId">Captain, when known.</param>
        /// <param name="sourceId">Originating unit of work id (mission/session), when known.</param>
        /// <param name="inputTokens">Real input (prompt) tokens, or null to estimate from inputText.</param>
        /// <param name="outputTokens">Real output (completion) tokens, or null to estimate from outputText.</param>
        /// <param name="cachedTokens">Real cache-read tokens, or null for 0.</param>
        /// <param name="inputText">Text used to estimate input tokens when inputTokens is null.</param>
        /// <param name="outputText">Text used to estimate output tokens when outputTokens is null.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task CaptureAsync(
            DatabaseDriver database,
            LoggingModule logging,
            string source,
            string? model,
            string? runtime,
            string? tenantId,
            string? userId,
            string? vesselId,
            string? captainId,
            string? sourceId,
            long? inputTokens,
            long? outputTokens,
            long? cachedTokens,
            string? inputText,
            string? outputText,
            CancellationToken token = default)
        {
            try
            {
                if (database == null) return;

                bool estimated = false;

                long output;
                if (outputTokens.HasValue && outputTokens.Value >= 0)
                {
                    output = outputTokens.Value;
                }
                else
                {
                    output = EstimateTokens(outputText);
                    if (output > 0) estimated = true;
                }

                long input;
                if (inputTokens.HasValue && inputTokens.Value >= 0)
                {
                    input = inputTokens.Value;
                }
                else
                {
                    input = EstimateTokens(inputText);
                    if (input > 0) estimated = true;
                }

                long cached = cachedTokens.HasValue && cachedTokens.Value >= 0 ? cachedTokens.Value : 0;

                if (input <= 0 && output <= 0 && cached <= 0) return;

                TokenUsageRecord record = new TokenUsageRecord
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Model = ResolveModel(model, runtime),
                    Runtime = string.IsNullOrWhiteSpace(runtime) ? null : runtime,
                    Source = source,
                    SourceId = sourceId,
                    VesselId = vesselId,
                    CaptainId = captainId,
                    InputTokens = input,
                    OutputTokens = output,
                    CachedTokens = cached,
                    TotalTokens = input + output,
                    Estimated = estimated,
                    CreatedUtc = DateTime.UtcNow
                };

                await database.TokenUsage.CreateAsync(record, token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logging?.Warn("[TokenUsageCapture] failed to record token usage for " + source + ": " + e.Message);
            }
        }

        /// <summary>
        /// Estimate the number of tokens in a block of text (~3.5 characters per token). Returns 0 for
        /// null or empty text.
        /// </summary>
        /// <param name="text">Text to estimate.</param>
        /// <returns>Estimated token count.</returns>
        public static long EstimateTokens(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (long)Math.Round(text.Length / _CharsPerToken);
        }

        #endregion

        #region Private-Methods

        private static string ResolveModel(string? model, string? runtime)
        {
            if (!string.IsNullOrWhiteSpace(model)) return model.Trim();
            if (!string.IsNullOrWhiteSpace(runtime)) return runtime.Trim();
            return "unknown";
        }

        #endregion
    }
}
