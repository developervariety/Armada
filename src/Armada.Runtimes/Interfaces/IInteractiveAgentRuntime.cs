namespace Armada.Runtimes.Interfaces
{
    /// <summary>
    /// Optional extension contract for runtimes that can keep one interactive process alive
    /// across multiple turns instead of relaunching per prompt.
    /// </summary>
    public interface IInteractiveAgentRuntime : IAgentRuntime
    {
        /// <summary>
        /// Whether the runtime can keep a persistent interactive session alive across turns.
        /// </summary>
        bool SupportsInteractiveSessions { get; }

        /// <summary>
        /// Start an interactive agent process without closing stdin after the first prompt.
        /// </summary>
        /// <param name="workingDirectory">Working directory for the agent.</param>
        /// <param name="environment">Additional environment variables.</param>
        /// <param name="logFilePath">Optional path to write agent output log.</param>
        /// <param name="model">Optional model override.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Process ID of the started agent.</returns>
        Task<int> StartInteractiveAsync(
            string workingDirectory,
            Dictionary<string, string>? environment = null,
            string? logFilePath = null,
            string? model = null,
            CancellationToken token = default);

        /// <summary>
        /// Send one new turn to an existing interactive agent process.
        /// </summary>
        /// <param name="processId">Process ID of the interactive session.</param>
        /// <param name="prompt">Prompt or turn content to send.</param>
        /// <param name="token">Cancellation token.</param>
        Task SendAsync(int processId, string prompt, CancellationToken token = default);
    }
}
