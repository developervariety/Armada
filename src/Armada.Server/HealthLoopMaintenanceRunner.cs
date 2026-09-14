namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// One periodic maintenance step of the admiral health loop.
    /// </summary>
    internal sealed class HealthLoopMaintenanceStep
    {
        #region Public-Members

        /// <summary>
        /// Name used when the step fails.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Returns true when the step is due on the given health-loop cycle.
        /// </summary>
        public Func<int, bool> IsDue { get; }

        /// <summary>
        /// The step body.
        /// </summary>
        public Func<CancellationToken, Task> RunAsync { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="name">Step name.</param>
        /// <param name="isDue">Due predicate for a cycle number.</param>
        /// <param name="runAsync">Step body.</param>
        public HealthLoopMaintenanceStep(string name, Func<int, bool> isDue, Func<CancellationToken, Task> runAsync)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
            Name = name;
            IsDue = isDue ?? throw new ArgumentNullException(nameof(isDue));
            RunAsync = runAsync ?? throw new ArgumentNullException(nameof(runAsync));
        }

        /// <summary>
        /// A step due on every cycle whose number is a multiple of the interval read at call time.
        /// </summary>
        /// <param name="name">Step name.</param>
        /// <param name="intervalCycles">Interval provider; values below one run every cycle.</param>
        /// <param name="runAsync">Step body.</param>
        /// <returns>The step.</returns>
        public static HealthLoopMaintenanceStep EveryCycles(string name, Func<int> intervalCycles, Func<CancellationToken, Task> runAsync)
        {
            if (intervalCycles == null) throw new ArgumentNullException(nameof(intervalCycles));
            return new HealthLoopMaintenanceStep(name, cycle =>
            {
                int interval = intervalCycles();
                return interval <= 1 || cycle % interval == 0;
            }, runAsync);
        }

        #endregion
    }

    /// <summary>
    /// Runs the admiral's periodic maintenance steps so that one failing step cannot starve the
    /// steps after it. Intervals are multiples of each other (10, 50, 100, 200 cycles), so a step
    /// that throws on every run of a shorter cadence would otherwise abort every run of each longer
    /// cadence that shares its cycle numbers, and that step would never execute at all.
    /// </summary>
    internal static class HealthLoopMaintenanceRunner
    {
        /// <summary>
        /// Run every step due on the cycle, each in isolation.
        /// </summary>
        /// <param name="cycle">Health-loop cycle number.</param>
        /// <param name="steps">Steps in execution order.</param>
        /// <param name="onFailure">Observer for a failed step and its exception.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of steps that failed.</returns>
        public static async Task<int> RunDueStepsAsync(
            int cycle,
            IReadOnlyList<HealthLoopMaintenanceStep> steps,
            Action<string, Exception> onFailure,
            CancellationToken token)
        {
            if (steps == null) throw new ArgumentNullException(nameof(steps));
            if (onFailure == null) throw new ArgumentNullException(nameof(onFailure));

            int failures = 0;
            foreach (HealthLoopMaintenanceStep step in steps)
            {
                token.ThrowIfCancellationRequested();

                bool due;
                try
                {
                    due = step.IsDue(cycle);
                }
                catch (Exception ex)
                {
                    failures++;
                    onFailure(step.Name, ex);
                    continue;
                }

                if (!due) continue;

                try
                {
                    await step.RunAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures++;
                    onFailure(step.Name, ex);
                }
            }

            return failures;
        }
    }
}
