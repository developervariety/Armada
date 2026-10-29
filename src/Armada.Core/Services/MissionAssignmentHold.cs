namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The reasons the assignment pass holds a Pending mission back from a captain, stored on
    /// <see cref="Armada.Core.Models.Mission.AssignmentHoldReason"/> with the record that holds it, and the events
    /// that record each change. A hold is recorded once when it starts or its reason changes, never on every
    /// assignment tick, and once when it is released.
    /// </summary>
    public static class MissionAssignmentHold
    {
        #region Public-Members

        /// <summary>Event recorded when a named hold starts or changes reason.</summary>
        public const string HeldEventType = "mission.assignment_held";

        /// <summary>Event recorded when a named hold is released and assignment proceeds.</summary>
        public const string ReleasedEventType = "mission.assignment_hold_released";

        /// <summary>The predecessor stage the mission depends on does not exist.</summary>
        public const string DependencyMissing = "dependency_missing";

        /// <summary>The predecessor stage has not finished. This is the ordinary wait and records no event.</summary>
        public const string DependencyUnfinished = "dependency_unfinished";

        /// <summary>The predecessor finished, but a parallel sibling stage of the same order is still running.</summary>
        public const string ParallelStagesRunning = "parallel_stages_running";

        /// <summary>The predecessor on another vessel produced work that has not landed yet.</summary>
        public const string CrossVesselDependencyNotLanded = "cross_vessel_dependency_not_landed";

        /// <summary>The predecessor is an Architect whose plan has not been turned into stages yet.</summary>
        public const string ArchitectHandoffPending = "architect_handoff_pending";

        /// <summary>
        /// The predecessor produced work and its completion handling is still running: the definition-of-done gate,
        /// including any re-verification of declared consumer repositories, then the handoff to this stage.
        /// </summary>
        public const string DependencyCompletionInFlight = "dependency_completion_in_flight";

        /// <summary>The predecessor's Judge PASS is held for operator review.</summary>
        public const string DependencyHeldForReview = "dependency_held_for_review";

        /// <summary>The predecessor produced work with no recorded definition-of-done result for its launch.</summary>
        public const string DependencyGateResultMissing = "dependency_gate_result_missing";

        /// <summary>
        /// An earlier stage of the mission's own chain still has the inherited branch checked out in its dock, so the
        /// mission's dock cannot check it out yet.
        /// </summary>
        public const string PredecessorDockHeld = "predecessor_dock_held";

        /// <summary>The Architect plan marks this mission to run after the implementation work.</summary>
        public const string ArchitectSequenced = "architect_sequenced";

        /// <summary>
        /// A reassigned root (stage-1) mission's retained dock authorized continuing its committed branch, but that
        /// branch no longer resolves in the vessel repository. The mission is held rather than cut from the default
        /// branch, which would orphan its committed work.
        /// </summary>
        public const string RootContinuationRefMissing = "root_continuation_ref_missing";

        /// <summary>
        /// A reassigned root (stage-1) mission still carries committed work on its persisted branch, but no active dock
        /// authorizes continuing it (a failed provision left no live dock). The mission is held rather than cut from the
        /// default branch, which would orphan its committed work.
        /// </summary>
        public const string RootContinuationDockMissing = "root_continuation_dock_missing";

        /// <summary>The configured Harbor runner has no free job slot.</summary>
        public const string HarborRunnerCapacity = "harbor_runner_capacity_exhausted";

        /// <summary>A Harbor job for this mission may still be running, so it cannot be assigned again.</summary>
        public const string HarborJobUnconfirmed = "harbor_job_stop_unconfirmed";

        /// <summary>
        /// Captains of the mission's tenant are idle, but none may take the mission. The held event names the
        /// reason each captain was passed over.
        /// </summary>
        public const string NoEligibleCaptain = "no_eligible_captain";

        #endregion

        #region Public-Methods

        /// <summary>
        /// True when the hold is worth an event. Every reason except <see cref="DependencyUnfinished"/> holds a
        /// mission whose predecessor has finished or is missing, which an operator cannot read from the
        /// predecessor's own status.
        /// </summary>
        /// <param name="reason">Reason code.</param>
        /// <returns>True when a change to or from this reason is recorded as an event.</returns>
        public static bool IsNamedHold(string? reason)
        {
            if (String.IsNullOrWhiteSpace(reason)) return false;
            return !String.Equals(reason, DependencyUnfinished, StringComparison.Ordinal);
        }

        /// <summary>
        /// Drop the hold from a Cancelled mission. A cancelled mission waits for nothing, and a leftover reason reads
        /// as a live wait that hides the cancel. Every mission write applies this, so no cancel path can keep a hold.
        /// </summary>
        /// <param name="mission">Mission about to be stored.</param>
        public static void ReleaseWhenCancelled(Mission mission)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (mission.Status != MissionStatusEnum.Cancelled) return;
            mission.AssignmentHoldReason = null;
            mission.AssignmentHoldEntityId = null;
        }

        #endregion
    }
}
