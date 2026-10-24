namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Reads checked-out Git branch and commit identity.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private async Task<string> ReadHeadAsync(string workingDirectory, CancellationToken token)
        {
            HarborWorkspaceProcessResult result = await RunGitAsync(workingDirectory,
                new[] { "rev-parse", "--verify", "HEAD^{commit}" }, TimeSpan.FromSeconds(10), 4096, token).ConfigureAwait(false);
            RequireSuccess(result, "workspace_checkout_head_unavailable");
            string value = result.StandardOutput.Trim();
            if (!IsFullCommit(value)) throw Failure("workspace_checkout_head_invalid", "The checkout HEAD is not a full commit identity.");
            return value;
        }

        private async Task<string> ReadBranchAsync(string workingDirectory, CancellationToken token)
        {
            HarborWorkspaceProcessResult result = await RunGitAsync(workingDirectory,
                new[] { "symbolic-ref", "--short", "-q", "HEAD" }, TimeSpan.FromSeconds(10), 4096, token).ConfigureAwait(false);
            if (result.ExitCode == 1) return "(detached)";
            RequireSuccess(result, "workspace_checkout_branch_unavailable");
            return result.StandardOutput.Trim();
        }

        private static bool IsFullCommit(string value)
        {
            if (value.Length != 40 && value.Length != 64) return false;
            foreach (char character in value)
                if (!Uri.IsHexDigit(character)) return false;
            return true;
        }
    }
}
