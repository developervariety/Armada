namespace Armada.Core.Services
{
    using System;
    using System.Globalization;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The hold a Judge NEEDS_REVISION waits in while the independent Checks at the reviewed commit have
    /// not reached a verdict.
    /// </summary>
    /// <remarks>
    /// A Judge that cannot see finished Checks may ask for a revision only because they are not done,
    /// and a revision spends a rescue on work that may be correct. So a NEEDS_REVISION given while the
    /// Checks are unresolved is not recorded yet: the mission stays WorkProduced with the operator-review
    /// hold flag set, and once the Checks resolve (or the wait budget ends) the Judge runs again and
    /// decides with their result. That re-run happens at most once per mission; a NEEDS_REVISION after
    /// it is recorded as usual.
    /// </remarks>
    public static class JudgeRevisionCheckWaitHold
    {
        #region Public-Members

        /// <summary>
        /// Opening of every revision check-wait hold reason, and the attempt-fact reason of its re-run.
        /// </summary>
        public const string ReasonPrefix = MissionAttemptFactRules.JudgeRevisionCheckWaitReason;

        /// <summary>
        /// Event recorded when a NEEDS_REVISION is held for its Checks; one per mission bounds the re-run.
        /// </summary>
        public const string HeldEventType = "mission.judge_revision_check_wait";

        #endregion

        #region Private-Members

        private const string _NoCommit = "(none)";
        private const string _TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";
        private static readonly Regex _Header = new Regex(
            "^" + Regex.Escape(ReasonPrefix) + @" since (?<since>\S+) at (?<commit>\S+):",
            RegexOptions.CultureInvariant);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the hold reason for a NEEDS_REVISION that waits on its Checks.
        /// </summary>
        /// <param name="sinceUtc">When the wait began.</param>
        /// <param name="reviewedCommit">The commit the Judge reviewed, or null when it recorded none.</param>
        /// <param name="holding">The Checks the verdict waits on, for the operator. May be empty.</param>
        /// <returns>The hold reason.</returns>
        public static string BuildReason(DateTime sinceUtc, string? reviewedCommit, string? holding)
        {
            return ReasonPrefix + " since "
                + sinceUtc.ToUniversalTime().ToString(_TimestampFormat, CultureInfo.InvariantCulture)
                + " at " + (String.IsNullOrWhiteSpace(reviewedCommit) ? _NoCommit : reviewedCommit.Trim())
                + ": the Judge asked for a revision before its independent Checks finished; it runs again once they resolve"
                + (String.IsNullOrWhiteSpace(holding) ? String.Empty : "; waiting on: " + holding);
        }

        /// <summary>
        /// True when the mission's NEEDS_REVISION waits in a revision check-wait hold.
        /// </summary>
        /// <param name="mission">The mission. Null returns false.</param>
        /// <returns>True for a held WorkProduced mission whose hold reason is a revision check-wait reason.</returns>
        public static bool IsHeld(Mission? mission)
        {
            if (mission == null) return false;
            if (mission.Status != MissionStatusEnum.WorkProduced) return false;
            if (!mission.HeldForOperatorReview) return false;
            string? reason = mission.HeldForOperatorReviewReason;
            return !String.IsNullOrEmpty(reason) && reason.StartsWith(ReasonPrefix + " ", StringComparison.Ordinal);
        }

        /// <summary>
        /// Read the wait start and the reviewed commit.
        /// </summary>
        /// <param name="reason">The hold reason.</param>
        /// <param name="sinceUtc">When the wait began.</param>
        /// <param name="reviewedCommit">The commit the Judge reviewed, or "(none)".</param>
        /// <returns>False when the reason is not a well-formed revision check-wait reason.</returns>
        public static bool TryParse(string? reason, out DateTime sinceUtc, out string reviewedCommit)
        {
            sinceUtc = DateTime.MinValue;
            reviewedCommit = String.Empty;
            if (String.IsNullOrEmpty(reason)) return false;
            Match match = _Header.Match(reason);
            if (!match.Success) return false;
            if (!DateTime.TryParseExact(
                match.Groups["since"].Value,
                _TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out sinceUtc))
            {
                return false;
            }

            reviewedCommit = match.Groups["commit"].Value;
            return true;
        }

        #endregion
    }
}
