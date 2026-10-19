namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Payload of the event recorded when a definition-of-done gate that failed with a <c>Timeout</c>
    /// classification while the host was overloaded is run again after load falls.
    /// </summary>
    public class DefinitionOfDoneLoadRerunRecord
    {
        #region Public-Members

        /// <summary>
        /// Event type under which load re-run records are stored.
        /// </summary>
        public const string EventType = "mission.definition_of_done_load_rerun";

        /// <summary>
        /// Payload version this code writes.
        /// </summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>
        /// Payload version.
        /// </summary>
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>
        /// Label of the command that timed out, for example <c>unit-test</c>.
        /// </summary>
        public string? TimedOutCommandLabel { get; set; } = null;

        /// <summary>
        /// 1-minute load average read when the gate failed.
        /// </summary>
        public double? LoadAtTimeout { get; set; } = null;

        /// <summary>
        /// Logical core count the readings are compared against.
        /// </summary>
        public int LogicalCores { get; set; } = 1;

        /// <summary>
        /// Load average above which the host counts as overloaded.
        /// </summary>
        public double OverloadThreshold { get; set; } = 0;

        /// <summary>
        /// Load average at or below which load counts as fallen.
        /// </summary>
        public double RecoveredThreshold { get; set; } = 0;

        /// <summary>
        /// 1-minute load average read when the re-run started, or null when it was unknown.
        /// </summary>
        public double? LoadAtRerun { get; set; } = null;

        /// <summary>
        /// True when load fell before the re-run; false when the maximum wait ended first.
        /// </summary>
        public bool LoadFell { get; set; } = false;

        /// <summary>
        /// Seconds waited for load to fall before the re-run.
        /// </summary>
        public double WaitedSeconds { get; set; } = 0;

        /// <summary>
        /// Maximum seconds the gate waits for load to fall.
        /// </summary>
        public double MaximumWaitSeconds { get; set; } = 0;

        /// <summary>
        /// UTC time the re-run started.
        /// </summary>
        public DateTime RerunStartedUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public DefinitionOfDoneLoadRerunRecord()
        {
        }

        #endregion
    }
}
