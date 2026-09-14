namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The single definition of which attempt facts mean an attempt chain was not accepted on its
    /// first pass.
    /// </summary>
    public static class MissionAttemptFactRules
    {
        /// <summary>
        /// Reason code for a Judge that is re-run only because its independent Checks have not
        /// finished. The work was not rejected, so this re-run does not end first-pass acceptance.
        /// </summary>
        public const string JudgeCheckWaitReason = "judge_check_wait";

        /// <summary>Whether a fact ends first-pass acceptance for its attempt chain.</summary>
        /// <param name="fact">Fact to classify.</param>
        /// <returns>True for a failure, review denial, restart, rescue, or rejecting retry.</returns>
        public static bool DisqualifiesFirstPass(MissionAttemptFact fact)
        {
            if (fact == null) throw new ArgumentNullException(nameof(fact));
            if (fact.IsRescue) return true;
            return fact.FactType switch
            {
                MissionAttemptFactTypeEnum.Failed => true,
                MissionAttemptFactTypeEnum.ReviewDenied => true,
                MissionAttemptFactTypeEnum.Restarted => true,
                MissionAttemptFactTypeEnum.Retried => !String.Equals(fact.ReasonCode, JudgeCheckWaitReason, StringComparison.Ordinal),
                _ => false
            };
        }
    }
}
