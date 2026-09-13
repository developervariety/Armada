namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Resolution state of a dock's provisioning evidence.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DockGitAnchorStateEnum
    {
        /// <summary>The provisioning commit is captured; enrichment has not run.</summary>
        Seeded,
        /// <summary>All selected bounded anchor queries completed.</summary>
        Complete,
        /// <summary>A query failed or snapshot data exceeded its bounds.</summary>
        Incomplete
    }
}
