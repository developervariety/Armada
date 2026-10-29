namespace Test.Shared.Suites.Services
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

    internal sealed class AskThreadReplayRuntime : IAgentRuntime
    {
        public string Name => "Saved conversation fixture";
        public bool SupportsResume => false;
        public bool SupportsPlanningSessions => true;
        public event Action<int, string>? OnOutputReceived;
        public event Action<int, string>? OnStdoutReceived;
        public event Action<int, RuntimeTokenUsage>? OnTokenUsageReceived;
        public event Action<int, RuntimeTokenUsage>? OnProviderProgressReceived;
        public event Action<int>? OnProcessStarted;
        public event Action<int, int?>? OnProcessExited;
        public string Prompt { get; private set; } = String.Empty;
        public bool AutoComplete { get; set; } = true;
        public string Reply { get; set; } = "Saved assistant reply";
        public List<string> Lines { get; set; } = new List<string>();
        public TaskCompletionSource<bool> Started { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> StartAsync(string workingDirectory, string prompt, Dictionary<string, string>? environment = null,
            string? logFilePath = null, string? finalMessageFilePath = null, string? model = null, Captain? captain = null,
            bool showThinking = false, CancellationToken token = default, CaptainLaunchIsolationPlan? isolationPlan = null)
        {
            Prompt = prompt;
            OnProcessStarted?.Invoke(1900000001);
            Started.TrySetResult(true);
            if (AutoComplete) Complete();
            return Task.FromResult(1900000001);
        }

        public void Emit(string line)
        {
            OnStdoutReceived?.Invoke(1900000001, line);
        }

        public void Complete()
        {
            foreach (string line in Lines) OnStdoutReceived?.Invoke(1900000001, line);
            OnStdoutReceived?.Invoke(1900000001, Reply);
            OnOutputReceived?.Invoke(1900000001, Reply);
            GC.KeepAlive(OnTokenUsageReceived);
            GC.KeepAlive(OnProviderProgressReceived);
            OnProcessExited?.Invoke(1900000001, 0);
        }

        public Task<AgentStopResult> StopAsync(int processId, CancellationToken token = default)
        {
            return Task.FromResult(AgentStopResult.Stopped());
        }

        public Task<bool> IsRunningAsync(int processId, CancellationToken token = default)
        {
            return Task.FromResult(false);
        }
    }
}
