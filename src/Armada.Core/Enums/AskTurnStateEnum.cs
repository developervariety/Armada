namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Durable lifecycle state for an Ask Armada turn.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AskTurnStateEnum
    {
        /// <summary>The request is stored but has not begun runtime execution.</summary>
        Pending,
        /// <summary>The runtime is executing this turn in the current process.</summary>
        Running,
        /// <summary>The runtime returned a successful response which is stored.</summary>
        Completed,
        /// <summary>The runtime returned a failed response which is stored.</summary>
        Failed,
        /// <summary>The stored active turn has no runtime owner in this process and must not be replayed.</summary>
        Uncertain
    }
}
