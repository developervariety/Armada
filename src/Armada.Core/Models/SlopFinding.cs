namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// One Slop pattern found on an added line of a reviewed diff.
    /// </summary>
    public class SlopFinding
    {
        #region Public-Members

        /// <summary>
        /// The rule that matched.
        /// </summary>
        public SlopRuleEnum Rule { get; set; } = SlopRuleEnum.SkippedTest;

        /// <summary>
        /// The severity the rule carries.
        /// </summary>
        public SlopSeverityEnum Severity { get; set; } = SlopSeverityEnum.Fail;

        /// <summary>
        /// Repository-relative path of the file, as the diff names it.
        /// </summary>
        public string Path { get; set; } = String.Empty;

        /// <summary>
        /// One-based line number on the new side of the diff.
        /// </summary>
        public int Line { get; set; } = 0;

        /// <summary>
        /// The trimmed text of the matched line.
        /// </summary>
        public string Text { get; set; } = String.Empty;

        /// <summary>
        /// Why the pattern is reported.
        /// </summary>
        public string Explanation { get; set; } = String.Empty;

        /// <summary>
        /// True when a suppression marker with a recorded reason covers this finding.
        /// </summary>
        public bool Suppressed { get; set; } = false;

        /// <summary>
        /// The reason recorded by the suppression marker, when suppressed.
        /// </summary>
        public string? SuppressionReason { get; set; } = null;

        /// <summary>
        /// Why a suppression marker near this finding was not honored, when one was present.
        /// </summary>
        public string? SuppressionProblem { get; set; } = null;

        #endregion
    }
}
