namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// What assigned a stage attempt's suspected cause.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum FailureCauseSourceEnum
    {
        /// <summary>
        /// Nothing has classified the attempt.
        /// </summary>
        [EnumMember(Value = "None")]
        None,

        /// <summary>
        /// A failure-cause rule (built-in or administrator-defined) matched.
        /// </summary>
        [EnumMember(Value = "Rule")]
        Rule,

        /// <summary>
        /// A Judge suggested the cause in its review.
        /// </summary>
        [EnumMember(Value = "Judge")]
        Judge,

        /// <summary>
        /// A person classified the attempt; rules never overwrite it.
        /// </summary>
        [EnumMember(Value = "Manual")]
        Manual
    }
}
