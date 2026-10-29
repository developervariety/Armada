namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>What a remembered Ask tool permission rule does.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AskToolPermissionRuleActionEnum
    {
        /// <summary>Allow the tool without asking.</summary>
        Allow,
        /// <summary>Deny the tool without asking.</summary>
        Deny
    }
}
