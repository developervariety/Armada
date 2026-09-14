namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// Identity of one launch of a mission: the launch time and the agent process it started.
    /// </summary>
    /// <remarks>
    /// The completion handler de-duplicates repeat completion calls for the SAME launch, because
    /// the process-exit callback and the health check can both report one exit. Every path that
    /// returns a mission for another attempt (a Judge re-run, a refusal continuation, a transient
    /// requeue or re-route, an operator restart, a review denial, a merge-recovery redispatch, a
    /// stale-captain reset, or a stall relaunch) ends in a new launch, and a new launch always
    /// records a new process and normally a new start time. So the requeue rule lives here, once:
    /// a completion for a later launch is never treated as a duplicate of an earlier one, and no
    /// requeue path has to release the guard itself.
    /// </remarks>
    public sealed class MissionCompletionAttempt
    {
        #region Public-Members

        /// <summary>UTC time the attempt was launched.</summary>
        public DateTime StartedUtc { get; }

        /// <summary>Agent process of the attempt, when still recorded on the mission.</summary>
        public int? ProcessId { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="startedUtc">UTC launch time.</param>
        /// <param name="processId">Agent process, if recorded.</param>
        public MissionCompletionAttempt(DateTime startedUtc, int? processId)
        {
            StartedUtc = startedUtc;
            ProcessId = processId;
        }

        /// <summary>
        /// The launch attempt a mission currently records, or null when the mission has not been
        /// launched (a requeued mission waiting for assignment).
        /// </summary>
        /// <param name="mission">Mission, or null.</param>
        /// <returns>The attempt, or null when there is no launch to identify.</returns>
        public static MissionCompletionAttempt? Of(Mission? mission)
        {
            if (mission == null || !mission.StartedUtc.HasValue) return null;
            return new MissionCompletionAttempt(mission.StartedUtc.Value, mission.ProcessId);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether <paramref name="current"/> is a later launch than <paramref name="handled"/>, so
        /// its completion must be processed even inside the de-duplication window.
        /// </summary>
        /// <remarks>
        /// A mission with no launch is never a new attempt: a completion call for a requeued mission
        /// that has not been launched again is a late duplicate of the attempt already handled. A
        /// process id that one side no longer records is not evidence of a new launch, because the
        /// completion handler clears the process id as it runs.
        /// </remarks>
        /// <param name="handled">Attempt whose completion was already handled, or null when unknown.</param>
        /// <param name="current">Attempt the mission records now, or null when not launched.</param>
        /// <returns>True when the current attempt is a different, later launch.</returns>
        public static bool IsNewAttempt(MissionCompletionAttempt? handled, MissionCompletionAttempt? current)
        {
            if (current == null) return false;
            if (handled == null) return true;
            if (current.StartedUtc != handled.StartedUtc) return true;
            return current.ProcessId.HasValue
                && handled.ProcessId.HasValue
                && current.ProcessId.Value != handled.ProcessId.Value;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return "launch " + StartedUtc.ToString("o") + " process " + (ProcessId.HasValue ? ProcessId.Value.ToString() : "none");
        }

        #endregion
    }
}
