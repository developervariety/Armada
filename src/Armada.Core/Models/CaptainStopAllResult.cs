namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Result of an emergency stop of every working captain, planning session and objective refinement session.
    /// REST, MCP and WebSocket return the same result.
    /// </summary>
    public class CaptainStopAllResult
    {
        #region Public-Members

        /// <summary>
        /// Status value when every captain and session stopped.
        /// </summary>
        public const string AllStoppedStatus = "all_stopped";

        /// <summary>
        /// Status value when at least one captain or session could not be stopped.
        /// </summary>
        public const string PartialStatus = "stopped_with_failures";

        /// <summary>
        /// <see cref="UnavailableSources"/> entry when the provider does not store planning sessions.
        /// </summary>
        public const string PlanningSessionsSource = "PlanningSessions";

        /// <summary>
        /// <see cref="UnavailableSources"/> entry when the provider does not store objective refinement sessions.
        /// </summary>
        public const string RefinementSessionsSource = "RefinementSessions";

        /// <summary>
        /// <see cref="AllStoppedStatus"/> when <see cref="Failed"/> is zero, otherwise <see cref="PartialStatus"/>.
        /// </summary>
        public string Status => Failed == 0 ? AllStoppedStatus : PartialStatus;

        /// <summary>
        /// Total captains and sessions stopped.
        /// </summary>
        public int Stopped => CaptainsStopped + PlanningSessionsStopped + RefinementSessionsStopped;

        /// <summary>
        /// Total captains and sessions that could not be stopped.
        /// </summary>
        public int Failed => CaptainsFailed + PlanningSessionsFailed + RefinementSessionsFailed;

        /// <summary>
        /// Working captains recalled.
        /// </summary>
        public int CaptainsStopped { get; set; } = 0;

        /// <summary>
        /// Working captains whose recall failed.
        /// </summary>
        public int CaptainsFailed { get; set; } = 0;

        /// <summary>
        /// Active planning sessions stopped.
        /// </summary>
        public int PlanningSessionsStopped { get; set; } = 0;

        /// <summary>
        /// Active planning sessions whose stop failed.
        /// </summary>
        public int PlanningSessionsFailed { get; set; } = 0;

        /// <summary>
        /// Active objective refinement sessions stopped.
        /// </summary>
        public int RefinementSessionsStopped { get; set; } = 0;

        /// <summary>
        /// Active objective refinement sessions whose stop failed.
        /// </summary>
        public int RefinementSessionsFailed { get; set; } = 0;

        /// <summary>
        /// One entry per captain or session that could not be stopped.
        /// </summary>
        public List<CaptainStopFailure> Failures { get; set; } = new List<CaptainStopFailure>();

        /// <summary>
        /// Session sources the database provider does not store, so no session of that kind can be running.
        /// A named source is not a failure; it says why that kind reports zero.
        /// </summary>
        public List<string> UnavailableSources { get; set; } = new List<string>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public CaptainStopAllResult()
        {
        }

        #endregion
    }
}
