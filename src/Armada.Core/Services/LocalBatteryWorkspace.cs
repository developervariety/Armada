namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A battery workspace over a directory on the Admiral's own machine.
    /// </summary>
    public class LocalBatteryWorkspace : IBatteryWorkspace, IDisposable
    {
        #region Public-Members

        /// <inheritdoc />
        public string Root { get; }

        /// <inheritdoc />
        public string HostLabel => "the Admiral host";

        #endregion

        #region Private-Members

        private bool _OwnsDirectory;
        private const int _OutputLimitBytes = 256 * 1024;
        private static readonly UTF8Encoding _Utf8 = new UTF8Encoding(false);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="root">Worktree root.</param>
        /// <exception cref="ArgumentNullException">Thrown when root is null or empty.</exception>
        public LocalBatteryWorkspace(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentNullException(nameof(root));
            Root = Path.GetFullPath(root);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Create an isolated checkout of the recorded result, independent of the captain's reclaimed dock.
        /// </summary>
        /// <param name="repository">Local repository that holds the commit.</param>
        /// <param name="commit">Recorded result commit.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>An owned workspace; dispose it after grading.</returns>
        public static async Task<LocalBatteryWorkspace> CreateSnapshotAsync(string repository, string commit, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(repository)) throw new ArgumentNullException(nameof(repository));
            if (commit == null || commit.Length != 40 || !System.Linq.Enumerable.All(commit, Uri.IsHexDigit))
                throw new ArgumentException("A full result commit is required.", nameof(commit));
            string root = Path.Combine(Path.GetTempPath(), "armada-battery-grade-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            LocalBatteryWorkspace workspace = new LocalBatteryWorkspace(root) { _OwnsDirectory = true };
            try
            {
                BoundedProcessResult clone = await workspace.RunAsync("git", new List<string>
                {
                    "clone", "--no-checkout", "--no-hardlinks", "--", Path.GetFullPath(repository), "."
                }, 120000, token).ConfigureAwait(false);
                if (!clone.Succeeded) throw new InvalidOperationException("Could not preserve the result for grading: " + clone.StandardError);
                BoundedProcessResult checkout = await workspace.RunAsync("git", new List<string>
                {
                    "-c", "core.autocrlf=false", "checkout", "--detach", commit
                }, 120000, token).ConfigureAwait(false);
                if (!checkout.Succeeded) throw new InvalidOperationException("Could not check out the recorded result: " + checkout.StandardError);
                return workspace;
            }
            catch
            {
                workspace.Dispose();
                throw;
            }
        }

        /// <summary>Delete an owned grading checkout. Caller-owned workspaces are retained.</summary>
        public void Dispose()
        {
            if (_OwnsDirectory && Directory.Exists(Root)) Directory.Delete(Root, true);
        }

        /// <inheritdoc />
        public Task<bool> ExistsAsync(CancellationToken token = default)
        {
            return Task.FromResult(Directory.Exists(Root));
        }

        /// <inheritdoc />
        public async Task<BoundedProcessResult> RunAsync(string executable, List<string> arguments, int timeoutMs, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(executable)) throw new ArgumentNullException(nameof(executable));
            ProcessStartInfo startInfo = new ProcessStartInfo { FileName = executable, WorkingDirectory = Root };
            foreach (string argument in arguments ?? new List<string>()) startInfo.ArgumentList.Add(argument);
            BuildServerEnvironment.Apply(startInfo);
            BoundedProcessRequest request = new BoundedProcessRequest(startInfo, TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs)))
            {
                OutputLimitBytes = _OutputLimitBytes,
                OwnProcessGroup = true
            };
            try
            {
                return await BoundedProcessRunner.RunAsync(request, token).ConfigureAwait(false);
            }
            catch (Win32Exception ex)
            {
                return new BoundedProcessResult { ExitCode = -1, StandardError = executable + " could not start: " + ex.Message };
            }
        }

        /// <inheritdoc />
        public async Task<string?> ReadTextAsync(string relativePath, CancellationToken token = default)
        {
            string path = Combine(relativePath);
            if (!File.Exists(path)) return null;
            return await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task WriteTextAsync(string relativePath, string content, CancellationToken token = default)
        {
            string path = Combine(relativePath);
            string? folder = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(path, content, _Utf8, token).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private string Combine(string relativePath)
        {
            string? full = BatteryPackValidator.ResolveInside(Root, relativePath);
            if (full == null) throw new ArgumentException("Path '" + relativePath + "' must stay inside the worktree.", nameof(relativePath));
            return full;
        }

        #endregion
    }
}
