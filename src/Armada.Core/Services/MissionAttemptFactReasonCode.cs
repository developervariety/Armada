namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Exact stable reason codes that may be stored on mission attempt facts.
    /// Unknown values remain unclassified; caller text is never converted into a code.
    /// </summary>
    public static class MissionAttemptFactReasonCode
    {
        private static readonly HashSet<string> _Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "landed",
            "pull_request_merged",
            "restarted",
            "judge_check_wait",
            "judge_revision_check_wait",
            "missing_judge_verdict",
            "interrupted_process_exit",
            "transient_captain_failure",
            "stall_recovery_deferred_dispatch_hold",
            "captain_recovery_relaunch",
            "lineage_cycle",
            "lineage_parent_missing",
            "lineage_depth_exceeded",
            "stage_watchdog_no_captain_heartbeat",
            "runtime_crash",
            "runtime_usage_limit",
            "runtime_auth_failure",
            "provider_timeout",
            "dod_compile",
            "dod_test_fail",
            "dod_timeout",
            "dod_infra",
            "dod_post_test_step",
            "unknown",
            "captain_blocked",
            "judge_needs_revision",
            "judge_failed_checks",
            "judge_missing_checks",
            "report_incomplete",
            "planner_committed_code",
            "operator_review_hold_failed",
            "no_op_missing_terminal_marker",
            "no_op_claimed_without_work",
            "start_from_ref_missing",
            "landing_handoff_failed",
            "terminal_voyage_work_unlanded",
            "terminal_voyage_commit_absent",
            "terminal_voyage_no_commit",
            "worker_produced_no_commits",
            "rescue_produced_no_commits",
            "landing_push_failed",
            "landing_merge_failed",
            "landing_source_unavailable",
            "dispatch_hold_active"
        };

        /// <summary>Returns the canonical allow-listed value, or null for an unknown value.</summary>
        public static string? Normalize(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            string candidate = value.Trim();
            if (_Known.Contains(candidate)) return candidate.ToLowerInvariant();
            const string judgePrefix = "judge_pass_";
            if (candidate.StartsWith(judgePrefix, StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse(candidate.Substring(judgePrefix.Length), true, out ReviewSubstanceRuleCategory category)
                && category != ReviewSubstanceRuleCategory.Valid
                && Enum.IsDefined(typeof(ReviewSubstanceRuleCategory), category)
                && String.Equals(candidate.Substring(judgePrefix.Length), category.ToString(), StringComparison.OrdinalIgnoreCase))
                return judgePrefix + category.ToString().ToLowerInvariant();
            const string reviewPrefix = "review_denied_";
            if (candidate.StartsWith(reviewPrefix, StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse(candidate.Substring(reviewPrefix.Length), true, out ReviewDenyActionEnum action)
                && Enum.IsDefined(typeof(ReviewDenyActionEnum), action)
                && String.Equals(candidate.Substring(reviewPrefix.Length), action.ToString(), StringComparison.OrdinalIgnoreCase))
                return reviewPrefix + action.ToString().ToLowerInvariant();
            return null;
        }

        /// <summary>Read only exact protocol markers and allow-listed failure prefixes; unknown causes stay unknown.</summary>
        public static string? FromMissionFailure(Mission mission)
        {
            if (CaptainBlockedResult.IsBlockedFailure(mission.FailureReason)) return "captain_blocked";
            if (mission.FailureReason == "Judge verdict: NEEDS_REVISION") return "judge_needs_revision";
            string reason = mission.FailureReason ?? String.Empty;
            if (reason.StartsWith("no_op_completion_detected:", StringComparison.Ordinal))
                return ProgressParser.HasTerminalMarker(mission.AgentOutput) ? "no_op_claimed_without_work" : "no_op_missing_terminal_marker";
            int colon = reason.IndexOf(':');
            return colon > 0 ? Normalize(reason.Substring(0, colon)) : Normalize(reason);
        }

        /// <summary>Maps a typed runtime result to its stable attempt-fact reason.</summary>
        public static string? FromRuntimeFailure(RuntimeFailureKindEnum failure)
        {
            return failure switch
            {
                RuntimeFailureKindEnum.UsageLimit => "runtime_usage_limit",
                RuntimeFailureKindEnum.AuthFailure => "runtime_auth_failure",
                RuntimeFailureKindEnum.Crash => "runtime_crash",
                _ => null
            };
        }

        /// <summary>Maps a typed DoD result to its stable attempt-fact reason.</summary>
        public static string? FromDefinitionOfDoneFailure(DefinitionOfDoneFailureClassEnum? failure)
        {
            return failure switch
            {
                DefinitionOfDoneFailureClassEnum.Compile => "dod_compile",
                DefinitionOfDoneFailureClassEnum.TestFail => "dod_test_fail",
                DefinitionOfDoneFailureClassEnum.Timeout => "dod_timeout",
                DefinitionOfDoneFailureClassEnum.Infra => "dod_infra",
                DefinitionOfDoneFailureClassEnum.PostTestStep => "dod_post_test_step",
                _ => null
            };
        }
    }
}
