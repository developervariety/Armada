namespace Armada.Core.Services
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Native bounded process and host-lease adapter for generated-artifact preparation.</summary>
    public static class GeneratedArtifactLandingRunner
    {
        /// <summary>Prepare in a caller-owned worktree; generation spends the existing command budget after admission.</summary>
        public static Task<GeneratedArtifactLandingResult> PrepareAsync(string worktree, string sourceRef,
            GeneratedArtifactPolicy? policy, string ownerId, string? vesselId, TimeSpan commandTimeout,
            bool requireFreshReview = false, CancellationToken token = default)
        {
            if (commandTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(commandTimeout));
            Func<string[], CancellationToken, Task<GeneratedArtifactGitOutput>> git = async (arguments, ct) =>
                {
                    ProcessStartInfo start = GitProcessStartInfo.Create(worktree, arguments);
                    BoundedProcessResult result = await RunAsync(start, GitProcessTimeouts.Resolve(), false, ct).ConfigureAwait(false);
                    return new GeneratedArtifactGitOutput(result.ExitCode ?? -1, result.StandardOutput);
                };
            Func<string, CancellationToken, Task<int>> run = async (command, ct) =>
                {
                    using (await HostWideCommandLock.AcquireAsync(ownerId, "generated artifact regeneration", vesselId, ct).ConfigureAwait(false))
                    {
                        ProcessStartInfo start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
                        { WorkingDirectory = worktree };
                        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
                        start.ArgumentList.Add(command);
                        BuildServerEnvironment.Apply(start);
                        BoundedProcessResult result = await RunAsync(start, commandTimeout, true, ct).ConfigureAwait(false);
                        return result.ExitCode ?? -1;
                    }
                };
            return requireFreshReview
                ? GeneratedArtifactLanding.PrepareBoundCandidateAsync(worktree, sourceRef, policy, git, run, token)
                : GeneratedArtifactLanding.PrepareAsync(worktree, sourceRef, policy, git, run, token);
        }

        private static async Task<BoundedProcessResult> RunAsync(ProcessStartInfo start, TimeSpan timeout,
            bool ownProcessGroup, CancellationToken token)
        {
            BoundedProcessResult result = await BoundedProcessRunner.RunAsync(new BoundedProcessRequest(start, timeout)
            { OwnProcessGroup = ownProcessGroup, OutputLimitBytes = 1024 * 1024 }, token).ConfigureAwait(false);
            if (result.Cancelled) throw new OperationCanceledException(token);
            if (result.TimedOut) throw new TimeoutException("generated_command_timeout");
            if (result.KillError != null) throw new InvalidOperationException("generated_command_cleanup_failed: " + result.KillError);
            return result;
        }
    }
}
