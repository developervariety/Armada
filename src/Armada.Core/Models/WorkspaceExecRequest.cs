namespace Armada.Core.Models
{
    /// <summary>
    /// A request to run a shell command inside a vessel's workspace (the in-browser dock terminal).
    /// Intended for the authenticated operator working in their own workspace.
    /// </summary>
    public class WorkspaceExecRequest
    {
        /// <summary>
        /// The command line to execute in the workspace root via the platform shell.
        /// </summary>
        public string Command { get; set; } = string.Empty;

        /// <summary>
        /// Timeout in seconds before the command (and its process tree) is killed. Clamped to [1, 600].
        /// </summary>
        public int TimeoutSeconds { get; set; } = 60;
    }
}
