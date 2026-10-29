namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// How one captain process attempt at a mission ended. The mission's own status and the attempt facts record
    /// what Armada did with the work afterwards.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MissionAttemptOutcomeEnum
    {
        /// <summary>The process has not exited, or its exit was not observed.</summary>
        Running = 0,

        /// <summary>The process exited with code 0.</summary>
        Completed = 1,

        /// <summary>The process exited with a non-zero or unknown code.</summary>
        Failed = 2,

        /// <summary>The attempt was stopped, superseded by a newer attempt, or reported an interruption.</summary>
        Interrupted = 3
    }
}
