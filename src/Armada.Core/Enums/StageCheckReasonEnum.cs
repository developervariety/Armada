namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Why a stage check ended with the status it did.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum StageCheckReasonEnum
    {
        /// <summary>
        /// No further reason: the check passed, or a command failed.
        /// </summary>
        [EnumMember(Value = "None")]
        None,

        /// <summary>
        /// Skipped: the captain's runtime is not checked (VerifyAllStageRuntimes is off).
        /// </summary>
        [EnumMember(Value = "RuntimeNotChecked")]
        RuntimeNotChecked,

        /// <summary>
        /// Skipped: the persona does not change code (Judge, Architect, Product Manager, Recorder).
        /// </summary>
        [EnumMember(Value = "ReviewPersona")]
        ReviewPersona,

        /// <summary>
        /// Skipped: a verifier persona reported a blocking product defect, so a failing check is its evidence.
        /// </summary>
        [EnumMember(Value = "VerifierReportedBlocker")]
        VerifierReportedBlocker,

        /// <summary>
        /// Skipped: the mission has no dock or vessel, or the dock has no worktree.
        /// </summary>
        [EnumMember(Value = "NoDock")]
        NoDock,

        /// <summary>
        /// Skipped: no workflow profile resolves for the vessel.
        /// </summary>
        [EnumMember(Value = "NoWorkflowProfile")]
        NoWorkflowProfile,

        /// <summary>
        /// Skipped: the workflow profile has no build or unit-test command.
        /// </summary>
        [EnumMember(Value = "NoCommands")]
        NoCommands,

        /// <summary>
        /// Skipped: a vendor-runtime stage left HEAD where it started, so there is nothing new to verify.
        /// </summary>
        [EnumMember(Value = "NoNewCommit")]
        NoNewCommit,

        /// <summary>
        /// Could not run: the dock's Harbor did not reconnect in time.
        /// </summary>
        [EnumMember(Value = "HarborDisconnected")]
        HarborDisconnected,

        /// <summary>
        /// Could not run: a command exceeded StageCheckTimeoutMinutes.
        /// </summary>
        [EnumMember(Value = "TimedOut")]
        TimedOut,

        /// <summary>
        /// Could not run: an error prevented the commands from running.
        /// </summary>
        [EnumMember(Value = "Error")]
        Error
    }
}
