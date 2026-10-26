namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Aggregate status for a fleet action run.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum FleetActionRunStatusEnum
    {
        /// <summary>Run is accepted and has not started.</summary>
        Pending,
        /// <summary>Run has active or dispatching targets.</summary>
        Running,
        /// <summary>All targets completed successfully.</summary>
        Completed,
        /// <summary>Some targets completed and some did not.</summary>
        PartiallyCompleted,
        /// <summary>Run failed.</summary>
        Failed,
        /// <summary>Run was cancelled.</summary>
        Cancelled,
        /// <summary>Dispatch outcome is unknown and must not be repeated.</summary>
        Uncertain
    }
}
