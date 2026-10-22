namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>How expired history is treated.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HistoryRetentionModeEnum
    {
        /// <summary>Uploaded to the archive and verified before it is freed locally.</summary>
        [EnumMember(Value = "Archive")]
        Archive,

        /// <summary>Kept, because no archive is configured.</summary>
        [EnumMember(Value = "Keep")]
        Keep,

        /// <summary>Freed without a copy.</summary>
        [EnumMember(Value = "Delete")]
        Delete
    }
}
