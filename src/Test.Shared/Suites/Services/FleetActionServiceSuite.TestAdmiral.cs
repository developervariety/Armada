namespace Test.Shared.Suites.Services
{
    using System;
    using System.IO;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;

    public sealed partial class FleetActionServiceSuite
    {
        public class TestAdmiral : DispatchProxy
        {
            private TaskCompletionSource<bool>? _DispatchEntered;
            private TaskCompletionSource<bool>? _DispatchRelease;
            private bool _ThrowAfterRelease;
            private int _DispatchCalls;
            public Pipeline? ResolvedPipeline { get; set; }

            public int DispatchCalls => Volatile.Read(ref _DispatchCalls);

            public void HoldNextDispatch(bool throwAfterRelease)
            {
                _DispatchEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _DispatchRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ThrowAfterRelease = throwAfterRelease;
            }

            public Task WaitForDispatchAsync() => (_DispatchEntered ?? throw new InvalidOperationException("Dispatch hold was not configured.")).Task;

            public void ReleaseDispatch() => _DispatchRelease?.TrySetResult(true);

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod?.Name == "get_DispatchHold") return null;
                if (targetMethod?.Name == "ResolvePipelineAsync") return Task.FromResult(ResolvedPipeline);
                if (targetMethod?.Name == "ProbeCapacityAsync") return Task.FromResult<FleetCapacityAdmissionException?>(null);
                if (targetMethod?.Name == "DispatchVoyageQueuedAsync") return DispatchQueuedAsync(args!);
                throw new NotSupportedException("This service test does not dispatch runtime work.");
            }

            private async Task<Voyage> DispatchQueuedAsync(object?[] args)
            {
                Interlocked.Increment(ref _DispatchCalls);
                TaskCompletionSource<bool> entered = _DispatchEntered ?? throw new InvalidOperationException("Dispatch hold was not configured.");
                TaskCompletionSource<bool> release = _DispatchRelease ?? throw new InvalidOperationException("Dispatch hold was not configured.");
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                if (_ThrowAfterRelease) throw new IOException("Simulated dispatch result loss after the handler started.");
                return new Voyage((string)args[0]!);
            }
        }
    }
}
