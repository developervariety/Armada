namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Runtimes.Interfaces;

    /// <summary>
    /// Agent runtime that starts no process. A start registers a synthetic process identifier that stays
    /// running, doing no work, until a stop for that identifier arrives through any instance; the stop then
    /// reports <see cref="StoppedExitCode"/> on the instance that started it. The captain therefore stays
    /// Working exactly as long as the mission holds it, and no agent exit hands Pending work to other idle
    /// captains. A test that needs a failed or completed run raises that exit explicitly with
    /// <see cref="Exit"/>.
    /// </summary>
    public sealed class NonLaunchingAgentRuntime : IAgentRuntime
    {
        #region Public-Members

        /// <summary>
        /// Exit code reported when a running start is stopped, matching a process ended by a kill signal.
        /// </summary>
        public const int StoppedExitCode = 137;

        /// <summary>
        /// First synthetic identifier. It is above any operating-system process identifier and below the
        /// API-endpoint runtime's range, so neither an OS lookup nor an API run can collide with it.
        /// </summary>
        public const int FirstProcessId = 1_900_000_000;

        /// <inheritdoc />
        public string Name => _RuntimeType + " (test runtime, no process)";

        /// <inheritdoc />
        public bool SupportsResume => false;

        /// <inheritdoc />
        public bool SupportsPlanningSessions => true;

        /// <summary>
        /// Runtime the captain was configured with.
        /// </summary>
        public AgentRuntimeEnum RuntimeType => _RuntimeType;

        /// <inheritdoc />
        public event Action<int, string>? OnOutputReceived;

        /// <inheritdoc />
        public event Action<int, string>? OnStdoutReceived;

        /// <inheritdoc />
        public event Action<int, RuntimeTokenUsage>? OnTokenUsageReceived;

        /// <inheritdoc />
        public event Action<int, RuntimeTokenUsage>? OnProviderProgressReceived;

        /// <inheritdoc />
        public event Action<int>? OnProcessStarted;

        /// <inheritdoc />
        public event Action<int, int?>? OnProcessExited;

        #endregion

        #region Private-Members

        private static int _PidCounter = FirstProcessId;
        private static readonly ConcurrentDictionary<int, NonLaunchingAgentRuntime> _Running = new ConcurrentDictionary<int, NonLaunchingAgentRuntime>();

        private readonly AgentRuntimeEnum _RuntimeType;
        private readonly ConcurrentDictionary<int, string> _LogFiles = new ConcurrentDictionary<int, string>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="runtimeType">Runtime the captain was configured with.</param>
        public NonLaunchingAgentRuntime(AgentRuntimeEnum runtimeType)
        {
            _RuntimeType = runtimeType;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a synthetic identifier started by this runtime is still running.
        /// </summary>
        /// <param name="processId">Synthetic identifier.</param>
        /// <returns>True while running.</returns>
        public static bool IsRunning(int processId)
        {
            return _Running.ContainsKey(processId);
        }

        /// <summary>
        /// End a running start with an explicit exit code, as if the agent process had exited on its own.
        /// </summary>
        /// <param name="processId">Synthetic identifier.</param>
        /// <param name="exitCode">Exit code to report.</param>
        /// <returns>True when the identifier was running and its exit was reported.</returns>
        public static bool Exit(int processId, int? exitCode)
        {
            if (!_Running.TryRemove(processId, out NonLaunchingAgentRuntime? runtime)) return false;
            runtime.RaiseExit(processId, exitCode);
            return true;
        }

        /// <inheritdoc />
        public Task<int> StartAsync(
            string workingDirectory,
            string prompt,
            Dictionary<string, string>? environment = null,
            string? logFilePath = null,
            string? finalMessageFilePath = null,
            string? model = null,
            Captain? captain = null,
            bool showThinking = false,
            CancellationToken token = default,
            CaptainLaunchIsolationPlan? isolationPlan = null)
        {
            if (String.IsNullOrEmpty(workingDirectory)) throw new ArgumentNullException(nameof(workingDirectory));
            if (String.IsNullOrEmpty(prompt)) throw new ArgumentNullException(nameof(prompt));
            token.ThrowIfCancellationRequested();

            int processId = Interlocked.Increment(ref _PidCounter);
            ProcessSupervisor.RegisterSyntheticProcess(processId);
            _Running[processId] = this;

            if (!String.IsNullOrEmpty(logFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
                File.AppendAllText(logFilePath, "[" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "] Agent starting: "
                    + Name + Environment.NewLine + prompt + Environment.NewLine + Environment.NewLine);
                _LogFiles[processId] = logFilePath;
            }

            TestProcessLaunchLog.Record(new TestProcessLaunch
            {
                RuntimeType = _RuntimeType,
                LaunchedProcess = false,
                ProcessId = processId,
                ProcessName = Name,
                CaptainId = captain?.Id
            });

            OnProcessStarted?.Invoke(processId);
            GC.KeepAlive(OnOutputReceived);
            GC.KeepAlive(OnStdoutReceived);
            GC.KeepAlive(OnTokenUsageReceived);
            GC.KeepAlive(OnProviderProgressReceived);
            return Task.FromResult(processId);
        }

        /// <inheritdoc />
        public Task<AgentStopResult> StopAsync(int processId, CancellationToken token = default)
        {
            Exit(processId, StoppedExitCode);
            return Task.FromResult(AgentStopResult.Stopped());
        }

        /// <inheritdoc />
        public Task<bool> IsRunningAsync(int processId, CancellationToken token = default)
        {
            return Task.FromResult(IsRunning(processId));
        }

        #endregion

        #region Private-Methods

        private void RaiseExit(int processId, int? exitCode)
        {
            ProcessSupervisor.UnregisterSyntheticProcess(processId);
            TestProcessLaunchLog.RecordExit(processId, exitCode);
            if (_LogFiles.TryRemove(processId, out string? logFilePath))
            {
                File.AppendAllText(logFilePath, "[" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "] Agent exited with code "
                    + (exitCode?.ToString() ?? "unknown") + Environment.NewLine);
            }

            OnProcessExited?.Invoke(processId, exitCode);
        }

        #endregion
    }
}
