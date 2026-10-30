namespace Armada.Server.Mcp
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>The explicit saved-chat catalog; mutations always require a human proposal decision.</summary>
    public static class AskToolPolicy
    {
        /// <summary>MCP tool that answers a Claude Code saved turn's native tool permission prompts.</summary>
        public const string PermissionPromptToolName = "armada_cli_permission_prompt";

        private static readonly HashSet<string> _Reads = new HashSet<string>(StringComparer.Ordinal)
        {
            "armada_status", "armada_enumerate", "armada_get_fleet", "armada_get_vessel", "armada_suggest_vessel_toolchain",
            "armada_get_captain", "armada_get_dock", "armada_voyage_status", "armada_mission_status",
            "armada_mission_output", "armada_get_mission_diff", "armada_get_mission_log",
            "armada_mission_change_summary", "armada_mission_attempts", "armada_job_status", "list_objectives", "get_objective",
            "list_backlog", "get_backlog_item", "preview_objective_dispatch", "get_check_run",
            "get_persona", "get_pipeline", "get_prompt_template", "list_prompt_templates", "get_playbook",
            "armada_campaign_status", "token_usage_summary",
            "armada_index_status", "armada_code_search", "armada_fleet_code_search",
            "armada_get_merge_entry", "armada_list_incidents", "armada_get_incident"
        };
        private static readonly HashSet<string> _Proposals = new HashSet<string>(StringComparer.Ordinal)
        {
            "create_objective", "update_objective", "create_backlog_item", "update_backlog_item",
            "armada_dispatch", "armada_create_mission", "armada_update_mission",
            "armada_cancel_mission", "armada_cancel_voyage", "armada_restart_mission", "armada_retry_landing"
        };

        /// <summary>Whether a tool can run without changing Armada records or starting work.</summary>
        public static bool IsRead(string toolName) => _Reads.Contains(toolName);

        /// <summary>Whether a tool is the permission prompt tool, which only a saved turn's own credential can reach.</summary>
        public static bool IsPermissionPrompt(string toolName) => String.Equals(toolName, PermissionPromptToolName, StringComparison.Ordinal);

        /// <summary>Whether a tool can become a saved, manually approved action.</summary>
        public static bool CanPropose(string toolName) => _Proposals.Contains(toolName);

        /// <summary>Whether a caller carries any saved-chat claim, including an invalid partial claim.</summary>
        public static bool HasScope(AuthContext caller) => !String.IsNullOrWhiteSpace(caller.AskThreadId)
            || !String.IsNullOrWhiteSpace(caller.AskTurnId);

        /// <summary>Check saved-chat scope before the ordinary operator administrator exception.</summary>
        public static bool IsAllowed(AuthContext caller, string toolName)
        {
            return caller.IsAuthenticated && caller.IsAdmin && String.IsNullOrWhiteSpace(caller.MissionId)
                && !String.IsNullOrWhiteSpace(caller.AskThreadId) && !String.IsNullOrWhiteSpace(caller.AskTurnId)
                && (_Reads.Contains(toolName) || _Proposals.Contains(toolName) || IsPermissionPrompt(toolName));
        }
    }
}
