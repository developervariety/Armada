namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Selects which unattended lead implementation can start normal lead cycles.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum LeadOperatingModeEnum
    {
        /// <summary>
        /// The local unattended lead is primary.
        /// </summary>
        LegacyPrimary,

        /// <summary>
        /// Grok Bot is primary and the local lead is standby.
        /// </summary>
        GrokPrimary,

        /// <summary>
        /// No unattended lead can start a cycle.
        /// </summary>
        Maintenance
    }
}
