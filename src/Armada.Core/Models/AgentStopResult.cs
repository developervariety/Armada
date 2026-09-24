namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Outcome of a request to stop an agent process. A refused stop names its reason, so a caller never reports a
    /// process that may still be running as stopped.
    /// </summary>
    public sealed class AgentStopResult
    {
        #region Public-Members

        /// <summary>Reason a stop was refused because the live process is not provably the launched agent.</summary>
        public const string IdentityUnverifiedReason = "process_identity_unverified";

        /// <summary>Reason a stop was refused because an unrelated process holds the identifier.</summary>
        public const string IdentifierReusedReason = "process_identifier_reused";

        /// <summary>Reason a stop was refused because the kill failed.</summary>
        public const string KillFailedReason = "kill_failed";

        /// <summary>Reason a stop was refused because stopping the work behind a synthetic identifier failed.</summary>
        public const string SyntheticStopFailedReason = "synthetic_stop_failed";

        /// <summary>What the stop did.</summary>
        public AgentStopOutcomeEnum Outcome { get; }

        /// <summary>Why the stop was refused; null unless <see cref="Outcome"/> is <see cref="AgentStopOutcomeEnum.Refused"/>.</summary>
        public string? Reason { get; }

        /// <summary>Human-readable detail for a log line, or null.</summary>
        public string? Detail { get; }

        /// <summary>True when the stop was refused and the process may still be running.</summary>
        public bool IsRefused => Outcome == AgentStopOutcomeEnum.Refused;

        #endregion

        #region Constructors-and-Factories

        private AgentStopResult(AgentStopOutcomeEnum outcome, string? reason, string? detail)
        {
            Outcome = outcome;
            Reason = reason;
            Detail = detail;
        }

        /// <summary>The process was stopped.</summary>
        /// <returns>A stopped result.</returns>
        public static AgentStopResult Stopped() => new AgentStopResult(AgentStopOutcomeEnum.Stopped, null, null);

        /// <summary>No process was running under the identifier.</summary>
        /// <returns>A not-running result.</returns>
        public static AgentStopResult NotRunning() => new AgentStopResult(AgentStopOutcomeEnum.NotRunning, null, null);

        /// <summary>The stop was refused.</summary>
        /// <param name="reason">Named reason, such as <see cref="IdentityUnverifiedReason"/>.</param>
        /// <param name="detail">Human-readable detail.</param>
        /// <returns>A refused result.</returns>
        public static AgentStopResult Refused(string reason, string? detail = null)
        {
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentNullException(nameof(reason));
            return new AgentStopResult(AgentStopOutcomeEnum.Refused, reason, detail);
        }

        #endregion

        #region Public-Methods

        /// <summary>Describe the outcome for a log line.</summary>
        /// <returns>The outcome, and the reason and detail when refused.</returns>
        public override string ToString()
        {
            if (!IsRefused) return Outcome.ToString();
            return "Refused (" + Reason + ")" + (String.IsNullOrEmpty(Detail) ? "" : ": " + Detail);
        }

        #endregion
    }
}
