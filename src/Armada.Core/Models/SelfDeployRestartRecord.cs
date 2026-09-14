namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Durable record of one supervised self-deploy restart. The running admiral writes
    /// <see cref="SelfDeployRestartStateEnum.Prepared"/> and <see cref="SelfDeployRestartStateEnum.ExitRequested"/>;
    /// the supervisor writes every other state.
    /// </summary>
    public sealed class SelfDeployRestartRecord
    {
        /// <summary>
        /// Maximum retained transitions.
        /// </summary>
        public const int MaximumTransitions = 64;

        /// <summary>
        /// Record format version.
        /// </summary>
        public int FormatVersion { get; set; } = 1;

        /// <summary>
        /// Unique operation id. A supervised launch carries it so the startup guard admits only that process.
        /// </summary>
        public string OperationId { get; set; } = String.Empty;

        /// <summary>
        /// Current state.
        /// </summary>
        public SelfDeployRestartStateEnum State { get; set; } = SelfDeployRestartStateEnum.Prepared;

        /// <summary>
        /// Stable reason for the current state.
        /// </summary>
        public string Reason { get; set; } = String.Empty;

        /// <summary>
        /// Creation time.
        /// </summary>
        public DateTime CreatedUtc { get; set; }

        /// <summary>
        /// Last write time.
        /// </summary>
        public DateTime UpdatedUtc { get; set; }

        /// <summary>
        /// Admiral process that owned the service before the cutover.
        /// </summary>
        public SelfDeployProcessIdentity? OldProcess { get; set; }

        /// <summary>
        /// Supervisor process identity.
        /// </summary>
        public SelfDeployProcessIdentity? SupervisorProcess { get; set; }

        /// <summary>
        /// Candidate process identity once launched.
        /// </summary>
        public SelfDeployProcessIdentity? CandidateProcess { get; set; }

        /// <summary>
        /// Rollback process identity once launched.
        /// </summary>
        public SelfDeployProcessIdentity? RollbackProcess { get; set; }

        /// <summary>
        /// Immutable candidate artifact.
        /// </summary>
        public SelfDeployReleaseArtifact Candidate { get; set; } = new SelfDeployReleaseArtifact();

        /// <summary>
        /// Immutable rollback artifact captured from the running admiral before the build.
        /// </summary>
        public SelfDeployReleaseArtifact Rollback { get; set; } = new SelfDeployReleaseArtifact();

        /// <summary>
        /// Database schema version read by the running admiral before cutover.
        /// </summary>
        public int SchemaVersionBefore { get; set; }

        /// <summary>
        /// Health endpoint probed after each launch.
        /// </summary>
        public string HealthUrl { get; set; } = String.Empty;

        /// <summary>
        /// Time the supervisor confirmed that the previous admiral had exited.
        /// </summary>
        public DateTime? OldExitConfirmedUtc { get; set; }

        /// <summary>
        /// Ordered, bounded state history.
        /// </summary>
        public List<SelfDeployRestartTransition> Transitions { get; set; } = new List<SelfDeployRestartTransition>();

        /// <summary>
        /// Whether the state ends the operation.
        /// </summary>
        /// <param name="state">State to test.</param>
        /// <returns>True for a terminal state.</returns>
        public static bool IsTerminal(SelfDeployRestartStateEnum state)
        {
            return state == SelfDeployRestartStateEnum.Committed
                || state == SelfDeployRestartStateEnum.RolledBack
                || state == SelfDeployRestartStateEnum.RollbackBlocked
                || state == SelfDeployRestartStateEnum.Aborted
                || state == SelfDeployRestartStateEnum.Failed;
        }

        /// <summary>
        /// Move to a new state and append a bounded transition entry.
        /// </summary>
        /// <param name="state">New state.</param>
        /// <param name="reason">Stable reason.</param>
        public void MoveTo(SelfDeployRestartStateEnum state, string reason)
        {
            DateTime now = DateTime.UtcNow;
            State = state;
            Reason = reason ?? String.Empty;
            UpdatedUtc = now;
            Transitions.Add(new SelfDeployRestartTransition { State = state, Utc = now, Reason = Reason });
            if (Transitions.Count > MaximumTransitions)
                Transitions.RemoveRange(0, Transitions.Count - MaximumTransitions);
        }
    }
}
