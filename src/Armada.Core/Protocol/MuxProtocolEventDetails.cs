namespace Armada.Core.Protocol
{
    using System;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The fields of a Mux JSONL protocol event (<c>--output-format jsonl</c>, contract version 2) that
    /// <see cref="MuxProtocolEvent"/> does not carry: the endpoint and iteration limit of <c>run_started</c>, the failure
    /// category of <c>error</c>, the error count of <c>run_completed</c>, the context figures of <c>context_status</c> and
    /// <c>context_compacted</c>, the task counts of <c>task_plan_updated</c>, and the fields of <c>hook</c>. Deserialized
    /// from the same line as the <see cref="MuxProtocolEvent"/>; every field is null when the event does not carry it.
    /// </summary>
    public class MuxProtocolEventDetails
    {
        #region Public-Members

        /// <summary>
        /// hook (a plugin hook ran). Not one of <see cref="MuxProtocolEvent.KnownEventTypes"/>.
        /// </summary>
        public const string Hook = "hook";

        /// <summary>
        /// Event type.
        /// </summary>
        [JsonPropertyName("eventType")]
        public string? EventType { get; set; } = null;

        /// <summary>
        /// Endpoint name (run_started, error), or null.
        /// </summary>
        [JsonPropertyName("endpointName")]
        public string? EndpointName { get; set; } = null;

        /// <summary>
        /// The run's iteration limit (run_started), or null.
        /// </summary>
        [JsonPropertyName("maxIterations")]
        public int? MaxIterations { get; set; } = null;

        /// <summary>
        /// Failure category of an error event (for example approval, tool, network, backend, runtime), or null.
        /// </summary>
        [JsonPropertyName("failureCategory")]
        public string? FailureCategory { get; set; } = null;

        /// <summary>
        /// Error events the run emitted (run_completed), or null.
        /// </summary>
        [JsonPropertyName("errorCount")]
        public int? ErrorCount { get; set; } = null;

        /// <summary>
        /// Context warning level (context_status): none, approaching, or critical; or null.
        /// </summary>
        [JsonPropertyName("warningLevel")]
        public string? WarningLevel { get; set; } = null;

        /// <summary>
        /// Estimated context tokens (context_status), or null.
        /// </summary>
        [JsonPropertyName("estimatedTokens")]
        public long? EstimatedTokens { get; set; } = null;

        /// <summary>
        /// Usable input limit in tokens (context_status), or null.
        /// </summary>
        [JsonPropertyName("usableInputLimit")]
        public long? UsableInputLimit { get; set; } = null;

        /// <summary>
        /// Remaining context tokens (context_status), or null.
        /// </summary>
        [JsonPropertyName("remainingTokens")]
        public long? RemainingTokens { get; set; } = null;

        /// <summary>
        /// Compaction strategy (context_compacted), or null.
        /// </summary>
        [JsonPropertyName("strategy")]
        public string? Strategy { get; set; } = null;

        /// <summary>
        /// Estimated tokens before a compaction (context_compacted), or null.
        /// </summary>
        [JsonPropertyName("estimatedTokensBefore")]
        public long? EstimatedTokensBefore { get; set; } = null;

        /// <summary>
        /// Estimated tokens after a compaction (context_compacted), or null.
        /// </summary>
        [JsonPropertyName("estimatedTokensAfter")]
        public long? EstimatedTokensAfter { get; set; } = null;

        /// <summary>
        /// Tasks in the plan (task_plan_updated), or null.
        /// </summary>
        [JsonPropertyName("totalCount")]
        public int? TotalCount { get; set; } = null;

        /// <summary>
        /// Completed tasks in the plan (task_plan_updated), or null.
        /// </summary>
        [JsonPropertyName("completedCount")]
        public int? CompletedCount { get; set; } = null;

        /// <summary>
        /// Hook event name (hook), or null.
        /// </summary>
        [JsonPropertyName("hookEvent")]
        public string? HookEvent { get; set; } = null;

        /// <summary>
        /// Hook name (hook), or null.
        /// </summary>
        [JsonPropertyName("hookName")]
        public string? HookName { get; set; } = null;

        /// <summary>
        /// Hook outcome (hook), or null.
        /// </summary>
        [JsonPropertyName("outcome")]
        public string? Outcome { get; set; } = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Parse one output line. Returns false for plain text, malformed JSON, and JSON without an <c>eventType</c>.
        /// Callers decide which event types they accept.
        /// </summary>
        /// <param name="line">Raw line.</param>
        /// <param name="value">The details, or null.</param>
        /// <returns>True when the line is a JSON object with an event type.</returns>
        public static bool TryParse(string? line, out MuxProtocolEventDetails? value)
        {
            if (RuntimeStreamJson.TryDeserializeLine<MuxProtocolEventDetails>(line, out value)
                && value != null && !String.IsNullOrEmpty(value.EventType))
                return true;

            value = null;
            return false;
        }

        #endregion
    }
}
