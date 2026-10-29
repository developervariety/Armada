namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>What decided a native tool permission request.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AskToolPermissionSourceEnum
    {
        /// <summary>The thread owner decided it.</summary>
        Owner,
        /// <summary>A remembered rule of the thread decided it.</summary>
        Rule,
        /// <summary>The request expired or its turn ended, so it was denied.</summary>
        Expiry
    }
}
