namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Where a native memory record came from. Stored as provenance so a record can be traced to
    /// the work that produced it and later reconciled or deleted.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MemorySourceKindEnum
    {
        /// <summary>
        /// Distilled from a voyage.
        /// </summary>
        Voyage,

        /// <summary>
        /// Distilled from a single mission.
        /// </summary>
        Mission,

        /// <summary>
        /// Learned about a specific vessel.
        /// </summary>
        Vessel,

        /// <summary>
        /// Extracted from an interactive conversation.
        /// </summary>
        Conversation,

        /// <summary>
        /// Entered directly by an operator.
        /// </summary>
        Manual,

        /// <summary>
        /// Any other origin.
        /// </summary>
        Other
    }
}
