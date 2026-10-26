namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Stored decision state for a proposed Ask action.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AskProposalStateEnum
    {
        /// <summary>Waiting for an owner decision.</summary>
        Pending,
        /// <summary>Claimed for execution.</summary>
        Executing,
        /// <summary>Execution completed.</summary>
        Executed,
        /// <summary>Execution failed.</summary>
        Failed,
        /// <summary>The owner rejected the action.</summary>
        Rejected,
        /// <summary>Execution outcome is unknown.</summary>
        Uncertain,
        /// <summary>The proposal expired.</summary>
        Expired
    }
}
