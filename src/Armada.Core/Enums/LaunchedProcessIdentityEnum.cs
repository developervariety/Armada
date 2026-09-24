namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// What a lookup of a recorded agent process identifier found: whether a live process holds the identifier and
    /// whether it is provably the process Armada launched with it.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum LaunchedProcessIdentityEnum
    {
        /// <summary>No live process holds the identifier.</summary>
        [EnumMember(Value = "NotRunning")]
        NotRunning,

        /// <summary>A live process holds the identifier and its start time matches the recorded launch.</summary>
        [EnumMember(Value = "Verified")]
        Verified,

        /// <summary>
        /// A live process holds the identifier, but nothing proves it is the launched one: no launch was recorded for
        /// the identifier in this admiral process, or the live process's start time cannot be read. It reads as
        /// running, and it is never killed.
        /// </summary>
        [EnumMember(Value = "Unverified")]
        Unverified,

        /// <summary>
        /// A live process holds the identifier, but it started at a different time from the recorded launch (or after
        /// the caller's launch reference): the operating system reused the identifier for an unrelated process. It
        /// reads as not running, and it is never killed.
        /// </summary>
        [EnumMember(Value = "Reused")]
        Reused
    }
}
