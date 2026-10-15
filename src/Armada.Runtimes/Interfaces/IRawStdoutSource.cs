namespace Armada.Runtimes.Interfaces
{
    using System;

    /// <summary>
    /// A runtime that reports each stdout line of its CLI exactly as the CLI wrote it, before any decoding. While a
    /// mission streams its structured output, <see cref="IAgentRuntime.OnStdoutReceived"/> carries the decoded, readable
    /// lines (as text mode prints them); consumers that need the CLI's typed events (for example the usage of Claude
    /// Code's result event, Codex's turn.completed, or Mux's run_completed) subscribe here instead.
    /// </summary>
    public interface IRawStdoutSource
    {
        /// <summary>
        /// Raised for each non-empty stdout line the CLI wrote, unchanged. Parameters: process id, line.
        /// </summary>
        event Action<int, string>? OnRawStdoutReceived;
    }
}
