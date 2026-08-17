namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The kind of work a background job performs. The discriminator lets the coordinator route a claimed
    /// job to the right handler.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum JobKindEnum
    {
        /// <summary>
        /// A generic/unspecified background job.
        /// </summary>
        [EnumMember(Value = "Generic")]
        Generic,

        /// <summary>
        /// A bulk cleanup / purge job.
        /// </summary>
        [EnumMember(Value = "Cleanup")]
        Cleanup,

        /// <summary>
        /// A repository or data import/sync job.
        /// </summary>
        [EnumMember(Value = "Sync")]
        Sync,

        /// <summary>
        /// A report/aggregation job.
        /// </summary>
        [EnumMember(Value = "Report")]
        Report
    }
}
