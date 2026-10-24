namespace Armada.Core.Harbor
{
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Server-to-Harbor request to launch a captain process. Carries the fully resolved launch plan the
    /// Admiral built (runtime, arguments, environment, working directory, prompt); the Harbor spawns the
    /// process and streams its lifecycle back keyed by <see cref="JobId"/>.
    /// </summary>
    public class HarborLaunchRequest : HarborMessage
    {
        #region Public-Members

        /// <summary>
        /// Harbor-scoped job identifier assigned by the server for this launch. All later messages about
        /// the process reference it.
        /// </summary>
        public string JobId { get; set; } = string.Empty;

        /// <summary>
        /// Server-issued attempt key retained by the runner for authenticated restart recovery.
        /// </summary>
        public string LaunchKey { get; set; } = string.Empty;

        /// <summary>Durable dock identifier for an explicitly owned checkout launch.</summary>
        public string? DockId { get; set; }

        /// <summary>Pinned checkout identity; null for existing mapped or local routes.</summary>
        public HarborCheckoutBinding? CheckoutBinding { get; set; }

        /// <summary>Private mission-scoped runtime configuration; absent for existing mapped launches.</summary>
        public HarborMissionRuntimeConfiguration? MissionConfiguration { get; set; }

        /// <summary>
        /// Runtime identifier (for example "claude", "codex", "opencode").
        /// </summary>
        public string Runtime { get; set; } = string.Empty;

        /// <summary>
        /// Absolute working directory (a dock worktree) on the Harbor host.
        /// </summary>
        public string WorkingDirectory { get; set; } = string.Empty;

        /// <summary>
        /// Optional model identifier to pass to the runtime.
        /// </summary>
        public string? Model { get; set; } = null;

        /// <summary>
        /// The prompt to deliver to the captain.
        /// </summary>
        public string? Prompt { get; set; } = null;

        /// <summary>
        /// Whether the prompt is delivered on stdin (true) or as an argument (false).
        /// </summary>
        public bool PromptViaStdin { get; set; } = true;

        /// <summary>
        /// Command-line arguments for the runtime, in order.
        /// </summary>
        public List<string> Arguments { get; set; } = new List<string>();

        /// <summary>
        /// Additional environment variables to set for the process. Secrets should be avoided; the Harbor
        /// uses its own host login for provider auth.
        /// </summary>
        public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();

        #endregion
    }
}
