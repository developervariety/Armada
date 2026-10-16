namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// How a Mux captain sends tool calls that need approval to Armada (Approve in Armada): <c>mux print</c> runs with
    /// the ask policy, <c>--permission-prompt-tool</c> pointing at Armada's prompt tool on the scoped MCP server, and the
    /// workspace-write sandbox. Like Claude Code's acceptEdits mode, file edits inside the worktree run without asking;
    /// writes outside it are refused.
    /// </summary>
    public class MuxPermissionPrompt
    {
        #region Public-Members

        /// <summary>
        /// Tools that run without asking Armada: Armada's own MCP tools (each call is authorized for the captain's caller),
        /// file edits (confined to the worktree by the sandbox), and stopping the run's own background processes.
        /// </summary>
        public const string DefaultAutoApproveTools = "armada.*,write_file,edit_file,multi_edit,process_stop";

        /// <summary>
        /// The prompt tool in Mux's "server.tool" form.
        /// </summary>
        public string ToolName
        {
            get => _ToolName;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(ToolName));
                _ToolName = value.Trim();
            }
        }

        /// <summary>
        /// Seconds Mux waits for each answer (10 to 86400). Longer than Armada's prompt timeout, so Armada expires and
        /// denies a request first.
        /// </summary>
        public int TimeoutSeconds
        {
            get => _TimeoutSeconds;
            set
            {
                if (value < 10 || value > 86400) throw new ArgumentOutOfRangeException(nameof(TimeoutSeconds), "TimeoutSeconds must be between 10 and 86400.");
                _TimeoutSeconds = value;
            }
        }

        /// <summary>
        /// Tools (names or globs, comma separated) that run without asking.
        /// </summary>
        public string AutoApproveTools { get; set; } = DefaultAutoApproveTools;

        #endregion

        #region Private-Members

        private string _ToolName = "armada.cli_permission_prompt";
        private int _TimeoutSeconds = 720;

        #endregion
    }
}
