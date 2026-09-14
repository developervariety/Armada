namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Kind of durable attempt fact recorded for a mission. Production metrics read these facts
    /// instead of inferring attempt history from titles or from a mission's final status.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MissionAttemptFactTypeEnum
    {
        /// <summary>A captain process was launched for the mission.</summary>
        AttemptStarted = 0,

        /// <summary>The mission was run again automatically without operator action.</summary>
        Retried = 1,

        /// <summary>An operator or client restarted a terminal mission.</summary>
        Restarted = 2,

        /// <summary>A reviewer denied the mission's work.</summary>
        ReviewDenied = 3,

        /// <summary>The mission reached Failed or LandingFailed.</summary>
        Failed = 4,

        /// <summary>The mission's work landed. This resolves the attempt chain as delivered.</summary>
        Landed = 5
    }
}
