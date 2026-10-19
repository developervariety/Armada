namespace Armada.Helm.Commands
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Common dispatch and fleet-action approval arguments.</summary>
    public static class RunApprovalArguments
    {
        /// <summary>Validate optional arguments and build a per-run snapshot.</summary>
        public static RunApprovalOptions? Build(bool approveAll, int? timeoutSeconds, string? decision)
        {
            if (!approveAll && timeoutSeconds == null && decision == null) return null;
            ApprovalTimeoutDecisionEnum parsed = ApprovalTimeoutDecisionEnum.Deny;
            if (decision != null && (!Enum.TryParse(decision, true, out parsed) || !Enum.IsDefined(typeof(ApprovalTimeoutDecisionEnum), parsed)))
                throw new ArgumentException("Approval default must be Deny or Approve.");
            return new RunApprovalOptions { ApproveAll = approveAll, TimeoutSeconds = timeoutSeconds ?? 600, TimeoutDecision = parsed }.Snapshot();
        }
    }
}
