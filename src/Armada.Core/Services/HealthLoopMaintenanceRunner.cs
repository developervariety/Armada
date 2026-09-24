namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Runs the steps of an admiral health cycle so that one failing step cannot starve the steps
    /// after it. The health check's sub-steps, the background sweep triggers and the periodic
    /// maintenance steps all run through it. Maintenance intervals are multiples of each other
    /// (10, 50, 100, 200 cycles), so a step that throws on every run of a shorter cadence would
    /// otherwise abort every run of each longer cadence that shares its cycle numbers, and that
    /// step would never execute at all.
    /// </summary>
    public static class HealthLoopMaintenanceRunner
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
