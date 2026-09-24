namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The counting rule that produced a token-usage record's input counts. Records made by different rules are never
    /// summed into one input figure without saying so.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum TokenUsageRuleEnum
    {
        /// <summary>
        /// The input count is whatever the runtime's provider called input, and its meaning differs by runtime: some
        /// providers count cached input inside it and some do not. No input bucket is stored. Every record written
        /// before input buckets existed reads with this rule; its stored values are kept as they were written.
        /// </summary>
        [EnumMember(Value = "Legacy")]
        Legacy,

        /// <summary>
        /// Input is stored as three separate buckets, the same way for every runtime: uncached input, cache-read input
        /// and cache-write input. The input count is their sum, the cached count is the cache-read bucket, and the total
        /// is input plus output.
        /// </summary>
        [EnumMember(Value = "SeparateInputBuckets")]
        SeparateInputBuckets
    }
}
