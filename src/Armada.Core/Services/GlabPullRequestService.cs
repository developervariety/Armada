namespace Armada.Core.Services
{
    using System;
    using System.Diagnostics;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Merge requests via the GitLab CLI (<c>glab</c>).
    /// </summary>
    public sealed class GlabPullRequestService : IPullRequestService
    {
        private readonly string _ExecutablePath;
        private readonly string _WorkingDirectory;
        private readonly Func<string, string, string[], CancellationToken, Task<string>>? _ProcessRunner;

        /// <summary>
        /// Creates the service.
        /// </summary>
        /// <param name="glabCliPath">Path or name of the glab executable.</param>
        /// <param name="workingDirectory">Repository working directory for CLI context.</param>
        /// <param name="processRunner">Optional test hook; receives working directory, executable, and argv.</param>
        public GlabPullRequestService(
            string glabCliPath,
            string workingDirectory,
            Func<string, string, string[], CancellationToken, Task<string>>? processRunner = null)
        {
            _ExecutablePath = glabCliPath ?? throw new ArgumentNullException(nameof(glabCliPath));
            _WorkingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
            _ProcessRunner = processRunner;
        }

        /// <inheritdoc />
        public async Task<string> CreateAsync(string branch, string baseBranch, string title, string body, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(branch))
                throw new ArgumentException("Branch is required.", nameof(branch));
            if (String.IsNullOrWhiteSpace(baseBranch))
                throw new ArgumentException("Base branch is required.", nameof(baseBranch));
            if (String.IsNullOrWhiteSpace(title))
                throw new ArgumentException("Title is required.", nameof(title));

            string[] args = new[]
            {
                "mr", "create",
                "--source-branch", branch,
                "--target-branch", baseBranch,
                "--title", title,
                "--description", body ?? "",
                "--yes"
            };

            string stdout = await InvokeAsync(args, token).ConfigureAwait(false);
            return stdout.Trim();
        }

        /// <inheritdoc />
        public async Task<bool> IsMergedAsync(string prUrl, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(prUrl))
                throw new ArgumentException("PR URL is required.", nameof(prUrl));

            string[] args = new[] { "mr", "view", prUrl, "-F", "json" };
            string stdout = await InvokeAsync(args, token).ConfigureAwait(false);
            return ParseMergedFromMrJson(stdout);
        }

        private async Task<string> InvokeAsync(string[] args, CancellationToken token)
        {
            if (_ProcessRunner != null)
                return await _ProcessRunner(_WorkingDirectory, _ExecutablePath, args, token).ConfigureAwait(false);

            return await RunProcessAsync(_WorkingDirectory, _ExecutablePath, args, token).ConfigureAwait(false);
        }

        private static bool ParseMergedFromMrJson(string json)
        {
            if (String.IsNullOrWhiteSpace(json))
                return false;

            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("state", out JsonElement stateElement))
                return false;

            string? state = stateElement.GetString();
            return !String.IsNullOrEmpty(state) && state.Equals("merged", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> RunProcessAsync(
            string workingDirectory,
            string executablePath,
            string[] args,
            CancellationToken token)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };

            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            using CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            using Process process = new Process { StartInfo = startInfo };
            process.Start();

            string stdout;
            string stderr;
            try
            {
                stdout = await process.StandardOutput.ReadToEndAsync(linkedCts.Token).ConfigureAwait(false);
                stderr = await process.StandardError.ReadToEndAsync(linkedCts.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }

                throw new TimeoutException(executablePath + " timed out after 120 seconds");
            }

            if (process.ExitCode != 0)
            {
                string detail = stderr.Trim();
                if (String.IsNullOrEmpty(detail))
                    detail = stdout.Trim();

                throw new InvalidOperationException(
                    executablePath + " failed (exit " + process.ExitCode + "): " + detail);
            }

            return stdout;
        }
    }
}
