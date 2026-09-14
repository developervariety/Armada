namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Concurrent;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// An <see cref="IAdmiralService"/> that records the process exits the agent lifecycle hands to the Admiral, the
    /// entry point of completion and recovery, and answers every other member with a default value.
    /// </summary>
    public class RecordingAdmiral : DispatchProxy
    {
        private readonly ConcurrentQueue<RecordedProcessExit> _Exits = new ConcurrentQueue<RecordedProcessExit>();
        private readonly SemaphoreSlim _Signal = new SemaphoreSlim(0);

        /// <summary>The recorder as an Admiral service.</summary>
        public IAdmiralService Service => (IAdmiralService)(object)this;

        /// <summary>Create a recorder.</summary>
        /// <returns>The recorder.</returns>
        public static RecordingAdmiral Create()
        {
            return (RecordingAdmiral)(object)DispatchProxy.Create<IAdmiralService, RecordingAdmiral>();
        }

        /// <summary>Wait for the next recorded process exit.</summary>
        /// <param name="timeout">How long to wait.</param>
        /// <returns>The exit.</returns>
        public async Task<RecordedProcessExit> NextExitAsync(TimeSpan timeout)
        {
            if (!await _Signal.WaitAsync(timeout).ConfigureAwait(false))
                throw new Exception("No process exit reached the Admiral within " + timeout.TotalSeconds + "s.");
            if (!_Exits.TryDequeue(out RecordedProcessExit? exit) || exit == null)
                throw new Exception("A process exit was signalled but none was recorded.");
            return exit;
        }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) return null;
            if (targetMethod.Name == nameof(IAdmiralService.HandleProcessExitAsync) && args != null && args.Length >= 4)
            {
                _Exits.Enqueue(new RecordedProcessExit((int)args[0]!, (int?)args[1], (string)args[2]!, (string)args[3]!));
                _Signal.Release();
                return Task.CompletedTask;
            }

            Type returnType = targetMethod.ReturnType;
            if (returnType == typeof(void)) return null;
            if (returnType == typeof(Task)) return Task.CompletedTask;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                Type resultType = returnType.GenericTypeArguments[0];
                object? result = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, new[] { result });
            }
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }

    /// <summary>A process exit recorded by <see cref="RecordingAdmiral"/>.</summary>
    public sealed class RecordedProcessExit
    {
        /// <summary>Process identifier.</summary>
        public int ProcessId { get; }

        /// <summary>Exit code, or null when none was reported.</summary>
        public int? ExitCode { get; }

        /// <summary>Captain identifier.</summary>
        public string CaptainId { get; }

        /// <summary>Mission identifier.</summary>
        public string MissionId { get; }

        /// <summary>Instantiate.</summary>
        public RecordedProcessExit(int processId, int? exitCode, string captainId, string missionId)
        {
            ProcessId = processId;
            ExitCode = exitCode;
            CaptainId = captainId;
            MissionId = missionId;
        }
    }
}
