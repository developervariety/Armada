namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The worktree a battery task ran in, on the host that ran it. Graders run commands and read or write files
    /// through it, so grading happens where the captain worked.
    /// </summary>
    public interface IBatteryWorkspace
    {
        /// <summary>
        /// Worktree root on its host.
        /// </summary>
        string Root { get; }

        /// <summary>
        /// Host description for reports, for example "the Admiral host".
        /// </summary>
        string HostLabel { get; }

        /// <summary>
        /// Whether the worktree still exists on its host (a reclaimed dock answers false).
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the worktree exists.</returns>
        Task<bool> ExistsAsync(CancellationToken token = default);

        /// <summary>
        /// Run a command in the worktree root. Never throws for a missing executable; returns a failed result instead.
        /// </summary>
        /// <param name="executable">Executable.</param>
        /// <param name="arguments">Arguments.</param>
        /// <param name="timeoutMs">Timeout in milliseconds.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The result; <see cref="BoundedProcessResult.ExitCode"/> is -1 when the executable could not start.</returns>
        Task<BoundedProcessResult> RunAsync(string executable, List<string> arguments, int timeoutMs, CancellationToken token = default);

        /// <summary>
        /// Read a worktree file as text.
        /// </summary>
        /// <param name="relativePath">Path relative to the root.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The text, or null when the file does not exist.</returns>
        Task<string?> ReadTextAsync(string relativePath, CancellationToken token = default);

        /// <summary>
        /// Write a worktree file as text, creating folders as needed.
        /// </summary>
        /// <param name="relativePath">Path relative to the root.</param>
        /// <param name="content">Content.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        Task WriteTextAsync(string relativePath, string content, CancellationToken token = default);
    }
}
