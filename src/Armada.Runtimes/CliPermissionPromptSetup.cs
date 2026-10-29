namespace Armada.Runtimes
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Runtimes.Interfaces;

    /// <summary>
    /// Points a CLI runtime's permission prompts at Armada's <c>cli_permission_prompt</c> tool on the scoped "armada" MCP
    /// server (Approve in Armada). Used on the Admiral host and on a Harbor, so both launch a captain the same way:
    /// Claude Code through <c>--permission-prompt-tool mcp__armada__cli_permission_prompt</c>, Mux 1.5.0 or later
    /// through <c>mux print --permission-prompt-tool armada.cli_permission_prompt</c>, and pi through
    /// <c>--extension &lt;bin&gt;/Pi/armada-permission-gate.ts</c>, whose tool_call hook calls the same tool. The launch must
    /// carry a scoped MCP token, or the prompt tool is unreachable and every prompt is denied.
    /// </summary>
    public static class CliPermissionPromptSetup
    {
        #region Public-Members

        /// <summary>
        /// Seconds the CLI waits for a prompt beyond Armada's own prompt timeout, so Armada expires (and denies) a request
        /// first.
        /// </summary>
        public const int CliTimeoutMarginSeconds = 120;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Configure a CLI runtime for a policy. Every policy but ApproveInArmada clears the prompt tool, so a reused
        /// runtime instance never keeps one.
        /// </summary>
        /// <param name="runtime">Runtime instance for this launch.</param>
        /// <param name="effective">Effective policy.</param>
        /// <param name="promptTimeoutSeconds">Seconds Armada waits for a decision.</param>
        /// <returns>True when the runtime will send its permission prompts to Armada.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="runtime"/> is null.</exception>
        public static bool Configure(IAgentRuntime runtime, CliPermissionPolicyEnum effective, int promptTimeoutSeconds)
        {
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));
            bool approve = effective == CliPermissionPolicyEnum.ApproveInArmada;

            if (runtime is CodexRuntime codex)
            {
                // full-auto still uses workspace-write on Unix. An explicitly resolved
                // Bypass policy must use the actual bypass flag, including on Harbors.
                codex.ApprovalMode = effective == CliPermissionPolicyEnum.Bypass ? "dangerous" : "full-auto";
                return false; // Codex has no Armada permission prompt hook.
            }

            if (runtime is ClaudeCodeRuntime claude)
            {
                claude.PermissionPromptTool = approve ? CliPermissionPolicyResolver.ClaudePromptToolName : null;
                claude.PermissionPromptTimeoutSeconds = approve ? promptTimeoutSeconds : null;
                return approve;
            }

            if (runtime is PiRuntime pi)
            {
                // pi never prompts: Armada's permission gate extension asks cli_permission_prompt from pi's tool_call
                // hook. Without the extension file the launch stays on the Refuse tool exclusion.
                pi.PermissionGateExtensionPath = null;
                pi.PermissionPromptTimeoutSeconds = null;
                if (!approve) return false;
                string? extension = PiRuntime.FindPermissionGateExtension();
                if (extension == null) return false;
                pi.PermissionGateExtensionPath = extension;
                pi.PermissionPromptTimeoutSeconds = Math.Clamp(promptTimeoutSeconds + CliTimeoutMarginSeconds, 10, 86400);
                return true;
            }

            if (runtime is MuxRuntime mux)
            {
                mux.PermissionPrompt = null;
                if (!approve || !MuxCapabilities.SupportsPermissionPromptTool()) return false;
                mux.PermissionPrompt = new MuxPermissionPrompt
                {
                    ToolName = CaptainThreadMcpPlanner.ServerName + "." + CliPermissionPolicyResolver.PromptToolName,
                    TimeoutSeconds = Math.Clamp(promptTimeoutSeconds + CliTimeoutMarginSeconds, 10, 86400)
                };
                return true;
            }

            return false;
        }

        #endregion
    }
}
