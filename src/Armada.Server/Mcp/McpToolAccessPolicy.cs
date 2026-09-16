namespace Armada.Server.Mcp
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Decides which MCP tools an authenticated caller may list and call.
    ///
    /// Most of the catalog is operator control with no tenant or user scope: dispatch, landing,
    /// deployment, restore, purge and server control. Only a global administrator may use it. Any
    /// other authenticated caller reaches only the tools that apply the caller's own scope, so MCP
    /// never grants a narrower role more than the REST API does.
    /// </summary>
    public static class McpToolAccessPolicy
    {
        #region Public-Members

        /// <summary>
        /// Tools that read or change only what the caller may see under the shared ownership rule.
        /// </summary>
        public static IReadOnlyCollection<string> CallerScopedTools => _CallerScopedTools;

        #endregion

        #region Private-Members

        private static readonly HashSet<string> _CallerScopedTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "get_persona",
            "get_pipeline",
            "get_prompt_template",
            "list_prompt_templates",
            "create_memory",
            "get_memory",
            "search_memory",
            "update_memory",
            "delete_memory",

            // The captain-facing typed-decision tool and its two pre-shaped helpers. They redact
            // before egress, are budgeted per mission, record one event per call, and have no side
            // effect on any Armada record, so a mission caller may reach them like the memory tools.
            "armada_typed_decision",
            "armada_check_premise",
            "armada_memory_triage",

            // The captain-facing context fetch tool. It is read-only and informative: it returns
            // sanitized memory and docs leaf text, writes no record, and is budgeted per mission, so a
            // mission caller may reach it like the memory tools.
            "armada_fetch_context",

            // Harbor job tools apply the shared runner authorization rule to every job, and stopping also needs
            // the tenant administrator level the REST route requires.
            "armada_harbor_jobs",
            "armada_harbor_job",
            "armada_harbor_job_stop"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Decide whether a caller may list and call a tool.
        /// </summary>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="toolName">Tool name.</param>
        /// <returns>True when allowed.</returns>
        public static bool IsAllowed(AuthContext caller, string toolName)
        {
            if (caller == null || !caller.IsAuthenticated) return false;
            if (String.IsNullOrEmpty(toolName)) return false;
            if (caller.IsAdmin) return true;
            return _CallerScopedTools.Contains(toolName);
        }

        #endregion
    }
}
