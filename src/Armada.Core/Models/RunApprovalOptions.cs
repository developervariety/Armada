namespace Armada.Core.Models
{
    using System;
    using System.Text.Json;
    using Armada.Core.Enums;

    /// <summary>Approval choices scoped to one dispatch or fleet-action run.</summary>
    public class RunApprovalOptions
    {
        /// <summary>Bypass tool prompts and human mission reviews for this run.</summary>
        public bool ApproveAll { get; set; } = false;
        /// <summary>Approval countdown in seconds, from 10 to 3600.</summary>
        public int TimeoutSeconds { get; set; } = 600;
        /// <summary>Decision when the countdown expires. Default Deny.</summary>
        public ApprovalTimeoutDecisionEnum TimeoutDecision { get; set; } = ApprovalTimeoutDecisionEnum.Deny;

        /// <summary>Validate and snapshot choices before dispatch begins.</summary>
        public RunApprovalOptions Snapshot()
        {
            if (TimeoutSeconds < 10 || TimeoutSeconds > 3600) throw new ArgumentException("Approval timeout must be between 10 and 3600 seconds.");
            if (!Enum.IsDefined(typeof(ApprovalTimeoutDecisionEnum), TimeoutDecision)) throw new ArgumentException("Unknown approval timeout decision.");
            return new RunApprovalOptions { ApproveAll = ApproveAll, TimeoutSeconds = TimeoutSeconds, TimeoutDecision = TimeoutDecision };
        }

        /// <summary>Validate privilege for unattended approvals without altering saved policies.</summary>
        public void ValidateFor(AuthContext caller)
        {
            Snapshot();
            if ((ApproveAll || TimeoutDecision == ApprovalTimeoutDecisionEnum.Approve) && (!String.IsNullOrEmpty(caller.AskThreadId) || !String.IsNullOrEmpty(caller.MissionId) || (!caller.IsAdmin && !caller.IsTenantAdmin)))
                throw new UnauthorizedAccessException("Only administrators may select Approve all or automatic approval on timeout.");
        }

        /// <summary>Deserialize a persisted snapshot.</summary>
        public static RunApprovalOptions? FromJson(string? json) => String.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<RunApprovalOptions>(json);
        /// <summary>Serialize a snapshot; preserve null for legacy runs.</summary>
        public static string? ToJson(RunApprovalOptions? options) => options == null ? null : JsonSerializer.Serialize(options);
    }
}
