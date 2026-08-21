namespace Armada.Runtimes.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Interface for agent runtime adapters.
    /// </summary>
    public interface IAgentRuntime
    {
        /// <summary>
        /// Runtime display name.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Whether this runtime supports session resume.
        /// </summary>
        bool SupportsResume { get; }

        /// <summary>
        /// Whether this runtime can participate in Armada planning sessions.
        /// Planning currently uses transcript-backed turn relaunches rather than a persistent stdin session.
        /// </summary>
        bool SupportsPlanningSessions { get; }

        /// <summary>
        /// Event raised when the agent writes a line to EITHER stdout or stderr. The int parameter is
        /// the process ID, the string is the output line. Missions subscribe here because some CLIs
        /// emit useful progress and diagnostics on stderr. Interactive consumers that only want the
        /// model's answer (chat, planning) should subscribe to <see cref="OnStdoutReceived"/> instead,
        /// since agent CLIs print human-readable banners and prompt echoes to stderr that must not
        /// appear in the reply.
        /// </summary>
        event Action<int, string>? OnOutputReceived;

        /// <summary>
        /// Event raised only when the agent writes a line to stdout, never stderr. The int parameter is
        /// the process ID, the string is the output line. Use this for interactive reply capture so CLI
        /// stderr banners (for example Codex's "Reading prompt from stdin..." preamble) are excluded
        /// from what the user is shown as the model's answer.
        /// </summary>
        event Action<int, string>? OnStdoutReceived;

        /// <summary>
        /// Event raised when the runtime receives authoritative provider token usage.
        /// </summary>
        event Action<int, RuntimeTokenUsage>? OnTokenUsageReceived;

        /// <summary>
        /// Event raised when the runtime receives authoritative provider-progress evidence
        /// (token usage, step completion, model response). Captains whose providers have
        /// silently hung inside a long-running request keep their OS process alive (so the
        /// captain heartbeat stays fresh) but stop publishing this signal. The autonomous
        /// recovery orchestrator subscribes to this event to bound the silent-provider case
        /// within the configured stall window and to distinguish it from a captain-wide
        /// heartbeat stall.
        /// </summary>
        event Action<int, RuntimeTokenUsage>? OnProviderProgressReceived;

        /// <summary>
        /// Event raised immediately after the agent process starts and a PID is available.
        /// </summary>
        event Action<int>? OnProcessStarted;

        /// <summary>
        /// Event raised when the agent process exits.
        /// Parameters: processId, exitCode (null if unavailable).
        /// </summary>
        event Action<int, int?>? OnProcessExited;

        /// <summary>
        /// Start an agent process with the given prompt in the specified working directory.
        /// Returns the process ID.
        /// </summary>
        /// <param name="workingDirectory">Working directory for the agent.</param>
        /// <param name="prompt">Initial prompt or mission description.</param>
        /// <param name="environment">Additional environment variables.</param>
        /// <param name="logFilePath">Optional path to write agent output log.</param>
        /// <param name="finalMessageFilePath">Optional path to write the agent's final response artifact.</param>
        /// <param name="model">Optional model override.</param>
        /// <param name="captain">Optional captain metadata used by runtimes that need persisted runtime-specific options.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Process ID of the started agent.</returns>
        Task<int> StartAsync(
            string workingDirectory,
            string prompt,
            Dictionary<string, string>? environment = null,
            string? logFilePath = null,
            string? finalMessageFilePath = null,
            string? model = null,
            Captain? captain = null,
            CancellationToken token = default);

        /// <summary>
        /// Stop an agent process gracefully.
        /// </summary>
        /// <param name="processId">Process ID to stop.</param>
        /// <param name="token">Cancellation token.</param>
        Task StopAsync(int processId, CancellationToken token = default);

        /// <summary>
        /// Check if an agent process is still running.
        /// </summary>
        /// <param name="processId">Process ID to check.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the process is running.</returns>
        Task<bool> IsRunningAsync(int processId, CancellationToken token = default);
    }
}
