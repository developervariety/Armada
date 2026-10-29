namespace Test.Shared.Infrastructure
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// What the pi permission gate's <c>tool_call</c> handler returned to pi when it blocked a tool.
    /// </summary>
    public sealed class PiGateDecision
    {
        /// <summary>
        /// True when the tool is blocked.
        /// </summary>
        [JsonPropertyName("block")]
        public bool Block { get; set; } = false;

        /// <summary>
        /// The reason pi returns to the model as the tool's error result.
        /// </summary>
        [JsonPropertyName("reason")]
        public string? Reason { get; set; } = null;
    }
}
