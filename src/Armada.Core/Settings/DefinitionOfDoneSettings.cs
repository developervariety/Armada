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
        /// Maximum seconds each command (build or unit-test) may run before it is killed and
        /// reported as a timeout failure. Clamped to [30, 3600]. Defaults to 300.
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

        #endregion

        #region Private-Members

        private string _DocOnlyMarker = "[DOD:DOC-ONLY]";
        private int _CommandTimeoutSeconds = 300;
        private int _OutputTailLines = 50;

        #endregion
    }
}
