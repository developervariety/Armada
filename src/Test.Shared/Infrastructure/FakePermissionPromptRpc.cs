namespace Test.Shared.Infrastructure
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// A JSON-RPC request read by <see cref="FakePermissionPromptMcpServer"/>.
    /// </summary>
    public sealed class FakePermissionPromptRpc
    {
        /// <summary>
        /// Request id (absent on notifications).
        /// </summary>
        [JsonPropertyName("id")]
        public int? Id { get; set; } = null;

        /// <summary>
        /// Method name.
        /// </summary>
        [JsonPropertyName("method")]
        public string Method { get; set; } = "";

        /// <summary>
        /// tools/call parameters.
        /// </summary>
        [JsonPropertyName("params")]
        public FakePermissionPromptRpcParams? Params { get; set; } = null;
    }
}
