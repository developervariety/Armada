namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Payload of the event recorded when a definition-of-done gate waits for an overloaded host before its first
    /// command, so the command time limits count run time and not time spent queued behind host load.
    /// </summary>
    public class DefinitionOfDoneLoadAdmissionRecord
    {
        #region Public-Members

        /// <summary>
        /// Event type under which load admission records are stored.
        /// </summary>
        public const string EventType = "mission.definition_of_done_load_admission";

        /// <summary>
        /// Reason code for a gate that waited because the host was overloaded when it was due to start.
        /// </summary>
        public const string HostOverloadedReason = "host_overloaded";

        /// <summary>
        /// Payload version this code writes.
        /// </summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>
        /// Payload version.
        /// </summary>
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>
        /// Why the gate waited. Always <see cref="HostOverloadedReason"/>.
        /// </summary>
        public string Reason { get; set; } = HostOverloadedReason;

        /// <summary>
        /// 1-minute load average read when the gate was due to start.
        /// </summary>
        public double? LoadAtAdmission { get; set; } = null;

        /// <summary>
        /// Logical core count the readings are compared against.
        /// </summary>
        public int LogicalCores { get; set; } = 1;

        /// <summary>
        /// Load average above which the host counts as overloaded.
        /// </summary>
        public double OverloadThreshold { get; set; } = 0;

        /// <summary>
        /// 1-minute load average read when the gate started, or null when it was unknown.
        /// </summary>
        public double? LoadAtStart { get; set; } = null;

        /// <summary>
        /// True when the host stopped being overloaded before the gate started; false when the maximum wait
        /// ended first and the gate started anyway.
        /// </summary>
        public bool LoadFell { get; set; } = false;

        /// <summary>
        /// Seconds the gate waited before its first command.
        /// </summary>
        public double WaitedSeconds { get; set; } = 0;

        /// <summary>
        /// Maximum seconds the gate waits for the host before it starts anyway.
        /// </summary>
        public double MaximumWaitSeconds { get; set; } = 0;

        /// <summary>
        /// UTC time the gate started its first command.
        /// </summary>
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public DefinitionOfDoneLoadAdmissionRecord()
        {
        }

        #endregion
    }
}
