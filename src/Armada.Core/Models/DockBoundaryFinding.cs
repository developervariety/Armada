namespace Armada.Core.Models
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Classification of a dock-boundary finding returned by DockBoundaryScanner.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DockBoundaryFindingKindEnum
    {
        /// <summary>A changed path matches a protected path glob pattern.</summary>
        ProtectedPath,

        /// <summary>An added line matches a CORE_RULE_5 secret pattern.</summary>
        Secret,

        /// <summary>
        /// An added line matches a configured private identifier denylist entry
        /// in a repository classified as public.
        /// </summary>
        PrivateIdentifier
    }

    /// <summary>
    /// A single blocking finding produced by DockBoundaryScanner.
    /// Secret bytes and private identifier values are never echoed in any field;
    /// only the rule or label name and the path are reported.
    /// </summary>
    public sealed class DockBoundaryFinding
    {
        #region Public-Members

        /// <summary>
        /// Classification of this finding.
        /// </summary>
        public DockBoundaryFindingKindEnum Kind { get; set; }

        /// <summary>
        /// Actionable human-readable message. Always non-empty.
        /// Names the path for protected-path findings; names the rule and path for
        /// secret findings; names the configured label and path for private-identifier
        /// findings. Never includes raw secret bytes or identifier values.
        /// </summary>
        public string Message
        {
            get => _Message;
            set => _Message = value ?? "";
        }

        /// <summary>
        /// Repository-relative path the finding concerns, or null when the finding
        /// applies to the whole diff rather than a specific file.
        /// </summary>
        public string? Path { get; set; }

        /// <summary>
        /// Redacted finding label.
        /// For secrets: the CORE_RULE_5 rule name (e.g. CORE_RULE_5_private_key).
        /// For private identifiers: the configured entry label.
        /// For protected-path findings: the matched glob pattern.
        /// Never contains raw secret material.
        /// </summary>
        public string FindingLabel
        {
            get => _FindingLabel;
            set => _FindingLabel = value ?? "";
        }

        #endregion

        #region Private-Members

        private string _Message = "";
        private string _FindingLabel = "";

        #endregion
    }
}
