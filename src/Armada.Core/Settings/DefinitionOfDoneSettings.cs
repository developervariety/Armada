namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Controls the in-dock build and unit-test gate that runs before a Worker mission is
    /// accepted as complete. When enabled, a Worker mission must pass the vessel's configured
    /// BuildCommand and UnitTestCommand before handoff and landing proceed.
    /// </summary>
    public class DefinitionOfDoneSettings
    {
        #region Public-Members

        /// <summary>
        /// Whether the definition-of-done gate is active. Defaults to true.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Personas the gate applies to. Defaults to Worker only.
        /// </summary>
        public List<string> AppliedPersonas { get; set; } = new List<string> { "Worker" };

        /// <summary>
        /// Marker text that, when present anywhere in the mission description, opts the mission
        /// out of in-dock build and unit-test verification. Intended for documentation-only missions
        /// that produce no compilable code. Defaults to "[DOD:DOC-ONLY]".
        /// </summary>
        public string DocOnlyMarker
        {
            get => _DocOnlyMarker;
            set => _DocOnlyMarker = String.IsNullOrWhiteSpace(value) ? "[DOD:DOC-ONLY]" : value.Trim();
        }

        /// <summary>
        /// When true, the gate strips the <c>--no-restore</c> token from the build and
        /// unit-test commands before executing them, so a fresh dock performs NuGet (or
        /// equivalent) package restore as part of the normal build or test invocation.
        /// Set to false to leave the commands untouched (legacy behavior). Defaults to true.
        /// </summary>
        public bool RunRestoreBeforeBuild { get; set; } = true;

        /// <summary>
        /// Maximum seconds each command (build, restore, or unit-test) may run before it is
        /// killed and reported as a timeout failure. Clamped to [30, 3600]. Defaults to 600.
        /// </summary>
        public int CommandTimeoutSeconds
        {
            get => _CommandTimeoutSeconds;
            set => _CommandTimeoutSeconds = Math.Max(30, Math.Min(3600, value));
        }

        /// <summary>
        /// Maximum number of trailing output lines included in the failure reason. Clamped to
        /// [10, 500]. Defaults to 50.
        /// </summary>
        public int OutputTailLines
        {
            get => _OutputTailLines;
            set => _OutputTailLines = Math.Max(10, Math.Min(500, value));
        }

        /// <summary>
        /// Maximum number of leading recognized compiler or test-failure diagnostic lines
        /// retained from the complete command output. Clamped to [1, 100]. Defaults to 20.
        /// </summary>
        public int DiagnosticLines
        {
            get => _DiagnosticLines;
            set => _DiagnosticLines = Math.Max(1, Math.Min(100, value));
        }

        /// <summary>
        /// Whether a passing gate also builds the vessels that declare this vessel as a sibling
        /// repository. Defaults to true.
        /// </summary>
        /// <remarks>
        /// A producer's own build cannot observe a break it causes in a consumer: the consumer is
        /// a different repository with a different compilation. The gate passes, the branch lands,
        /// and the break surfaces on the consumer's next build, attributed to whatever ran then
        /// rather than to the change that caused it. Building the declared consumers closes that
        /// gap at the only point where the producer's change is still unlanded.
        /// <para>
        /// Verification requires a git seam; without one the step is skipped rather than failed,
        /// so a gate constructed without that dependency behaves exactly as before.
        /// </para>
        /// </remarks>
        public bool VerifyDeclaredConsumers { get; set; } = true;

        /// <summary>
        /// Whether a consumer that cannot be prepared fails the producer's gate. Defaults to
        /// false, which reports the problem and lets the gate pass.
        /// </summary>
        /// <remarks>
        /// A missing repository, an absent workflow profile, or a worktree that will not
        /// provision is an infrastructure fault in the verification, not evidence that the
        /// producer's change is wrong. Failing the producer for it would block landing on a
        /// condition the captain cannot fix. A consumer that genuinely fails to COMPILE is a
        /// different matter and always fails the gate.
        /// </remarks>
        public bool FailOnConsumerVerificationError { get; set; } = false;

        #endregion

        #region Private-Members

        private string _DocOnlyMarker = "[DOD:DOC-ONLY]";
        private int _CommandTimeoutSeconds = 600;
        private int _OutputTailLines = 50;
        private int _DiagnosticLines = 20;

        #endregion
    }
}
