namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// One numbered finding a stage handed to the next stage: where it is, how much it blocks, and the required fix.
    /// </summary>
    public class HandoffFinding
    {
        #region Public-Members

        /// <summary>
        /// Position of the finding in the artifact, starting at 1.
        /// </summary>
        public int Number { get; set; } = 1;

        /// <summary>
        /// Affected file (repository-relative as the stage wrote it), or null.
        /// </summary>
        public string? File { get; set; } = null;

        /// <summary>
        /// Affected line, or null.
        /// </summary>
        public int? Line { get; set; } = null;

        /// <summary>
        /// How much the finding blocks the work.
        /// </summary>
        public HandoffFindingSeverityEnum Severity { get; set; } = HandoffFindingSeverityEnum.Unknown;

        /// <summary>
        /// The finding as the stage reported it (scenario, violated requirement, expected and actual behavior).
        /// </summary>
        public string Summary { get; set; } = "";

        /// <summary>
        /// The correction the stage required, or null when it named none.
        /// </summary>
        public string? RequiredFix { get; set; } = null;

        /// <summary>
        /// Whether the finding is resolved. A new artifact records findings as unresolved; a later stage reports them
        /// resolved, superseded or unresolved in its own output.
        /// </summary>
        public bool Resolved { get; set; } = false;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public HandoffFinding()
        {
        }

        #endregion
    }
}
