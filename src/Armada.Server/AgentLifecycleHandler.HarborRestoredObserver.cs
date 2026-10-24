namespace Armada.Server
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;

    public partial class AgentLifecycleHandler
    {
        private sealed class RestoredHarborObserver : IHarborJobCompletionObserver
        {
            private readonly AgentLifecycleHandler _Handler;
            private readonly IHarborProcessEvents _Events;
            private readonly int _ProcessId;
            private readonly string _JobId;

            public RestoredHarborObserver(AgentLifecycleHandler handler, string jobId, int processId, IHarborProcessEvents events)
            {
                _Handler = handler;
                _JobId = jobId;
                _ProcessId = processId;
                _Events = events;
            }

            public void OnStarted(HarborJobSnapshot job)
            {
                _Handler._Logging.Info(_Handler._Header + "restored Harbor job " + _JobId + " resumed on runner " + job.RunnerId);
            }

            public void OnOutput(string jobId, HarborOutput output)
            {
                _Events.OnOutput(_ProcessId, output.Stream, output.Data ?? String.Empty);
            }

            public void OnFinished(HarborJobSnapshot job)
            {
                _ = OnFinishedAsync(job);
            }

            public async Task OnFinishedAsync(HarborJobSnapshot job)
            {
                int? exitCode = job.State == HarborJobStateEnum.Exited ? job.ExitCode : null;
                string? failureReason = job.State == HarborJobStateEnum.Exited
                    ? null
                    : (job.FailureReason ?? "harbor_job_" + job.State.ToString().ToLowerInvariant());
                try
                {
                    if (_Events is IHarborProcessCompletionEvents completionEvents)
                        await completionEvents.OnExitedAsync(_ProcessId, exitCode, failureReason).ConfigureAwait(false);
                    else
                        _Events.OnExited(_ProcessId, exitCode, failureReason);
                }
                finally
                {
                    ProcessSupervisor.UnregisterSyntheticProcess(_ProcessId);
                    _Handler._RestoredHarborEvents.TryRemove(_JobId, out _);
                }
            }
        }
    }
}
