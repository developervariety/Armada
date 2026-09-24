namespace Armada.Core.Services
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Pull requests via the GitHub CLI (<c>gh</c>).
    /// </summary>
    public sealed class GhPullRequestService : IPullRequestService
    {
        private readonly string _ExecutablePath;
        private readonly string _WorkingDirectory;
        private readonly Func<string, string, string[], CancellationToken, Task<string>>? _ProcessRunner;

        /// <summary>
        /// Creates the service.
        /// </summary>
        /// <param name="ghCliPath">Path or name of the gh executable.</param>
        /// <param name="workingDirectory">Repository working directory for CLI context.</param>
        /// <param name="processRunner">Optional test hook; receives working directory, executable, and argv.</param>
        public GhPullRequestService(
            string ghCliPath,
            string workingDirectory,
            Func<string, string, string[], CancellationToken, Task<string>>? processRunner = null)
        {
            _ExecutablePath = ghCliPath ?? throw new ArgumentNullException(nameof(ghCliPath));
            _WorkingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
            _ProcessRunner = processRunner;
        }

        /// <inheritdoc />
        public async Task<string> CreateAsync(string branch, string baseBranch, string title, string body, CancellationToken token = default)
        {
            ValidateBranchBaseTitle(branch, baseBranch, title);

            string[] args = new[]
            {
                "pr", "create",
                "--base", baseBranch,
                "--head", branch,
                "--title", title,
                "--body", body ?? ""
            };

            string stdout = await InvokeAsync(args, token).ConfigureAwait(false);
            return stdout.Trim();
        }

        /// <inheritdoc />
        public async Task<bool> IsMergedAsync(string prUrl, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(prUrl))
                throw new ArgumentException("PR URL is required.", nameof(prUrl));

            string[] args = new[] { "pr", "view", prUrl, "--json", "state", "--jq", ".state" };
            string stdout = await InvokeAsync(args, token).ConfigureAwait(false);
            return stdout.Trim().Equals("MERGED", StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateBranchBaseTitle(string branch, string baseBranch, string title)
        {
            if (String.IsNullOrWhiteSpace(branch))
                throw new ArgumentException("Branch is required.", nameof(branch));
            if (String.IsNullOrWhiteSpace(baseBranch))
                throw new ArgumentException("Base branch is required.", nameof(baseBranch));
            if (String.IsNullOrWhiteSpace(title))
                throw new ArgumentException("Title is required.", nameof(title));
        }

        private async Task<string> InvokeAsync(string[] args, CancellationToken token)
        {
            if (_ProcessRunner != null)
                return await _ProcessRunner(_WorkingDirectory, _ExecutablePath, args, token).ConfigureAwait(false);

            return await RunProcessAsync(_WorkingDirectory, _ExecutablePath, args, token).ConfigureAwait(false);
        }

        private static async Task<string> RunProcessAsync(
            string workingDirectory,
            string executablePath,
            string[] args,
            CancellationToken token)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(executablePath) { WorkingDirectory = workingDirectory };
            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            // The CLI talks to the forge over the network, so git's prompts are off for any git it runs.
            GitProcessStartInfo.ApplyNonInteractive(startInfo);
            BoundedProcessRequest request = new BoundedProcessRequest(startInfo, TimeSpan.FromSeconds(120))
            {
                OutputLimitBytes = 1024 * 1024
            };
            BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request, token).ConfigureAwait(false);
            if (result.Cancelled)
            {
                token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(token);
            }
            if (result.TimedOut)
                throw new TimeoutException(executablePath + " timed out after 120 seconds");

            string stdout = result.StandardOutput;
            string stderr = result.StandardError;
            int exitCode = result.ExitCode ?? -1;
            if (exitCode != 0)
            {
                string detail = stderr.Trim();
                if (String.IsNullOrEmpty(detail))
                    detail = stdout.Trim();

                throw new InvalidOperationException(
                    executablePath + " failed (exit " + exitCode + "): " + detail);
            }

            return stdout;
        }
    }
}
