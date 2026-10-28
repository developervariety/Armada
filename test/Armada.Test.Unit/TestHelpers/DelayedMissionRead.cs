namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Reflection;
    using System.Runtime.ExceptionServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>Holds one real mission read until a test advances the launch generation.</summary>
    public class DelayedMissionRead : DispatchProxy
    {
        private IMissionMethods _Inner = null!;
        private string _MissionId = String.Empty;
        private int _Remaining;
        private readonly TaskCompletionSource<bool> _Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the selected read reaches the controlled boundary.</summary>
        public Task Started => _Started.Task;

        /// <summary>Allow the selected read to observe the current durable mission.</summary>
        public void Release() => _Release.TrySetResult(true);

        /// <summary>Install the one-read delay without changing other database operations.</summary>
        /// <param name="database">Real test provider.</param>
        /// <param name="missionId">Mission whose selected full or summary read is delayed.</param>
        /// <param name="readNumber">One-based matching read to delay.</param>
        /// <returns>The installed delay control.</returns>
        public static DelayedMissionRead Install(DatabaseDriver database, string missionId, int readNumber = 1)
        {
            if (readNumber < 1) throw new ArgumentOutOfRangeException(nameof(readNumber));
            IMissionMethods methods = Create<IMissionMethods, DelayedMissionRead>();
            DelayedMissionRead control = (DelayedMissionRead)(object)methods;
            control._Inner = database.Missions;
            control._MissionId = missionId;
            control._Remaining = readNumber;
            typeof(DatabaseDriver).GetProperty(nameof(DatabaseDriver.Missions))!.SetValue(database, methods);
            return control;
        }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) throw new ArgumentNullException(nameof(targetMethod));
            if ((targetMethod.Name == nameof(IMissionMethods.ReadAsync) || targetMethod.Name == nameof(IMissionMethods.ReadSummaryAsync))
                && args?.Length > 0 && args[0] as string == _MissionId
                && Interlocked.Decrement(ref _Remaining) == 0)
                return ReadDelayedAsync(targetMethod, args);
            return Forward(targetMethod, args);
        }

        private async Task<Mission?> ReadDelayedAsync(MethodInfo method, object?[] args)
        {
            _Started.TrySetResult(true);
            await _Release.Task.ConfigureAwait(false);
            return await ((Task<Mission?>)Forward(method, args)!).ConfigureAwait(false);
        }

        private object? Forward(MethodInfo method, object?[]? args)
        {
            try { return method.Invoke(_Inner, args); }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }
}
