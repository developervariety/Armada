namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Body of PUT /api/v1/stage-outcomes/{id}/cause: classify a stage outcome by hand, or hand it back to the
    /// failure-cause rules.
    /// </summary>
    public class StageOutcomeClassifyRequest
    {
        #region Public-Members

        /// <summary>
        /// Cause to set by hand, or null to clear the manual cause and re-apply the rules.
        /// </summary>
        public FailureCauseEnum? Cause { get; set; } = null;

        /// <summary>
        /// Optional note (evidence), at most 2000 characters are kept.
        /// </summary>
        public string? Note { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public StageOutcomeClassifyRequest()
        {
        }

        #endregion
    }
}
