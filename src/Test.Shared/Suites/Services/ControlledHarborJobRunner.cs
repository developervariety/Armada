namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Services;

    /// <summary>
    /// A Harbor job runner whose jobs a test drives by hand: a launch only records the job's callbacks, and the test then
    /// starts it, writes its output, and ends it whenever it likes (for example while the Harbor's link is down). Stops are
    /// recorded and end the job with exit code -1.
    /// </summary>
    public sealed class ControlledHarborJobRunner : IHarborJobRunner
    {
        #region Public-Members

        /// <summary>
        /// Jobs a stop was requested for, in order.
        /// </summary>
        public List<string> Stopped
        {
            get { lock (_Lock) return new List<string>(_Stopped); }
        }

        #endregion

        #region Private-Members

        private readonly object _Lock = new object();
        private readonly Dictionary<string, Action<int>> _Started = new Dictionary<string, Action<int>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Action<HarborOutputStreamEnum, string>> _Output = new Dictionary<string, Action<HarborOutputStreamEnum, string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Action<int>> _Exited = new Dictionary<string, Action<int>>(StringComparer.Ordinal);
        private readonly List<string> _Stopped = new List<string>();

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public Task StartAsync(HarborLaunchRequest request, string? mcpBaseUrl, Action<int> onStarted, Action<HarborOutputStreamEnum, string> onOutput, Action<int> onExited, CancellationToken token)
        {
            lock (_Lock)
            {
                _Started[request.JobId] = onStarted;
                _Output[request.JobId] = onOutput;
                _Exited[request.JobId] = onExited;
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public string? ResolveWorkingDirectory(HarborLaunchRequest request)
        {
            return String.IsNullOrWhiteSpace(request.WorkingDirectory) ? null : request.WorkingDirectory;
        }

        /// <inheritdoc />
        public Task StopAsync(string jobId, int gracefulTimeoutMs, CancellationToken token)
        {
            lock (_Lock) _Stopped.Add(jobId);
            Exit(jobId, -1);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Whether the runner received a launch for the job.
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        /// <returns>True when launched.</returns>
        public bool Launched(string jobId)
        {
            lock (_Lock) return _Started.ContainsKey(jobId);
        }

        /// <summary>
        /// Report the job's process as started.
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="processId">Process id to report.</param>
        public void Start(string jobId, int processId)
        {
            Action<int>? callback;
            lock (_Lock) _Started.TryGetValue(jobId, out callback);
            callback?.Invoke(processId);
        }

        /// <summary>
        /// Report a line of the job's standard output.
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="data">The line.</param>
        public void Output(string jobId, string data)
        {
            Action<HarborOutputStreamEnum, string>? callback;
            lock (_Lock) _Output.TryGetValue(jobId, out callback);
            callback?.Invoke(HarborOutputStreamEnum.Stdout, data);
        }

        /// <summary>
        /// Report the job's exit (once; later calls do nothing).
        /// </summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="exitCode">Exit code.</param>
        public void Exit(string jobId, int exitCode)
        {
            Action<int>? callback;
            lock (_Lock)
            {
                _Exited.TryGetValue(jobId, out callback);
                _Exited.Remove(jobId);
            }

            callback?.Invoke(exitCode);
        }

        #endregion
    }
}
