namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// One named step of an admiral health cycle: a periodic maintenance step or a sub-step of the
    /// health check. The runner isolates each step so a failure is reported by name and never stops
    /// the steps after it.
    /// </summary>
    public sealed class HealthLoopMaintenanceStep
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
}
