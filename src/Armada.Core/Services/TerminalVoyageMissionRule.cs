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

        #endregion

        #region Public-Methods

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
