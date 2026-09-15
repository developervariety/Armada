namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The single rule for a WorkProduced mission whose voyage has ended.
    /// </summary>
    /// <remarks>
    /// WorkProduced means "work exists and a later step (the next pipeline stage or a landing) will act
    /// on it". That is a valid resting state only while the voyage is Open or InProgress. Once the voyage
    /// is Complete, Failed or Cancelled no stage will hand off and no landing will start, so every mission
    /// still in WorkProduced would otherwise read as live forever to branch cleanup, capacity and recovery.
    /// The mission then takes a terminal status from evidence, never from the voyage status alone, because
    /// a Failed voyage can hold landed work and a Complete voyage can hold unlanded work:
    /// <list type="bullet">
    /// <item>Landed work (commit on the default branch, or a recorded landing) becomes Complete.</item>
    /// <item>Unlanded work under a Failed voyage becomes Failed; under a Cancelled voyage it becomes Cancelled.</item>
    /// <item>Unlanded work under a Complete voyage becomes Cancelled: the voyage landed through another
    /// stage and this stage's own commit was superseded. It is never marked Complete.</item>
    /// <item>A mission is left unchanged, with a named reason, when ancestry is unknown, a landing is still
    /// in flight, the voyage ended inside the grace period, or the voyage asked for no landing and ended
    /// Complete (the work waits for a person to land it).</item>
    /// </list>
    /// </remarks>
    public static class TerminalVoyageMissionRule
    {
        #region Public-Members

        /// <summary>The voyage is still Open or InProgress, so WorkProduced is a valid resting state.</summary>
        public const string ReasonVoyageLive = "voyage_live";

        /// <summary>The voyage ended inside the grace period.</summary>
        public const string ReasonGrace = "voyage_terminal_grace";

        /// <summary>A merge entry for the mission is still moving through the landing pipeline.</summary>
        public const string ReasonLandingInFlight = "landing_in_flight";

        /// <summary>Ancestry against the default branch could not be established.</summary>
        public const string ReasonAncestryUnknown = "ancestry_unknown";

        /// <summary>The voyage asked for no landing and ended Complete; the work waits for manual landing.</summary>
        public const string ReasonAwaitingManualLanding = "awaiting_manual_landing";

        /// <summary>The work is on the default branch.</summary>
        public const string ReasonWorkLanded = "terminal_voyage_work_landed";

        /// <summary>The commit exists and is not on the default branch.</summary>
        public const string ReasonWorkUnlanded = "terminal_voyage_work_unlanded";

        /// <summary>The commit is absent from the vessel repository.</summary>
        public const string ReasonCommitAbsent = "terminal_voyage_commit_absent";

        /// <summary>The mission recorded no commit.</summary>
        public const string ReasonNoCommit = "terminal_voyage_no_commit";

        /// <summary>The mission left WorkProduced between the scan and the write.</summary>
        public const string ReasonStatusChanged = "status_changed";

        /// <summary>
        /// Separator between a reconciliation failure reason and the reason the mission carried before.
        /// </summary>
        public const string PreviousReasonSeparator = "; previous reason: ";

        #endregion

        #region Private-Members

        private static readonly string[] _UnlandedReasons = { ReasonWorkUnlanded, ReasonCommitAbsent, ReasonNoCommit };

        #endregion

        #region Public-Methods

        /// <summary>
        /// The failure reason recorded on a mission this rule moved to Failed or Cancelled. It is the only
        /// writer of that text; <see cref="IsReconciledFailureReason"/> is the only reader.
        /// </summary>
        /// <param name="voyageStatus">Status of the ended voyage.</param>
        /// <param name="reason">One of the unlanded reason codes.</param>
        /// <param name="previousReason">Failure reason the mission carried before, if any.</param>
        /// <returns>The failure reason to store.</returns>
        public static string FormatReconciledFailureReason(VoyageStatusEnum voyageStatus, string reason, string? previousReason)
        {
            if (Array.IndexOf(_UnlandedReasons, reason) < 0)
                throw new ArgumentException("not an unlanded reason code: " + reason, nameof(reason));

            string text = "Voyage ended " + voyageStatus + " and the mission's work is not on the default branch (" + reason + ")";
            return String.IsNullOrWhiteSpace(previousReason) ? text : text + PreviousReasonSeparator + previousReason;
        }

        /// <summary>
        /// Whether a failure reason was written by <see cref="FormatReconciledFailureReason"/>.
        /// </summary>
        /// <param name="failureReason">Mission failure reason.</param>
        /// <returns>True when the reason ends, before any previous reason, in an unlanded reason code.</returns>
        public static bool IsReconciledFailureReason(string? failureReason)
        {
            if (String.IsNullOrEmpty(failureReason)) return false;

            // The reconciliation text comes first and a previous reason, when there is one, follows the
            // separator, so the reason code closes the text before the first separator.
            int separator = failureReason.IndexOf(PreviousReasonSeparator, StringComparison.Ordinal);
            string head = separator >= 0 ? failureReason.Substring(0, separator) : failureReason;
            foreach (string code in _UnlandedReasons)
            {
                string token = "(" + code + ")";
                if (head.Length > token.Length && head.EndsWith(token, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Whether a mission reached its status through this rule rather than through a failure of its own.
        /// Such a mission is a record of an ended voyage: recovery must not open an incident for it, rescue
        /// it, defer a rescue, or write to it.
        /// </summary>
        /// <param name="status">Mission status.</param>
        /// <param name="failureReason">Mission failure reason.</param>
        /// <returns>True for a Failed or Cancelled mission whose reason this rule wrote.</returns>
        public static bool IsReconciledOutcome(MissionStatusEnum status, string? failureReason)
        {
            return (status == MissionStatusEnum.Failed || status == MissionStatusEnum.Cancelled)
                && IsReconciledFailureReason(failureReason);
        }

        /// <summary>
        /// Whether a voyage status is terminal.
        /// </summary>
        /// <param name="status">Voyage status.</param>
        /// <returns>True for Complete, Failed and Cancelled.</returns>
        public static bool IsTerminalVoyage(VoyageStatusEnum status)
        {
            return status == VoyageStatusEnum.Complete
                || status == VoyageStatusEnum.Failed
                || status == VoyageStatusEnum.Cancelled;
        }

        /// <summary>
        /// Whether a merge entry status means a landing is still in progress.
        /// </summary>
        /// <param name="status">Merge entry status.</param>
        /// <returns>True for every non-terminal merge status.</returns>
        public static bool IsLandingInFlight(MergeStatusEnum status)
        {
            return status != MergeStatusEnum.Landed
                && status != MergeStatusEnum.Failed
                && status != MergeStatusEnum.Cancelled;
        }

        /// <summary>
        /// Decide what happens to a WorkProduced mission under a voyage in the given state.
        /// </summary>
        /// <param name="voyageStatus">Voyage status.</param>
        /// <param name="voyageEndedUtc">When the voyage ended (its completion time, or last update when unrecorded).</param>
        /// <param name="nowUtc">Evaluation time.</param>
        /// <param name="grace">Minimum time since the voyage ended.</param>
        /// <param name="landingInFlight">True when a merge entry for the mission is still in progress.</param>
        /// <param name="noLandingRequested">True when the voyage or vessel landing mode is None.</param>
        /// <param name="landing">Landing probe result; ignored until the earlier guards pass.</param>
        /// <returns>The decision.</returns>
        public static TerminalVoyageMissionDecision Decide(
            VoyageStatusEnum voyageStatus,
            DateTime voyageEndedUtc,
            DateTime nowUtc,
            TimeSpan grace,
            bool landingInFlight,
            bool noLandingRequested,
            TerminalVoyageLandingProbeEnum landing)
        {
            if (!IsTerminalVoyage(voyageStatus))
                return TerminalVoyageMissionDecision.Keep(ReasonVoyageLive);

            if (nowUtc - voyageEndedUtc < grace)
                return TerminalVoyageMissionDecision.Keep(ReasonGrace);

            if (landingInFlight)
                return TerminalVoyageMissionDecision.Keep(ReasonLandingInFlight);

            if (landing == TerminalVoyageLandingProbeEnum.Landed)
                return TerminalVoyageMissionDecision.Move(MissionStatusEnum.Complete, ReasonWorkLanded);

            if (landing == TerminalVoyageLandingProbeEnum.Unknown)
                return TerminalVoyageMissionDecision.Keep(ReasonAncestryUnknown);

            if (voyageStatus == VoyageStatusEnum.Complete && noLandingRequested)
                return TerminalVoyageMissionDecision.Keep(ReasonAwaitingManualLanding);

            string reason = landing == TerminalVoyageLandingProbeEnum.CommitAbsent
                ? ReasonCommitAbsent
                : landing == TerminalVoyageLandingProbeEnum.NoCommit
                    ? ReasonNoCommit
                    : ReasonWorkUnlanded;

            MissionStatusEnum target = voyageStatus == VoyageStatusEnum.Failed
                ? MissionStatusEnum.Failed
                : MissionStatusEnum.Cancelled;
            return TerminalVoyageMissionDecision.Move(target, reason);
        }

        #endregion
    }
}
