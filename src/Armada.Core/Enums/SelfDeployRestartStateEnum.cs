namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Durable state of one supervised self-deploy restart.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SelfDeployRestartStateEnum
    {
        /// <summary>
        /// The running admiral wrote the record and started the supervisor. No process was stopped.
        /// </summary>
        Prepared = 0,

        /// <summary>
        /// The supervisor holds the cutover lock and verified both artifacts and the running admiral identity.
        /// </summary>
        Armed = 1,

        /// <summary>
        /// The running admiral saw the armed supervisor and committed to exit.
        /// </summary>
        ExitRequested = 2,

        /// <summary>
        /// The supervisor confirmed that the previous admiral process no longer runs.
        /// </summary>
        OldStopped = 3,

        /// <summary>
        /// The supervisor launched the candidate and is waiting for bounded health proof.
        /// </summary>
        CandidateStarting = 4,

        /// <summary>
        /// The candidate passed health validation. Terminal.
        /// </summary>
        Committed = 5,

        /// <summary>
        /// The candidate failed; the supervisor is stopping it before relaunching the rollback artifact.
        /// </summary>
        RollingBack = 6,

        /// <summary>
        /// The supervisor launched the rollback artifact and is waiting for bounded health proof.
        /// </summary>
        RollbackStarting = 7,

        /// <summary>
        /// The rollback artifact passed health validation. Terminal.
        /// </summary>
        RolledBack = 8,

        /// <summary>
        /// The candidate advanced the database schema, so the previous binary was not started. Terminal;
        /// an operator restore from the retained backup is required.
        /// </summary>
        RollbackBlocked = 9,

        /// <summary>
        /// The cutover stopped before any process was stopped; the previous admiral stays authoritative. Terminal.
        /// </summary>
        Aborted = 10,

        /// <summary>
        /// The cutover could not reach a healthy owner without risking overlap. Terminal; operator action required.
        /// </summary>
        Failed = 11
    }
}
