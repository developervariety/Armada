namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Kind of record a trace record describes.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum TraceEntityKindEnum
    {
        /// <summary>An objective.</summary>
        Objective,

        /// <summary>A voyage.</summary>
        Voyage,

        /// <summary>A mission.</summary>
        Mission,

        /// <summary>A check run.</summary>
        CheckRun,

        /// <summary>An incident.</summary>
        Incident,

        /// <summary>A merge-queue entry.</summary>
        MergeEntry,

        /// <summary>A deployment.</summary>
        Deployment
    }
}
