namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A battery workspace over a dock's host: the Harbor that owns the dock (through its command executor and file
    /// system), or the Admiral's own machine.
    /// </summary>
    public class DockBatteryWorkspace : IBatteryWorkspace
    {
        #region Public-Members

        /// <inheritdoc />
        public string Root { get; }

        /// <inheritdoc />
        public string HostLabel { get; }

        #endregion

        #region Private-Members

        private readonly DockHost _Host;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="host">Dock host.</param>
        /// <param name="root">Worktree root on that host.</param>
        /// <exception cref="ArgumentNullException">Thrown when host or root is null or empty.</exception>
        public DockBatteryWorkspace(DockHost host, string root)
        {
            _Host = host ?? throw new ArgumentNullException(nameof(host));
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentNullException(nameof(root));
            Root = root;
            HostLabel = host.IsHarbor ? "Harbor " + host.HarborId : "the Admiral host";
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(CancellationToken token = default)
        {
            try
            {
                return await _Host.Files.DirectoryExistsAsync(Root, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is TimeoutException)
            {
                return false;
            }
        }

        /// <inheritdoc />
        public async Task<HostCommandResult> RunAsync(string executable, List<string> arguments, int timeoutMs, CancellationToken token = default)
        {
            HostCommandRequest request = new HostCommandRequest
            {
                Executable = executable,
                Arguments = arguments ?? new List<string>(),
                WorkingDirectory = Root,
                TimeoutMs = timeoutMs,
                Kind = HostCommandKindEnum.CheckRun,
                Label = "Battery: " + executable
            };
            try
            {
                return await _Host.Commands.RunAsync(request, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is IOException || ex is TimeoutException)
            {
                return new HostCommandResult { ExitCode = -1, StandardError = executable + " could not start: " + ex.Message };
            }
        }

        /// <inheritdoc />
        public Task<string?> ReadTextAsync(string relativePath, CancellationToken token = default)
        {
            return _Host.Files.ReadTextAsync(Combine(relativePath), token);
        }

        /// <inheritdoc />
        public Task WriteTextAsync(string relativePath, string content, CancellationToken token = default)
        {
            return _Host.Files.WriteTextAsync(Combine(relativePath), content, token);
        }

        #endregion

        #region Private-Methods

        private string Combine(string relativePath)
        {
            if (!BatteryPackValidator.IsSafeRelativePath(relativePath))
                throw new ArgumentException("Path '" + relativePath + "' must stay inside the worktree.", nameof(relativePath));
            char separator = Root.Contains('\\') && !Root.Contains('/') ? '\\' : '/';
            string normalized = relativePath.Replace('\\', '/').Replace('/', separator);
            return Root.TrimEnd('/', '\\') + separator + normalized;
        }

        #endregion
    }
}
