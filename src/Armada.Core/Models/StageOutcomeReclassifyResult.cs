namespace Armada.Core.Models
{
    /// <summary>
    /// What re-applying the failure-cause rules to past stage outcomes did.
    /// </summary>
    public class StageOutcomeReclassifyResult
    {
        #region Public-Members

        /// <summary>
        /// Outcomes that matched the filter.
        /// </summary>
        public int Examined { get; set; } = 0;

        /// <summary>
        /// Outcomes whose cause or rule changed.
        /// </summary>
        public int Changed { get; set; } = 0;

        /// <summary>
        /// Outcomes left alone because a person classified them.
        /// </summary>
        public int SkippedManual { get; set; } = 0;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public StageOutcomeReclassifyResult()
        {
        }

        #endregion
    }
}
