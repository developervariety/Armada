namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Status of one snapshotted fleet action target.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum FleetActionTargetStatusEnum
    {
        /// <summary>Target awaits a dispatch claim.</summary>
        Pending,
        /// <summary>Target is claimed; dispatch must not be repeated.</summary>
        Dispatching,
        /// <summary>Associated voyage is active.</summary>
        Running,
        /// <summary>Work was produced by its voyage.</summary>
        WorkProduced,
        /// <summary>Associated voyage was delivered.</summary>
        Delivered,
        /// <summary>Target failed.</summary>
        Failed,
        /// <summary>Voyage completed with no confirmed source work.</summary>
        Completed,
        /// <summary>Target was skipped.</summary>
        Skipped,
        /// <summary>Target was cancelled.</summary>
        Cancelled,
        /// <summary>Dispatch outcome is unknown and must not be repeated.</summary>
        Uncertain
    }
}
