namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;

    /// <summary>
    /// Process-wide record of every agent start a test host makes through <see cref="TestAgentRuntimeFactory"/>.
    /// A test host checks it after the run: any operating-system process started for a runtime that was not
    /// opted in fails the run, naming each executable and its exit code.
    /// </summary>
    public static class TestProcessLaunchLog
    {
        #region Private-Members

        private static readonly object _Lock = new object();
        private static readonly List<TestProcessLaunch> _Launches = new List<TestProcessLaunch>();

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record an agent start.
        /// </summary>
        /// <param name="launch">Start to record.</param>
        public static void Record(TestProcessLaunch launch)
        {
            if (launch == null) throw new ArgumentNullException(nameof(launch));
            lock (_Lock)
            {
                _Launches.Add(launch);
            }
        }

        /// <summary>
        /// Record the exit code of a recorded start.
        /// </summary>
        /// <param name="processId">Process identifier of the start.</param>
        /// <param name="exitCode">Reported exit code.</param>
        public static void RecordExit(int processId, int? exitCode)
        {
            lock (_Lock)
            {
                TestProcessLaunch? launch = _Launches.LastOrDefault(l => l.ProcessId == processId);
                if (launch != null) launch.ExitCode = exitCode;
            }
        }

        /// <summary>
        /// Snapshot of every recorded start.
        /// </summary>
        /// <returns>Recorded starts in order.</returns>
        public static List<TestProcessLaunch> All()
        {
            lock (_Lock)
            {
                return _Launches.ToList();
            }
        }

        /// <summary>
        /// Starts that created an operating-system process for a runtime outside <paramref name="permitted"/>.
        /// </summary>
        /// <param name="permitted">Runtimes the host opted in to a real launch.</param>
        /// <returns>Unpermitted process starts.</returns>
        public static List<TestProcessLaunch> UnpermittedProcessLaunches(IReadOnlyCollection<AgentRuntimeEnum> permitted)
        {
            return UnpermittedProcessLaunches(All(), permitted);
        }

        /// <summary>
        /// Describe the unpermitted process starts this process recorded, or return null when there were none.
        /// </summary>
        /// <param name="permitted">Runtimes the host opted in to a real launch.</param>
        /// <returns>Failure text naming the count, each executable and its exit code; null when none.</returns>
        public static string? DescribeUnpermittedProcessLaunches(IReadOnlyCollection<AgentRuntimeEnum> permitted)
        {
            return DescribeUnpermittedProcessLaunches(All(), permitted);
        }

        /// <summary>
        /// Describe the unpermitted process starts in <paramref name="recorded"/>, or return null when there were none.
        /// </summary>
        /// <param name="recorded">Recorded starts.</param>
        /// <param name="permitted">Runtimes the host opted in to a real launch.</param>
        /// <returns>Failure text naming the count, each executable and its exit code; null when none.</returns>
        public static string? DescribeUnpermittedProcessLaunches(IEnumerable<TestProcessLaunch> recorded, IReadOnlyCollection<AgentRuntimeEnum> permitted)
        {
            List<TestProcessLaunch> launches = UnpermittedProcessLaunches(recorded, permitted);
            if (launches.Count == 0) return null;

            IEnumerable<string> byName = launches
                .GroupBy(l => l.ProcessName ?? "unknown")
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key + " x" + g.Count());
            return "The test host started " + launches.Count + " agent process(es) outside the test runtime ("
                + String.Join(", ", byName) + "). Opt a runtime in with "
                + TestAgentRuntimeFactory.RealRuntimesVariable + " only when a suite needs it:"
                + Environment.NewLine + "  " + String.Join(Environment.NewLine + "  ", launches.Select(l => l.ToString()));
        }

        #endregion

        #region Private-Methods

        private static List<TestProcessLaunch> UnpermittedProcessLaunches(IEnumerable<TestProcessLaunch> recorded, IReadOnlyCollection<AgentRuntimeEnum> permitted)
        {
            if (recorded == null) throw new ArgumentNullException(nameof(recorded));
            if (permitted == null) throw new ArgumentNullException(nameof(permitted));
            return recorded.Where(l => l.LaunchedProcess && !permitted.Contains(l.RuntimeType)).ToList();
        }

        #endregion
    }
}
