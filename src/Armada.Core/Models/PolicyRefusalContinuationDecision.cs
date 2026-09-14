namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// The decision taken for a mission whose captain refused work, with the reason for it.
    /// </summary>
    public class PolicyRefusalContinuationDecision
    {
        #region Public-Members

        /// <summary>Outcome of the decision.</summary>
        public PolicyRefusalContinuationOutcomeEnum Outcome { get; set; } = PolicyRefusalContinuationOutcomeEnum.NotApplicable;

        /// <summary>Why this outcome was chosen. Never empty.</summary>
        public string Reason { get; set; } = "";

        /// <summary>Captains the continuation must not be assigned to: every captain on the refusing runtime.</summary>
        public List<string> ExcludedCaptainIds { get; set; } = new List<string>();

        /// <summary>Distinct runtimes that hold at least one approved captain able to run the continuation.</summary>
        public List<string> AlternateRuntimes { get; set; } = new List<string>();

        #endregion
    }
}
