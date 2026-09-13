namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Read-only recovery detail for one mission, assembled from recorded mission counters, rescue missions, incidents,
    /// runbook executions and recovery events. Reading it dispatches nothing and does not change recovery state.
    /// </summary>
    public class MissionRecoveryReport
    {
        #region Public-Members

        /// <summary>
        /// Mission identifier.
        /// </summary>
        public string MissionId { get; set; } = "";

        /// <summary>
        /// Current mission status.
        /// </summary>
        public MissionStatusEnum Status { get; set; } = MissionStatusEnum.Pending;

        /// <summary>
        /// Redacted and bounded mission failure reason, or null.
        /// </summary>
        public string? FailureReason { get; set; } = null;

        /// <summary>
        /// Parent mission identifier when this mission is itself an autonomous rescue, or null.
        /// </summary>
        public string? ParentMissionId { get; set; } = null;

        /// <summary>
        /// Whether this mission is an autonomous rescue.
        /// </summary>
        public bool IsRescue { get; set; } = false;

        /// <summary>
        /// Rescue attempts recorded against this mission.
        /// </summary>
        public int RecoveryAttempts { get; set; } = 0;

        /// <summary>
        /// Current per-mission rescue budget from settings.
        /// </summary>
        public int MaxRecoveryAttempts { get; set; } = 0;

        /// <summary>
        /// Whether the recorded attempts have reached the current budget.
        /// </summary>
        public bool RecoveryBudgetExhausted { get; set; } = false;

        /// <summary>
        /// Whether autonomous recovery is currently enabled.
        /// </summary>
        public bool AutonomousRecoveryEnabled { get; set; } = false;

        /// <summary>
        /// Whether autonomous recovery currently dispatches rescue missions.
        /// </summary>
        public bool DispatchRescueMissions { get; set; } = false;

        /// <summary>
        /// Landing retries recorded against this mission.
        /// </summary>
        public int LandingRetryCount { get; set; } = 0;

        /// <summary>
        /// Current landing retry limit from settings.
        /// </summary>
        public int MaxLandingRetries { get; set; } = 0;

        /// <summary>
        /// UTC time of the last recorded recovery action, or null.
        /// </summary>
        public DateTime? LastRecoveryActionUtc { get; set; } = null;

        /// <summary>
        /// Rescue missions dispatched for this mission, oldest first and bounded.
        /// </summary>
        public List<MissionRecoveryRescue> Rescues { get; set; } = new List<MissionRecoveryRescue>();

        /// <summary>
        /// Whether more rescues exist than are listed.
        /// </summary>
        public bool RescuesTruncated { get; set; } = false;

        /// <summary>
        /// Reason rescues could not be listed, or null.
        /// </summary>
        public string? RescuesUnavailableReason { get; set; } = null;

        /// <summary>
        /// Incidents linked to this mission, most recently updated first and bounded.
        /// </summary>
        public List<MissionRecoveryIncident> Incidents { get; set; } = new List<MissionRecoveryIncident>();

        /// <summary>
        /// Whether more incidents exist than are listed.
        /// </summary>
        public bool IncidentsTruncated { get; set; } = false;

        /// <summary>
        /// Reason incidents could not be listed, or null.
        /// </summary>
        public string? IncidentsUnavailableReason { get; set; } = null;

        /// <summary>
        /// Recovery events for this mission found among its most recent events, newest first.
        /// </summary>
        public List<MissionRecoveryEvent> Events { get; set; } = new List<MissionRecoveryEvent>();

        /// <summary>
        /// Whether the event window was full, so older recovery events may exist that are not listed.
        /// </summary>
        public bool EventsWindowFull { get; set; } = false;

        /// <summary>
        /// Reason events could not be listed, or null.
        /// </summary>
        public string? EventsUnavailableReason { get; set; } = null;

        #endregion
    }
}
