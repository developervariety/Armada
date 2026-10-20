namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// The verdict on one integration candidate and the Check records that decided it.
    /// </summary>
    public class IntegrationValidationResult
    {
        #region Public-Members

        /// <summary>
        /// The validation outcome.
        /// </summary>
        public IntegrationValidationOutcomeEnum Outcome { get; set; } = IntegrationValidationOutcomeEnum.Unavailable;

        /// <summary>
        /// Named reason. For a refusal it starts with the failure class
        /// (<c>integration_validation_failed</c>, <c>integration_validation_command_missing</c>,
        /// <c>integration_validation_unavailable</c>) so it can stand as a mission or merge entry
        /// failure reason.
        /// </summary>
        public string Reason { get; set; } = String.Empty;

        /// <summary>
        /// The candidate that was validated.
        /// </summary>
        public IntegrationCandidate Candidate { get; set; } = new IntegrationCandidate();

        /// <summary>
        /// Check records that measured the candidate, in the order they decided it. Includes reused records.
        /// </summary>
        public List<string> CheckRunIds { get; set; } = new List<string>();

        /// <summary>
        /// The subset of <see cref="CheckRunIds"/> that already existed: a passing record at exactly the
        /// candidate commit with the same command, so the check was not run a second time.
        /// </summary>
        public List<string> ReusedCheckRunIds { get; set; } = new List<string>();

        /// <summary>
        /// True when the candidate may be published.
        /// </summary>
        public bool AllowsPublication
        {
            get
            {
                return Outcome == IntegrationValidationOutcomeEnum.Passed
                    || Outcome == IntegrationValidationOutcomeEnum.NotRequired
                    || Outcome == IntegrationValidationOutcomeEnum.Disabled;
            }
        }

        #endregion
    }
}
