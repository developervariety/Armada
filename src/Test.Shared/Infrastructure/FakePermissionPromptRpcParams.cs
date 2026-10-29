namespace Test.Shared.Infrastructure
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// tools/call parameters of a <see cref="FakePermissionPromptRpc"/>.
    /// </summary>
    public sealed class FakePermissionPromptRpcParams
    {
        /// <summary>
        /// Tool name.
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; } = null;

        /// <summary>
        /// cli_permission_prompt arguments.
        /// </summary>
        [JsonPropertyName("arguments")]
        public FakePermissionPromptArguments? Arguments { get; set; } = null;
    }
}
