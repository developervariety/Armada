namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;

    /// <summary>
    /// The outcome of classifying one reviewed diff for Slop patterns.
    /// </summary>
    public class SlopClassificationResult
    {
        #region Public-Members

        /// <summary>
        /// Every finding, suppressed or not, in diff order.
        /// </summary>
        public List<SlopFinding> Findings { get; set; } = new List<SlopFinding>();

        /// <summary>
        /// Number of C# and MSBuild files the diff added or changed lines in.
        /// </summary>
        public int FilesExamined { get; set; } = 0;

        /// <summary>
        /// Number of added lines the rules were applied to.
        /// </summary>
        public int AddedLinesExamined { get; set; } = 0;

        /// <summary>
        /// Whether the repository manages package versions centrally at the reviewed commit.
        /// </summary>
        public bool CentralPackageManagement { get; set; } = false;

        /// <summary>
        /// Unsuppressed findings that fail the check.
        /// </summary>
        public int FailCount
        {
            get { return Findings.Count(f => !f.Suppressed && f.Severity == SlopSeverityEnum.Fail); }
        }

        /// <summary>
        /// Unsuppressed findings reported without failing the check.
        /// </summary>
        public int WarnCount
        {
            get { return Findings.Count(f => !f.Suppressed && f.Severity == SlopSeverityEnum.Warn); }
        }

        /// <summary>
        /// Findings covered by a suppression marker with a recorded reason.
        /// </summary>
        public int SuppressedCount
        {
            get { return Findings.Count(f => f.Suppressed); }
        }

        /// <summary>
        /// True when no unsuppressed FAIL finding exists.
        /// </summary>
        public bool Passed
        {
            get { return FailCount == 0; }
        }

        #endregion
    }
}
