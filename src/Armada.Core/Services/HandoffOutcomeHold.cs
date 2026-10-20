namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The hold a finished stage waits in when the <c>handoff_outcome</c> decision reads its work as
    /// <c>partial</c> with high confidence and the hold is enabled.
    /// </summary>
    /// <remarks>
    /// The finished stage stays WorkProduced with the operator-review hold flag set, so it does not hand
    /// off, its dependent stages do not start, and the voyage does not complete. The hold reason records
    /// when the hold began, the decision event it acts on, and the unmet acceptance criteria, so the
    /// operator inbox can show the criteria and the timeout sweep can bound the wait. The hold never
    /// cancels, rescues, lands or dispatches by itself: only an operator choice or the timeout ends it,
    /// and the timeout ends it with the same Mail-and-continue handoff the decision makes without a hold.
    /// </remarks>
    public static class HandoffOutcomeHold
    {
        #region Public-Members

        /// <summary>Opening of every handoff-outcome hold reason.</summary>
        public const string ReasonPrefix = "handoff_outcome_hold";

        /// <summary>
        /// The lowest gated <c>partial</c> confidence that holds the next stage. Below it the decision Mails
        /// the next stage and the handoff continues.
        /// </summary>
        public const double MinimumConfidence = 0.90;

        /// <summary>Event type recorded when a hold engages.</summary>
        public const string EngagedEventType = "mission.handoff_hold_engaged";

        /// <summary>Label recorded when an operator hands the stage off unchanged.</summary>
        public const string LabelRelease = "release";

        /// <summary>Label recorded when an operator Mails the unmet criteria and hands the stage off.</summary>
        public const string LabelMailAndRelease = "mail_release";

        /// <summary>Label recorded when an operator sends the stage to rescue.</summary>
        public const string LabelRescue = "rescue";

        /// <summary>Label recorded when no operator chose before the timeout and the hold released with the Mail.</summary>
        public const string LabelTimeout = "hold_timeout";

        /// <summary>The decided-by value recorded for a timeout release.</summary>
        public const string DecidedByTimeout = "timeout";

        /// <summary>Opening of the failure reason a rescue choice writes on the held stage.</summary>
        public const string RescueFailurePrefix = "handoff_outcome_hold_rescue";

        /// <summary>The event id recorded when no decision event could be read.</summary>
        public const string NoEvent = "(none)";

        #endregion

        #region Private-Members

        private const string _TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";
        private const string _NoUnmet = "none";

        private static readonly Regex _Header = new Regex(
            "^" + Regex.Escape(ReasonPrefix) + @" since (?<since>\S+) event (?<event>\S+) unmet (?<unmet>\S+):",
            RegexOptions.CultureInvariant);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the hold reason. The reason names the unmet criteria in full, so the operator inbox shows
        /// them, and opens with a header the timeout sweep and the release read back.
        /// </summary>
        /// <param name="sinceUtc">When the hold began.</param>
        /// <param name="decisionEventId">The gated <c>handoff_outcome</c> decision event.</param>
        /// <param name="unmetIndices">The 1-based unmet acceptance-criteria indices.</param>
        /// <param name="criteria">The objective's acceptance criteria, in order; may be empty.</param>
        /// <param name="confidence">The gated partial confidence.</param>
        /// <param name="timeoutMinutes">Minutes before the hold is released with the Mail.</param>
        /// <returns>The hold reason.</returns>
        public static string BuildReason(
            DateTime sinceUtc,
            string? decisionEventId,
            IReadOnlyList<int>? unmetIndices,
            IReadOnlyList<string>? criteria,
            double confidence,
            int timeoutMinutes)
        {
            List<int> unmet = (unmetIndices ?? new List<int>()).Where(index => index >= 1).Distinct().OrderBy(index => index).ToList();
            StringBuilder sb = new StringBuilder();
            sb.Append(ReasonPrefix)
                .Append(" since ").Append(sinceUtc.ToUniversalTime().ToString(_TimestampFormat, CultureInfo.InvariantCulture))
                .Append(" event ").Append(String.IsNullOrWhiteSpace(decisionEventId) ? NoEvent : decisionEventId.Trim())
                .Append(" unmet ").Append(unmet.Count == 0 ? _NoUnmet : String.Join(",", unmet.Select(index => index.ToString(CultureInfo.InvariantCulture))))
                .Append(": the handoff-outcome check read this stage as partial at ")
                .Append(confidence.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(". The next stage is held until an operator chooses release, mail_release or rescue with armada_handoff_hold; ")
                .Append("with no choice it is released with the unmet-criteria note after ")
                .Append(timeoutMinutes.ToString(CultureInfo.InvariantCulture)).Append(" min.");

            sb.Append(" Unmet criteria:");
            if (unmet.Count == 0)
            {
                sb.Append(" one or more acceptance criteria (see the objective).");
                return sb.ToString();
            }

            foreach (int index in unmet)
            {
                sb.Append(' ').Append(index.ToString(CultureInfo.InvariantCulture)).Append(". ");
                if (criteria != null && index <= criteria.Count && !String.IsNullOrWhiteSpace(criteria[index - 1]))
                    sb.Append(criteria[index - 1].Trim());
                else
                    sb.Append("(see the objective)");
                sb.Append(';');
            }

            return sb.ToString();
        }

        /// <summary>
        /// True when the mission waits in a handoff-outcome hold.
        /// </summary>
        /// <param name="mission">The mission. Null returns false.</param>
        /// <returns>True for a WorkProduced mission whose hold reason is a handoff-outcome reason.</returns>
        public static bool IsHeld(Mission? mission)
        {
            if (mission == null) return false;
            if (mission.Status != MissionStatusEnum.WorkProduced) return false;
            if (!mission.HeldForOperatorReview) return false;
            return IsReason(mission.HeldForOperatorReviewReason);
        }

        /// <summary>
        /// True when a hold reason is a handoff-outcome reason.
        /// </summary>
        /// <param name="reason">The hold reason. Null returns false.</param>
        /// <returns>True when the reason opens with <see cref="ReasonPrefix"/> and a space.</returns>
        public static bool IsReason(string? reason)
        {
            return !String.IsNullOrEmpty(reason) && reason.StartsWith(ReasonPrefix + " ", StringComparison.Ordinal);
        }

        /// <summary>
        /// Read the hold start, the decision event id, and the unmet criteria indices from a hold reason.
        /// </summary>
        /// <param name="reason">The hold reason.</param>
        /// <param name="sinceUtc">When the hold began.</param>
        /// <param name="decisionEventId">The decision event id, or <see cref="NoEvent"/>.</param>
        /// <param name="unmetIndices">The 1-based unmet acceptance-criteria indices; empty when none were named.</param>
        /// <returns>False when the reason is not a well-formed handoff-outcome reason.</returns>
        public static bool TryParse(string? reason, out DateTime sinceUtc, out string decisionEventId, out List<int> unmetIndices)
        {
            sinceUtc = DateTime.MinValue;
            decisionEventId = NoEvent;
            unmetIndices = new List<int>();
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

            decisionEventId = match.Groups["event"].Value;
            string unmet = match.Groups["unmet"].Value;
            if (!String.Equals(unmet, _NoUnmet, StringComparison.Ordinal))
            {
                foreach (string part in unmet.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!Int32.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 1) return false;
                    unmetIndices.Add(index);
                }
            }

            return true;
        }

        /// <summary>
        /// Read an operator choice from its tool spelling: <c>release</c>, <c>mail_release</c>, or <c>rescue</c>.
        /// </summary>
        /// <param name="text">The choice text; case and surrounding space are ignored.</param>
        /// <param name="choice">The parsed choice.</param>
        /// <returns>False when the text names no choice.</returns>
        public static bool TryParseChoice(string? text, out HandoffOutcomeHoldChoiceEnum choice)
        {
            choice = HandoffOutcomeHoldChoiceEnum.Release;
            string normalized = (text ?? String.Empty).Trim().ToLowerInvariant();
            switch (normalized)
            {
                case LabelRelease:
                    choice = HandoffOutcomeHoldChoiceEnum.Release;
                    return true;
                case LabelMailAndRelease:
                    choice = HandoffOutcomeHoldChoiceEnum.MailAndRelease;
                    return true;
                case LabelRescue:
                    choice = HandoffOutcomeHoldChoiceEnum.Rescue;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The label an operator choice records.
        /// </summary>
        /// <param name="choice">The operator choice.</param>
        /// <returns><see cref="LabelRelease"/>, <see cref="LabelMailAndRelease"/>, or <see cref="LabelRescue"/>.</returns>
        public static string LabelFor(HandoffOutcomeHoldChoiceEnum choice)
        {
            switch (choice)
            {
                case HandoffOutcomeHoldChoiceEnum.MailAndRelease:
                    return LabelMailAndRelease;
                case HandoffOutcomeHoldChoiceEnum.Rescue:
                    return LabelRescue;
                default:
                    return LabelRelease;
            }
        }

        #endregion
    }
}
