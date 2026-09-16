namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>How an account login is completed.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AccountLoginMethodEnum
    {
        /// <summary>The owner opens a verification URL and confirms a user code (Codex, Cursor).</summary>
        [EnumMember(Value = "DeviceCode")]
        DeviceCode,

        /// <summary>The owner opens a sign-in URL and pastes the returned code back (Claude Code).</summary>
        [EnumMember(Value = "PasteCode")]
        PasteCode,

        /// <summary>The owner submits an API key once (OpenCode, Cursor).</summary>
        [EnumMember(Value = "ApiKey")]
        ApiKey
    }
}
