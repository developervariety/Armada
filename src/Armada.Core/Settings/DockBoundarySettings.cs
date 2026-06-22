namespace Armada.Core.Settings
{
    using System.Collections.Generic;

    /// <summary>
    /// A single private identifier entry whose pattern must not appear in diffs
    /// targeting public repositories. The label is reported in findings; the
    /// pattern and matched value are never echoed verbatim in failure messages.
    /// </summary>
    public sealed class DockBoundaryPrivateIdentifierEntry
    {
        #region Public-Members

        /// <summary>
        /// Human-readable label reported in findings. Never contains the identifier value.
        /// Example: "internal-org-name", "employee-id-prefix".
        /// </summary>
        public string Label
        {
            get => _Label;
            set => _Label = value ?? "";
        }

        /// <summary>
        /// Regex pattern matched against added '+' lines of the unified diff.
        /// Entries with null or whitespace patterns are silently skipped.
        /// </summary>
        public string Pattern
        {
            get => _Pattern;
            set => _Pattern = value ?? "";
        }

        #endregion

        #region Private-Members

        private string _Label = "";
        private string _Pattern = "";

        #endregion
    }

    /// <summary>
    /// Configuration for dock-boundary diff scanning applied before any commit,
    /// push, or landing path can proceed. Controls which checks run and which
    /// vessels are treated as public for private-identifier scanning.
    /// </summary>
    public sealed class DockBoundarySettings
    {
        #region Public-Members

        /// <summary>
        /// Whether CORE_RULE_5 secret pattern scanning is enabled. Default true.
        /// When enabled, added lines in the unified diff are matched against the
        /// ConventionChecker secret rule set and blocking findings are raised.
        /// </summary>
        public bool SecretScanEnabled { get; set; } = true;

        /// <summary>
        /// Whether private-identifier denylist scanning is enabled. Default true.
        /// Scanning only runs when the vessel matches at least one PublicRepoPatterns entry.
        /// </summary>
        public bool PrivateIdentifierScanEnabled { get; set; } = true;

        /// <summary>
        /// Substring or glob patterns matched (case-insensitive) against vessel id,
        /// vessel name, or repo URL to classify a vessel as public. Private-identifier
        /// scanning is active only when one of these patterns matches. An empty list
        /// means no vessel is treated as public and private-identifier scanning is
        /// effectively disabled regardless of PrivateIdentifierScanEnabled.
        /// </summary>
        public List<string> PublicRepoPatterns { get; set; } = new List<string>();

        /// <summary>
        /// Denylist of private identifiers that must not appear in diffs targeting
        /// public-flagged repositories. Each entry specifies a label (reported in
        /// findings) and a regex pattern (matched against added lines).
        /// </summary>
        public List<DockBoundaryPrivateIdentifierEntry> PrivateIdentifiers
        {
            get => _PrivateIdentifiers;
            set => _PrivateIdentifiers = value ?? new List<DockBoundaryPrivateIdentifierEntry>();
        }

        #endregion

        #region Private-Members

        private List<DockBoundaryPrivateIdentifierEntry> _PrivateIdentifiers = new List<DockBoundaryPrivateIdentifierEntry>();

        #endregion
    }
}
