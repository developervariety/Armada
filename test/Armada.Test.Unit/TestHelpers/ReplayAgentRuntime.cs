namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Exposes the real OpenCode output transform so captured opencode --format json events become the
    /// records the runtime emits to subscribers.
    /// </summary>
    public sealed class OpenCodeRecordTransform : OpenCodeRuntime
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public OpenCodeRecordTransform(LoggingModule logging) : base(logging)
        {
        }

        /// <summary>
        /// Transform captured event lines into the non-empty records the runtime emits.
        /// </summary>
        /// <param name="lines">Captured stdout lines.</param>
        /// <returns>Emitted records in order.</returns>
        public List<string> Records(IEnumerable<string> lines)
        {
            List<string> records = new List<string>();
            foreach (string line in lines)
            {
                foreach (string record in TransformOutputRecords(line))
                {
                    if (!String.IsNullOrEmpty(record)) records.Add(record);
                }
            }
            return records;
        }
    }

    /// <summary>
    /// Factory that returns a runtime replaying fixed stdout records instead of launching a process.
    /// </summary>
    public sealed class ReplayRuntimeFactory : AgentRuntimeFactory
    {
        private readonly IReadOnlyList<string> _Records;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="records">Records every created runtime raises.</param>
        public ReplayRuntimeFactory(LoggingModule logging, IReadOnlyList<string> records) : base(logging)
        {
            _Records = records ?? throw new ArgumentNullException(nameof(records));
        }

        /// <inheritdoc />
        public override IAgentRuntime Create(AgentRuntimeEnum runtimeType)
        {
            return new ReplayAgentRuntime(_Records);
        }
    }

    /// <summary>
    /// Runtime that raises the stdout records it was given on a background task, then reports a clean exit.
    /// </summary>
    public sealed class ReplayAgentRuntime : IAgentRuntime
    {
        private const int _ProcessId = 4242;
        private readonly IReadOnlyList<string> _Records;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="records">Records to raise.</param>
        public ReplayAgentRuntime(IReadOnlyList<string> records)
        {
            _Records = records ?? throw new ArgumentNullException(nameof(records));
        }

        /// <inheritdoc />
        public string Name => "Replay";

        /// <inheritdoc />
        public bool SupportsResume => false;

        /// <inheritdoc />
        public bool SupportsPlanningSessions => true;

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
            _ = Task.Run(async () =>
            {
                await Task.Delay(50).ConfigureAwait(false);
                OnProcessStarted?.Invoke(_ProcessId);
                foreach (string record in _Records)
                {
                    OnOutputReceived?.Invoke(_ProcessId, record);
                    OnStdoutReceived?.Invoke(_ProcessId, record);
                }
                GC.KeepAlive(OnTokenUsageReceived);
                GC.KeepAlive(OnProviderProgressReceived);
                OnProcessExited?.Invoke(_ProcessId, 0);
            });
            return Task.FromResult(_ProcessId);
        }

        /// <inheritdoc />
        public Task StopAsync(int processId, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<bool> IsRunningAsync(int processId, CancellationToken token = default)
        {
            return Task.FromResult(false);
        }
    }
}
