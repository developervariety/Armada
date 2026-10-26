namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// The result of applying failure-cause rules to a stage outcome.
    /// </summary>
    public class FailureCauseClassification
    {
        #region Public-Members

        /// <summary>
        /// Suspected cause.
        /// </summary>
        public FailureCauseEnum Cause { get; set; } = FailureCauseEnum.None;

        /// <summary>
        /// What assigned it: <see cref="FailureCauseSourceEnum.Rule"/> when a rule matched, otherwise None.
        /// </summary>
        public FailureCauseSourceEnum Source { get; set; } = FailureCauseSourceEnum.None;

        /// <summary>
        /// Rule that matched, or null.
        /// </summary>
        public string? RuleId { get; set; } = null;

        /// <summary>
        /// Short evidence: the rule name, or why nothing matched.
        /// </summary>
        public string? Note { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public FailureCauseClassification()
        {
        }

        #endregion
    }
}
