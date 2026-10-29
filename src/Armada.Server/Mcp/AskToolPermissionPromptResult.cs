namespace Armada.Server.Mcp
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The answer to a Claude Code permission prompt: <c>{"behavior":"allow","updatedInput":{...}}</c> or
    /// <c>{"behavior":"deny","message":"..."}</c>.
    /// </summary>
    public sealed class AskToolPermissionPromptResult
    {
        /// <summary>Behavior value that lets the tool call run.</summary>
        public const string AllowBehavior = "allow";

        /// <summary>Behavior value that refuses the tool call.</summary>
        public const string DenyBehavior = "deny";

        /// <summary>Either <see cref="AllowBehavior"/> or <see cref="DenyBehavior"/>.</summary>
        [JsonPropertyName("behavior")]
        public string Behavior { get; set; } = DenyBehavior;

        /// <summary>The tool input to run with, sent on allow.</summary>
        [JsonPropertyName("updatedInput")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? UpdatedInput { get; set; }

        /// <summary>The reason shown to the model, sent on deny.</summary>
        [JsonPropertyName("message")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Message { get; set; }

        /// <summary>Allow the call with its original input.</summary>
        /// <param name="inputJson">The original input JSON; an empty or invalid value runs with an empty input.</param>
        /// <returns>The allow answer.</returns>
        public static AskToolPermissionPromptResult Allow(string? inputJson)
        {
            JsonElement input;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson))
                    input = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                using (JsonDocument document = JsonDocument.Parse("{}")) input = document.RootElement.Clone();
            }
            return new AskToolPermissionPromptResult { Behavior = AllowBehavior, UpdatedInput = input };
        }

        /// <summary>Refuse the call.</summary>
        /// <param name="message">Reason shown to the model.</param>
        /// <returns>The deny answer.</returns>
        public static AskToolPermissionPromptResult Deny(string message)
        {
            return new AskToolPermissionPromptResult { Behavior = DenyBehavior, Message = message };
        }
    }
}
