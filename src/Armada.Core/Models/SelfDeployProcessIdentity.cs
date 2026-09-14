namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Identity of a process: its id plus its exact start time, so a reused id is never mistaken for it.
    /// </summary>
    public sealed class SelfDeployProcessIdentity
    {
        /// <summary>
        /// Operating-system process id.
        /// </summary>
        public int ProcessId { get; set; }

        /// <summary>
        /// Process start time in UTC as reported by the operating system.
        /// </summary>
        public DateTime StartedUtc { get; set; }
    }
}
