namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Verified state of a recorded process identity.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SelfDeployProcessStateEnum
    {
        /// <summary>
        /// A process with the recorded id and exact start time is running.
        /// </summary>
        Running = 0,

        /// <summary>
        /// No process with the recorded id and start time exists. A reused id counts as exited.
        /// </summary>
        Exited = 1,

        /// <summary>
        /// The process exists but its identity cannot be confirmed. Callers must fail closed.
        /// </summary>
        Unverified = 2
    }
}
