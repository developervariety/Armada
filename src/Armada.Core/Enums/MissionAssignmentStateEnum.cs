namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Assignment pipeline state for a mission.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MissionAssignmentStateEnum
    {
        /// <summary>
        /// Mission is waiting to enter assignment evaluation.
        /// </summary>
        [EnumMember(Value = "Pending")]
        Pending,

        /// <summary>
        /// Mission is waiting for a dependency to complete.
        /// </summary>
        [EnumMember(Value = "WaitingForDependency")]
        WaitingForDependency,

        /// <summary>
        /// Mission is waiting for its vessel mutex to become available.
        /// </summary>
        [EnumMember(Value = "WaitingForVesselMutex")]
        WaitingForVesselMutex,

        /// <summary>
        /// Mission is waiting for an eligible idle captain.
        /// </summary>
        [EnumMember(Value = "WaitingForIdleCaptain")]
        WaitingForIdleCaptain,

        /// <summary>
        /// Mission is waiting for the global host-capacity gate to admit another concurrent
        /// captain workload (MaxConcurrentCaptainWorkloads). Prevents host OOM from
        /// concurrent captain and compiler workloads across all vessels.
        /// </summary>
        [EnumMember(Value = "WaitingForHostCapacity")]
        WaitingForHostCapacity,

        /// <summary>
        /// Mission assignment is provisioning required resources.
        /// </summary>
        [EnumMember(Value = "Provisioning")]
        Provisioning,

        /// <summary>
        /// Mission has been assigned through the assignment pipeline.
        /// </summary>
        [EnumMember(Value = "Assigned")]
        Assigned,

        /// <summary>
        /// Mission assignment failed.
        /// </summary>
        [EnumMember(Value = "Failed")]
        Failed
    }
}
