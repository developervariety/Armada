namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Identifies an unattended lead implementation.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum LeadRunnerTypeEnum
    {
        /// <summary>
        /// The local lead-cycle launcher.
        /// </summary>
        Legacy,

        /// <summary>
        /// The Grok Bot cloud lead.
        /// </summary>
        Grok
    }
}
