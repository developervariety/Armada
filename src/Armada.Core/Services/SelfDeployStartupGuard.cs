namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Decides whether a normal admiral start may proceed. While a restart is in flight only the process the
    /// supervisor launched for that operation may start, so no second admiral can overlap the cutover.
    /// </summary>
    public static class SelfDeployStartupGuard
    {
        /// <summary>
        /// Evaluate the durable record against the operation id this process was launched with.
        /// </summary>
        /// <param name="read">Record read result.</param>
        /// <param name="launchedOperationId">Operation id from the environment, when present.</param>
        /// <returns>Decision with a stable reason.</returns>
        public static SelfDeployStartupDecision Evaluate(SelfDeployRestartRecordReadResult read, string? launchedOperationId)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            if (!read.Exists) return Allow("no_restart_record");
            if (!read.IsReadable) return Deny(String.IsNullOrWhiteSpace(read.FailureReason) ? "restart_record_unreadable" : read.FailureReason);
            SelfDeployRestartRecord record = read.Record!;
            if (SelfDeployRestartRecord.IsTerminal(record.State)) return Allow("restart_record_terminal");
            bool supervisedLaunch = !String.IsNullOrWhiteSpace(launchedOperationId)
                && String.Equals(launchedOperationId, record.OperationId, StringComparison.Ordinal)
                && (record.State == SelfDeployRestartStateEnum.CandidateStarting
                    || record.State == SelfDeployRestartStateEnum.RollbackStarting);
            if (supervisedLaunch) return Allow("supervised_launch");
            return Deny("restart_in_progress");
        }

        private static SelfDeployStartupDecision Allow(string reason)
        {
            return new SelfDeployStartupDecision { Allowed = true, Reason = reason };
        }

        private static SelfDeployStartupDecision Deny(string reason)
        {
            return new SelfDeployStartupDecision { Allowed = false, Reason = reason };
        }
    }
}
