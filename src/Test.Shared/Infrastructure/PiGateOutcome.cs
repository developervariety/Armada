namespace Test.Shared.Infrastructure
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// The pi permission gate's answer to one <see cref="PiGateEvent"/>: a null <see cref="Result"/> lets the tool run.
    /// </summary>
    public sealed class PiGateOutcome
    {
        /// <summary>
        /// Index of the event in the harness input.
        /// </summary>
        [JsonPropertyName("index")]
        public int Index { get; set; } = 0;

        /// <summary>
        /// The handler's return value, or null when the tool may run.
        /// </summary>
        [JsonPropertyName("result")]
        public PiGateDecision? Result { get; set; } = null;

        /// <summary>
        /// Milliseconds from the harness start until the handler resolved.
        /// </summary>
        [JsonPropertyName("finishedMs")]
        public long FinishedMs { get; set; } = 0;

        /// <summary>
        /// Whether pi would run the tool.
        /// </summary>
        [JsonIgnore]
        public bool Allowed => Result == null || !Result.Block;
    }
}
