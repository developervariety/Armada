namespace Armada.Core.Services
{
    using System;
    using System.Globalization;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The hold a Judge PASS waits in while the independent Checks at the reviewed commit have not
    /// reached a verdict.
    /// </summary>
    /// <remarks>
    /// The PASS is a finished review of one commit. While its Checks run, the mission stays
    /// WorkProduced with the operator-review hold flag set, so it does not hand off or land and the
    /// voyage does not complete. The hold reason records when the wait began and which commit was
    /// reviewed, so the release sweep can bound the wait and can tell a new commit from the one the
    /// Judge read. When the Checks pass at exactly that commit the PASS is released as it stands; a
    /// new Judge run happens only when the reviewed commit changed.
    /// </remarks>
    public static class JudgeCheckWaitHold
    {
        #region Public-Members

        /// <summary>
        /// Opening of every check-wait hold reason. It is the same code the attempt facts use for a
        /// Judge that waits on its Checks.
        /// </summary>
        public const string ReasonPrefix = MissionAttemptFactRules.JudgeCheckWaitReason;

        #endregion

        #region Private-Members

        private const string _NoCommit = "(none)";
        private const string _TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";
        private const string _OperatorReviewMarker = " | then held for operator review: ";

        private static readonly Regex _Header = new Regex(
            "^" + Regex.Escape(ReasonPrefix) + @" since (?<since>\S+) at (?<commit>\S+):",
            RegexOptions.CultureInvariant);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the hold reason for a PASS that waits on its Checks.
        /// </summary>
        /// <param name="sinceUtc">When the wait began.</param>
        /// <param name="reviewedCommit">The commit the Judge reviewed, or null when it recorded none.</param>
        /// <param name="holding">The Checks the PASS waits on, for the operator. May be empty.</param>
        /// <param name="operatorReviewReason">A review-substance hold that still applies once the
        /// Checks pass, or null.</param>
        /// <returns>The hold reason.</returns>
        public static string BuildReason(DateTime sinceUtc, string? reviewedCommit, string? holding, string? operatorReviewReason)
        {
            string reason = ReasonPrefix + " since "
                + sinceUtc.ToUniversalTime().ToString(_TimestampFormat, CultureInfo.InvariantCulture)
                + " at " + (String.IsNullOrWhiteSpace(reviewedCommit) ? _NoCommit : reviewedCommit.Trim())
                + ": the Judge PASS is released when the independent Checks pass at this commit; a failed Check rejects it"
                + (String.IsNullOrWhiteSpace(holding) ? String.Empty : "; waiting on: " + holding);
            if (!String.IsNullOrWhiteSpace(operatorReviewReason))
                reason += _OperatorReviewMarker + operatorReviewReason;
            return reason;
        }

        /// <summary>
        /// True when the mission's PASS waits in a check-wait hold.
        /// </summary>
        /// <param name="mission">The mission. Null returns false.</param>
        /// <returns>True for a WorkProduced mission whose hold reason is a check-wait reason.</returns>
        public static bool IsHeld(Mission? mission)
        {
            if (mission == null) return false;
            if (mission.Status != MissionStatusEnum.WorkProduced) return false;
            if (!mission.HeldForOperatorReview) return false;
            return IsReason(mission.HeldForOperatorReviewReason);
        }

        /// <summary>
        /// True when a hold reason is a check-wait reason.
        /// </summary>
        /// <param name="reason">The hold reason. Null returns false.</param>
        /// <returns>True when the reason opens with <see cref="ReasonPrefix"/>.</returns>
        public static bool IsReason(string? reason)
        {
            return !String.IsNullOrEmpty(reason) && reason.StartsWith(ReasonPrefix + " ", StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the mission still carries the commit recorded in its hold reason.
        /// </summary>
        /// <param name="recordedCommit">The commit parsed from the hold reason.</param>
        /// <param name="currentCommit">The mission's commit now.</param>
        /// <returns>True when both name the same commit, or both name none.</returns>
        public static bool IsSameReviewedCommit(string? recordedCommit, string? currentCommit)
        {
            bool recordedNone = String.IsNullOrWhiteSpace(recordedCommit) || String.Equals(recordedCommit, _NoCommit, StringComparison.Ordinal);
            bool currentNone = String.IsNullOrWhiteSpace(currentCommit);
            if (recordedNone || currentNone) return recordedNone && currentNone;
            return String.Equals(recordedCommit!.Trim(), currentCommit!.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Read the wait start, the reviewed commit, and any operator-review hold that follows.
        /// </summary>
        /// <param name="reason">The hold reason.</param>
        /// <param name="sinceUtc">When the wait began.</param>
        /// <param name="reviewedCommit">The commit the Judge reviewed.</param>
        /// <param name="operatorReviewReason">The review-substance hold that applies after the Checks pass, or null.</param>
        /// <returns>False when the reason is not a well-formed check-wait reason.</returns>
        public static bool TryParse(string? reason, out DateTime sinceUtc, out string reviewedCommit, out string? operatorReviewReason)
        {
            sinceUtc = DateTime.MinValue;
            reviewedCommit = String.Empty;
            operatorReviewReason = null;
            if (!IsReason(reason)) return false;

            Match match = _Header.Match(reason!);
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
            int marker = reason!.IndexOf(_OperatorReviewMarker, StringComparison.Ordinal);
            if (marker >= 0)
            {
                string after = reason.Substring(marker + _OperatorReviewMarker.Length);
                operatorReviewReason = String.IsNullOrWhiteSpace(after) ? null : after;
            }

            return true;
        }

        #endregion
    }
}
