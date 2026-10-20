namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Outcome of validating an integration candidate: the merge of a source branch into the current
    /// target commit, before the target is advanced or published.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum IntegrationValidationOutcomeEnum
    {
        /// <summary>
        /// Every required check passed against the candidate commit. Publication may proceed.
        /// </summary>
        Passed,

        /// <summary>
        /// The vessel's policy explicitly requires no integration checks. Publication may proceed.
        /// </summary>
        NotRequired,

        /// <summary>
        /// Integration validation is switched off in settings. Publication may proceed.
        /// </summary>
        Disabled,

        /// <summary>
        /// The vessel requires integration checks, but its workflow profile defines no Build or UnitTest
        /// command, so nothing can measure the candidate. Publication is refused.
        /// </summary>
        CommandMissing,

        /// <summary>
        /// A required check ran against the candidate and did not pass. Publication is refused.
        /// </summary>
        Failed,

        /// <summary>
        /// A required check could not run against the candidate (no profile, vessel not ready, checkout
        /// failure). Publication is refused.
        /// </summary>
        Unavailable
    }
}
