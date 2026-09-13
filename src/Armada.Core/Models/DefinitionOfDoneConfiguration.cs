namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Current definition-of-done configuration as the gate would resolve it for one mission.
    /// Describing it runs no command, reads no diff and is not evidence that a gate ran.
    /// </summary>
    public class DefinitionOfDoneConfiguration
    {
        #region Public-Members

        /// <summary>
        /// Whether a gate is wired into mission completion on this server. When false, completion records no evaluation.
        /// </summary>
        public bool GateActive { get; set; } = false;

        /// <summary>
        /// Whether the active gate's settings enable it. These are the settings the gate was built with, which a
        /// settings reload does not replace.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Personas the gate applies to.
        /// </summary>
        public List<string> AppliedPersonas { get; set; } = new List<string>();

        /// <summary>
        /// Whether the mission persona is in <see cref="AppliedPersonas"/>.
        /// </summary>
        public bool PersonaApplies { get; set; } = false;

        /// <summary>
        /// Whether the mission description contains the doc-only opt-out marker.
        /// </summary>
        public bool DocOnlyMarkerPresent { get; set; } = false;

        /// <summary>
        /// Skip reason the gate would return for the current mission and settings, or null when it would evaluate.
        /// </summary>
        public string? ExpectedSkipReason { get; set; } = null;

        /// <summary>
        /// Selected workflow profile identifier, or null when none resolves.
        /// </summary>
        public string? WorkflowProfileId { get; set; } = null;

        /// <summary>
        /// Selected workflow profile name, or null.
        /// </summary>
        public string? WorkflowProfileName { get; set; } = null;

        /// <summary>
        /// Scope the profile was selected from (vessel, then fleet, then global), or null.
        /// </summary>
        public WorkflowProfileScopeEnum? WorkflowProfileScope { get; set; } = null;

        /// <summary>
        /// Whether the selected profile defines a build command. Command text is not exposed.
        /// </summary>
        public bool HasBuildCommand { get; set; } = false;

        /// <summary>
        /// Whether the selected profile defines a unit-test command.
        /// </summary>
        public bool HasUnitTestCommand { get; set; } = false;

        /// <summary>
        /// Whether the selected profile defines a containerless unit-test fallback.
        /// </summary>
        public bool HasContainerlessUnitTestCommand { get; set; } = false;

        /// <summary>
        /// Whether evaluation would fail for missing commands (no build and no unit-test command).
        /// </summary>
        public bool MissingCommands { get; set; } = false;

        /// <summary>
        /// Whether a restore runs before build.
        /// </summary>
        public bool RunRestoreBeforeBuild { get; set; } = false;

        /// <summary>
        /// Per-command timeout in seconds.
        /// </summary>
        public int CommandTimeoutSeconds { get; set; } = 0;

        /// <summary>
        /// Whether declared consumers are built.
        /// </summary>
        public bool VerifyDeclaredConsumers { get; set; } = false;

        /// <summary>
        /// Whether a consumer verification setup error fails the gate.
        /// </summary>
        public bool FailOnConsumerVerificationError { get; set; } = false;

        /// <summary>
        /// Whether consumer unit tests can run. Whether they run for a change is decided from the producer diff at evaluation.
        /// </summary>
        public bool RunConsumerTests { get; set; } = false;

        /// <summary>
        /// Global consumer-test trigger prefixes. A producer sibling declaration with its own prefixes overrides these per edge.
        /// </summary>
        public List<string> DefaultConsumerTestTriggerPaths { get; set; } = new List<string>();

        #endregion
    }
}
