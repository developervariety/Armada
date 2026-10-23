namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Payload of the event recorded when a definition-of-done gate whose command hit its time limit while the
    /// host ran overloaded, while another build or test command shared the host, or while the command was still
    /// writing output, is run once more with the host to itself.
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
        public const int CurrentSchemaVersion = 3;

        /// <summary>
        /// Reason recorded when the run stood under host load. The gate waited for load to fall first.
        /// </summary>
        public const string HostOverloadedReason = "host_overloaded";

        /// <summary>
        /// Reason recorded when the run stood at normal load but shared the host command slots with another build or
        /// test command. The gate does not wait for load; the re-run waits until it can hold the host alone.
        /// </summary>
        public const string HostSlotSharedReason = "host_slot_shared";

        /// <summary>
        /// Reason recorded when the run stood at normal load and held its slot alone, but the command was still
        /// writing output at its time limit, so it was working rather than hung.
        /// </summary>
        public const string OutputProgressingReason = "output_progressing";

        /// <summary>
        /// Payload version.
        /// </summary>
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>
        /// Why the gate ran once more: <see cref="HostOverloadedReason"/>, <see cref="HostSlotSharedReason"/> or
        /// <see cref="OutputProgressingReason"/>.
        /// </summary>
        public string Reason { get; set; } = HostOverloadedReason;

        /// <summary>
        /// True when the timed-out command held its host slot at the same time as another build or test command.
        /// </summary>
        public bool SharedHostSlot { get; set; } = false;

        /// <summary>
        /// True when the timed-out command was still writing output when its time limit fired.
        /// </summary>
        public bool OutputProgressing { get; set; } = false;

        /// <summary>
        /// True when the re-run holds the host alone: it starts once no other build or test command holds a slot,
        /// and no other is granted while it runs.
        /// </summary>
        public bool RerunAlone { get; set; } = false;

        /// <summary>
        /// Label of the command that timed out, for example <c>unit-test</c>.
        /// </summary>
        public string? TimedOutCommandLabel { get; set; } = null;

        /// <summary>
        /// 1-minute load average read when the gate failed.
        /// </summary>
        public double? LoadAtTimeout { get; set; } = null;

        /// <summary>
        /// 15-minute load average read when the gate failed, or null when it was unknown.
        /// </summary>
        public double? LongLoadAtTimeout { get; set; } = null;

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
