namespace Armada.Core.Services
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Probes for a working container runtime by asking the docker CLI for its server version.
    /// A missing binary, a stopped daemon, or a hung call all report unavailable.
    /// </summary>
    /// <remarks>
    /// The result is cached for the lifetime of the instance: a dock runs the gate's build and test
    /// commands back to back, and a daemon does not appear mid-gate. Caching also keeps a hung
    /// docker call from costing the probe timeout more than once.
    /// </remarks>
    public sealed class DockerCliContainerRuntimeProbe : IContainerRuntimeProbe
    {
        #region Private-Members

        private readonly int _TimeoutSeconds;
        private readonly SemaphoreSlim _Gate = new SemaphoreSlim(1, 1);
        private bool? _Cached = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="timeoutSeconds">Seconds to wait for the runtime to answer. Clamped to [1, 60].</param>
        public DockerCliContainerRuntimeProbe(int timeoutSeconds = 10)
        {
            _TimeoutSeconds = Math.Max(1, Math.Min(60, timeoutSeconds));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<bool> IsAvailableAsync(string workingDirectory, CancellationToken token = default)
        {
            if (_Cached.HasValue) return _Cached.Value;

            await _Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_Cached.HasValue) return _Cached.Value;
                _Cached = await ProbeAsync(workingDirectory, token).ConfigureAwait(false);
                return _Cached.Value;
            }
            finally
            {
                _Gate.Release();
            }
        }

        #endregion

        #region Private-Methods

        private async Task<bool> ProbeAsync(string workingDirectory, CancellationToken token)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "docker",
                WorkingDirectory = String.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("info");
            startInfo.ArgumentList.Add("--format");
            startInfo.ArgumentList.Add("{{.ServerVersion}}");

            using CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_TimeoutSeconds));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            using Process process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start()) return false;

                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

                // `docker info` exits 0 but prints an empty server version when the CLI is installed
                // and the daemon is not reachable, so the version string is the real signal.
                return process.ExitCode == 0 && !String.IsNullOrWhiteSpace(stdoutTask.Result);
            }
            catch (Exception)
            {
                // Missing binary, timeout, or any launch failure: treat as unavailable. The probe must
                // never be the reason a gate fails.
                try { process.Kill(true); } catch { }
                return false;
            }
        }

        #endregion
    }
}
