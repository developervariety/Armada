namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Action to take when a stage review is denied.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ReviewDenyActionEnum
    {
        /// <summary>
        /// Send the same stage back for rework on its existing mission.
        /// </summary>
        [EnumMember(Value = "RetryStage")]
        RetryStage,

        /// <summary>
        /// Fail the current stage and cancel any downstream dependent stages.
        /// </summary>
        [EnumMember(Value = "FailPipeline")]
        FailPipeline
    }
}
