namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    /// <summary>
    /// In-process registry of running definition-of-done evaluations, keyed by mission. Cancelling a mission cancels
    /// its running evaluations, so the gate's command process is stopped and the host-wide command slot is released
    /// instead of running a suite for work nobody will land.
    /// <para>
    /// The registry lives in process memory by design: an evaluation exists only inside the process running it.
    /// </para>
    /// </summary>
    public static class DefinitionOfDoneGateRuns
    {
        #region Private-Members

        private static readonly object _Lock = new object();
        private static readonly Dictionary<string, List<DefinitionOfDoneGateRun>> _Runs =
            new Dictionary<string, List<DefinitionOfDoneGateRun>>(StringComparer.Ordinal);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register an evaluation for a mission. Dispose the returned run when the evaluation ends.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="token">Caller's token; the run's token is cancelled with it.</param>
        /// <returns>The registered run.</returns>
        public static DefinitionOfDoneGateRun Start(string missionId, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(missionId)) throw new ArgumentNullException(nameof(missionId));

            DefinitionOfDoneGateRun run = new DefinitionOfDoneGateRun(missionId, token);
            lock (_Lock)
            {
                if (!_Runs.TryGetValue(missionId, out List<DefinitionOfDoneGateRun>? runs))
                {
                    runs = new List<DefinitionOfDoneGateRun>();
                    _Runs[missionId] = runs;
                }

                runs.Add(run);
            }

            return run;
        }

        /// <summary>
        /// Cancel every running evaluation for a mission.
        /// </summary>
        /// <param name="missionId">Mission identifier; null or empty cancels nothing.</param>
        /// <returns>The number of evaluations cancelled.</returns>
        public static int Cancel(string? missionId)
        {
            if (String.IsNullOrWhiteSpace(missionId)) return 0;

            List<DefinitionOfDoneGateRun> toCancel;
            lock (_Lock)
            {
                if (!_Runs.TryGetValue(missionId, out List<DefinitionOfDoneGateRun>? runs)) return 0;
                toCancel = new List<DefinitionOfDoneGateRun>(runs);
            }

            foreach (DefinitionOfDoneGateRun run in toCancel)
            {
                run.CancelForMission();
            }

            return toCancel.Count;
        }

        /// <summary>
        /// True when at least one evaluation is running for the mission.
        /// </summary>
        /// <param name="missionId">Mission identifier; may be null.</param>
        /// <returns>True when an evaluation is registered.</returns>
        public static bool IsRunning(string? missionId)
        {
            if (String.IsNullOrWhiteSpace(missionId)) return false;
            lock (_Lock)
            {
                return _Runs.ContainsKey(missionId);
            }
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Remove a finished run.
        /// </summary>
        /// <param name="run">Run to remove.</param>
        internal static void Remove(DefinitionOfDoneGateRun run)
        {
            if (run == null) return;
            lock (_Lock)
            {
                if (!_Runs.TryGetValue(run.MissionId, out List<DefinitionOfDoneGateRun>? runs)) return;
                runs.Remove(run);
                if (runs.Count == 0) _Runs.Remove(run.MissionId);
            }
        }

        #endregion
    }
}
