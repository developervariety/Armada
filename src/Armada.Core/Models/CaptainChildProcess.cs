namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One live descendant of a captain's agent process, such as a build or a test run the agent started.
    /// </summary>
    public sealed class CaptainChildProcess
    {
        #region Public-Members

        /// <summary>Process identifier.</summary>
        public int ProcessId { get; }

        /// <summary>Parent process identifier.</summary>
        public int ParentProcessId { get; }

        /// <summary>Short command name as the operating system reports it.</summary>
        public string Name { get; }

        /// <summary>Start time in UTC.</summary>
        public DateTime StartedUtc { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="processId">Process identifier.</param>
        /// <param name="parentProcessId">Parent process identifier.</param>
        /// <param name="name">Short command name.</param>
        /// <param name="startedUtc">Start time in UTC.</param>
        public CaptainChildProcess(int processId, int parentProcessId, string name, DateTime startedUtc)
        {
            ProcessId = processId;
            ParentProcessId = parentProcessId;
            Name = name ?? String.Empty;
            StartedUtc = startedUtc;
        }

        #endregion
    }
}
