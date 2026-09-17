namespace Armada.Server.WebSocket
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// The one declaration of who may run each WebSocket command. The command handler dispatches only commands
    /// declared here and enforces the declared rule before a command runs, for every caller.
    ///
    /// A command's rule is the stricter of its REST route and its MCP tool. The MCP surface reserves most of its
    /// catalog for global administrators and lets any other caller reach only tools that apply the caller's own
    /// scope, so a command is <see cref="WebSocketCommandRuleEnum.GlobalAdmin"/> unless both its REST route and its
    /// MCP tool (when one exists) admit a narrower caller through the shared ownership rule. A change both surfaces
    /// admit for tenant administrators is <see cref="WebSocketCommandRuleEnum.TenantAdminScoped"/>.
    /// </summary>
    public static class WebSocketCommandRegistry
    {
        #region Public-Members

        /// <summary>
        /// Every declared command rule, in handler order.
        /// </summary>
        public static IReadOnlyList<WebSocketCommandRule> Rules => _Rules;

        #endregion

        #region Private-Members

        private static readonly List<WebSocketCommandRule> _Rules = new List<WebSocketCommandRule>
        {
            Rule("status", WebSocketCommandOperationEnum.Read, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/status", "armada_status"),
            Rule("stop_captain", WebSocketCommandOperationEnum.Action, WebSocketCommandRuleEnum.GlobalAdmin, "POST", "/api/v1/captains/{id}/stop", "armada_stop_captain"),
            Rule("stop_all", WebSocketCommandOperationEnum.Action, WebSocketCommandRuleEnum.GlobalAdmin, "POST", "/api/v1/captains/stop-all", "armada_stop_all"),
            Rule("stop_server", WebSocketCommandOperationEnum.Action, WebSocketCommandRuleEnum.GlobalAdmin, "POST", "/api/v1/server/stop", "armada_stop_server"),
            Rule("list_fleets", WebSocketCommandOperationEnum.List, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/fleets", "armada_enumerate"),
            Rule("get_fleet", WebSocketCommandOperationEnum.Read, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/fleets/{id}", "armada_get_fleet"),
            Rule("create_fleet", WebSocketCommandOperationEnum.Create, WebSocketCommandRuleEnum.GlobalAdmin, "POST", "/api/v1/fleets", "armada_create_fleet"),
            Rule("update_fleet", WebSocketCommandOperationEnum.Update, WebSocketCommandRuleEnum.GlobalAdmin, "PUT", "/api/v1/fleets/{id}", "armada_update_fleet"),
            Rule("delete_fleet", WebSocketCommandOperationEnum.Delete, WebSocketCommandRuleEnum.GlobalAdmin, "DELETE", "/api/v1/fleets/{id}", "armada_delete_fleet"),
            Rule("list_vessels", WebSocketCommandOperationEnum.List, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/vessels", "armada_enumerate"),
            Rule("get_vessel", WebSocketCommandOperationEnum.Read, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/vessels/{id}", "armada_get_vessel"),
            Rule("create_vessel", WebSocketCommandOperationEnum.Create, WebSocketCommandRuleEnum.GlobalAdmin, "POST", "/api/v1/vessels", "armada_add_vessel"),
            Rule("update_vessel", WebSocketCommandOperationEnum.Update, WebSocketCommandRuleEnum.GlobalAdmin, "PUT", "/api/v1/vessels/{id}", "armada_update_vessel"),
            Rule("update_vessel_context", WebSocketCommandOperationEnum.Update, WebSocketCommandRuleEnum.GlobalAdmin, "PATCH", "/api/v1/vessels/{id}/context", "armada_update_vessel_context"),
            Rule("delete_vessel", WebSocketCommandOperationEnum.Delete, WebSocketCommandRuleEnum.GlobalAdmin, "DELETE", "/api/v1/vessels/{id}", "armada_delete_vessel"),
            Rule("list_voyages", WebSocketCommandOperationEnum.List, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/voyages", "armada_enumerate"),
            Rule("get_voyage", WebSocketCommandOperationEnum.Read, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/voyages/{id}", "armada_voyage_status"),
            Rule("create_voyage", WebSocketCommandOperationEnum.Create, WebSocketCommandRuleEnum.GlobalAdmin, "POST", "/api/v1/voyages", "armada_dispatch"),
            Rule("cancel_voyage", WebSocketCommandOperationEnum.Action, WebSocketCommandRuleEnum.GlobalAdmin, "DELETE", "/api/v1/voyages/{id}", "armada_cancel_voyage"),
            Rule("purge_voyage", WebSocketCommandOperationEnum.Delete, WebSocketCommandRuleEnum.GlobalAdmin, "DELETE", "/api/v1/voyages/{id}/purge", "armada_purge_voyage"),
            Rule("list_missions", WebSocketCommandOperationEnum.List, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/missions", "armada_enumerate"),
            Rule("list_missions_summary", WebSocketCommandOperationEnum.List, WebSocketCommandRuleEnum.ListScoped, "GET", "/api/v1/missions/summaries", null),
            Rule("get_mission", WebSocketCommandOperationEnum.Read, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/missions/{id}", "armada_mission_status"),
            Rule("create_mission", WebSocketCommandOperationEnum.Create, WebSocketCommandRuleEnum.GlobalAdmin, "POST", "/api/v1/missions", "armada_create_mission"),
            Rule("update_mission", WebSocketCommandOperationEnum.Update, WebSocketCommandRuleEnum.GlobalAdmin, "PUT", "/api/v1/missions/{id}", "armada_update_mission"),
            Rule("transition_mission_status", WebSocketCommandOperationEnum.Action, WebSocketCommandRuleEnum.GlobalAdmin, "PUT", "/api/v1/missions/{id}/status", "armada_transition_mission_status"),
            Rule("cancel_mission", WebSocketCommandOperationEnum.Action, WebSocketCommandRuleEnum.GlobalAdmin, "DELETE", "/api/v1/missions/{id}", "armada_cancel_mission"),
            Rule("purge_mission", WebSocketCommandOperationEnum.Delete, WebSocketCommandRuleEnum.GlobalAdmin, "DELETE", "/api/v1/missions/{id}/purge", "armada_purge_mission"),
            Rule("restart_mission", WebSocketCommandOperationEnum.Action, WebSocketCommandRuleEnum.GlobalAdmin, "POST", "/api/v1/missions/{id}/restart", "armada_restart_mission"),
            Rule("get_mission_diff", WebSocketCommandOperationEnum.Read, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/missions/{id}/diff", "armada_get_mission_diff"),
            Rule("get_mission_log", WebSocketCommandOperationEnum.Read, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/missions/{id}/log", "armada_get_mission_log"),
            Rule("list_captains", WebSocketCommandOperationEnum.List, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/captains", "armada_enumerate"),
            Rule("get_captain", WebSocketCommandOperationEnum.Read, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/captains/{id}", "armada_get_captain"),
            Rule("create_captain", WebSocketCommandOperationEnum.Create, WebSocketCommandRuleEnum.GlobalAdmin, "POST", "/api/v1/captains", "armada_create_captain"),
            Rule("update_captain", WebSocketCommandOperationEnum.Update, WebSocketCommandRuleEnum.GlobalAdmin, "PUT", "/api/v1/captains/{id}", "armada_update_captain"),
            Rule("delete_captain", WebSocketCommandOperationEnum.Delete, WebSocketCommandRuleEnum.GlobalAdmin, "DELETE", "/api/v1/captains/{id}", "armada_delete_captain"),
            Rule("get_captain_log", WebSocketCommandOperationEnum.Read, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/captains/{id}/log", "armada_get_captain_log"),
            Rule("list_signals", WebSocketCommandOperationEnum.List, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/signals", "armada_enumerate"),
            Rule("send_signal", WebSocketCommandOperationEnum.Create, WebSocketCommandRuleEnum.GlobalAdmin, "POST", "/api/v1/signals", "armada_send_signal"),
            Rule("list_events", WebSocketCommandOperationEnum.List, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/events", "armada_enumerate"),
            Rule("list_docks", WebSocketCommandOperationEnum.List, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/docks", "armada_enumerate"),
            Rule("list_merge_queue", WebSocketCommandOperationEnum.List, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/merge-queue", "armada_enumerate"),
            Rule("get_merge_entry", WebSocketCommandOperationEnum.Read, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/merge-queue/{id}", "armada_get_merge_entry"),
            Rule("enqueue_merge", WebSocketCommandOperationEnum.Create, WebSocketCommandRuleEnum.GlobalAdmin, "POST", "/api/v1/merge-queue", "armada_enqueue_merge"),
            Rule("cancel_merge", WebSocketCommandOperationEnum.Delete, WebSocketCommandRuleEnum.GlobalAdmin, "DELETE", "/api/v1/merge-queue/{id}", "armada_cancel_merge"),
            Rule("process_merge_queue", WebSocketCommandOperationEnum.Action, WebSocketCommandRuleEnum.GlobalAdmin, "POST", "/api/v1/merge-queue/process", "armada_process_merge_queue"),
            Rule("enumerate", WebSocketCommandOperationEnum.List, WebSocketCommandRuleEnum.GlobalAdmin, "POST", "/api/v1/fleets/enumerate", "armada_enumerate"),
            Rule("backup", WebSocketCommandOperationEnum.Action, WebSocketCommandRuleEnum.GlobalAdmin, "GET", "/api/v1/backup", "armada_backup"),
            Rule("restore", WebSocketCommandOperationEnum.Action, WebSocketCommandRuleEnum.GlobalAdmin, "POST", "/api/v1/restore", "armada_restore"),
            Rule("get_persona", WebSocketCommandOperationEnum.Read, WebSocketCommandRuleEnum.ReadScoped, "GET", "/api/v1/personas/{name}", "get_persona"),
            Rule("create_persona", WebSocketCommandOperationEnum.Create, WebSocketCommandRuleEnum.TenantAdminScoped, "POST", "/api/v1/personas", "create_persona"),
            Rule("update_persona", WebSocketCommandOperationEnum.Update, WebSocketCommandRuleEnum.TenantAdminScoped, "PUT", "/api/v1/personas/{name}", "update_persona"),
            Rule("delete_persona", WebSocketCommandOperationEnum.Delete, WebSocketCommandRuleEnum.TenantAdminScoped, "DELETE", "/api/v1/personas/{name}", "delete_persona"),
            Rule("get_prompt_template", WebSocketCommandOperationEnum.Read, WebSocketCommandRuleEnum.ReadScoped, "GET", "/api/v1/prompt-templates/{name}", "get_prompt_template"),
            Rule("update_prompt_template", WebSocketCommandOperationEnum.Update, WebSocketCommandRuleEnum.GlobalAdmin, "PUT", "/api/v1/prompt-templates/{name}", "update_prompt_template"),
            Rule("get_pipeline", WebSocketCommandOperationEnum.Read, WebSocketCommandRuleEnum.ReadScoped, "GET", "/api/v1/pipelines/{name}", "get_pipeline"),
            Rule("create_pipeline", WebSocketCommandOperationEnum.Create, WebSocketCommandRuleEnum.TenantAdminScoped, "POST", "/api/v1/pipelines", "create_pipeline"),
            Rule("update_pipeline", WebSocketCommandOperationEnum.Update, WebSocketCommandRuleEnum.TenantAdminScoped, "PUT", "/api/v1/pipelines/{name}", "update_pipeline"),
            Rule("delete_pipeline", WebSocketCommandOperationEnum.Delete, WebSocketCommandRuleEnum.TenantAdminScoped, "DELETE", "/api/v1/pipelines/{name}", "delete_pipeline")
        };

        private static readonly Dictionary<string, WebSocketCommandRule> _ByAction = BuildIndex();

        #endregion

        #region Public-Methods

        /// <summary>
        /// Find the rule declared for a command.
        /// </summary>
        /// <param name="action">Command action name.</param>
        /// <param name="rule">The declared rule, or null.</param>
        /// <returns>True when the command is declared.</returns>
        public static bool TryGetRule(string action, out WebSocketCommandRule? rule)
        {
            rule = null;
            if (String.IsNullOrEmpty(action)) return false;
            if (!_ByAction.TryGetValue(action, out WebSocketCommandRule? found)) return false;
            rule = found;
            return true;
        }

        /// <summary>
        /// Decide whether a caller may run a command. An undeclared command, a missing or unauthenticated caller,
        /// and a caller without the declared role are refused. A scoped command is admitted here and then finds its
        /// records through the shared caller scope.
        /// </summary>
        /// <param name="action">Command action name.</param>
        /// <param name="caller">Session caller, or null.</param>
        /// <returns>Null when the command may run; otherwise the refusal.</returns>
        public static WebSocketCommandRefusal? Authorize(string action, AuthContext? caller)
        {
            if (!TryGetRule(action, out WebSocketCommandRule? rule) || rule == null)
                return WebSocketCommandRefusal.UnknownCommand(action ?? "");
            if (caller == null || !caller.IsAuthenticated)
                return new WebSocketCommandRefusal(WebSocketCommandRefusal.AuthenticationRequiredCode, action + " requires an authenticated caller");
            if (rule.Rule == WebSocketCommandRuleEnum.GlobalAdmin && !caller.IsAdmin)
                return new WebSocketCommandRefusal(WebSocketCommandRefusal.GlobalAdministratorRequiredCode, action + " requires a global administrator");
            if (rule.Rule == WebSocketCommandRuleEnum.TenantAdminScoped && !caller.IsAdmin && !caller.IsTenantAdmin)
                return new WebSocketCommandRefusal(WebSocketCommandRefusal.TenantAdministratorRequiredCode, action + " requires a global or tenant administrator");
            return null;
        }

        #endregion

        #region Private-Methods

        private static WebSocketCommandRule Rule(
            string action,
            WebSocketCommandOperationEnum operation,
            WebSocketCommandRuleEnum rule,
            string? restMethod,
            string? restPath,
            string? mcpTool)
        {
            return new WebSocketCommandRule(action, operation, rule, restMethod, restPath, mcpTool);
        }

        private static Dictionary<string, WebSocketCommandRule> BuildIndex()
        {
            Dictionary<string, WebSocketCommandRule> index = new Dictionary<string, WebSocketCommandRule>(StringComparer.Ordinal);
            foreach (WebSocketCommandRule rule in _Rules)
            {
                if (index.ContainsKey(rule.Action))
                    throw new InvalidOperationException("WebSocket command " + rule.Action + " is declared twice.");
                index[rule.Action] = rule;
            }
            return index;
        }

        #endregion
    }
}
