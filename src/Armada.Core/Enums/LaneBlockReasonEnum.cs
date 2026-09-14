namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Fleet-wide condition that prevented a lane from receiving work at an observation, independent
    /// of the lane's own occupancy and capacity.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum LaneBlockReasonEnum
    {
        /// <summary>Nothing outside the lane blocked dispatch.</summary>
        None = 0,

        /// <summary>The fleet-wide concurrent voyage limit was reached.</summary>
        FleetCapacity = 1,

        /// <summary>A dispatch hold was engaged.</summary>
        DispatchHold = 2
    }
}
