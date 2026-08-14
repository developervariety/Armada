namespace Armada.Core.Services
{
    /// <summary>
    /// A single dock-boundary finding. Deliberately carries no secret bytes -- only the kind of
    /// violation, the rule that matched, the offending path, and (for secrets) the diff line number.
    /// </summary>
    public sealed class BoundaryFinding
    {
        #region Public-Members

        /// <summary>
        /// The category of violation: "secret", "protected-path", or "private-identifier".
        /// </summary>
        public string Kind { get; }

        /// <summary>
        /// The rule that matched (a secret rule id, the protected-path glob, or the private identifier).
        /// </summary>
        public string RuleId { get; }

        /// <summary>
        /// The changed file path the finding applies to.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// For secret/identifier findings, the 1-based line number within the diff; null for path findings.
        /// </summary>
        public int? Line { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate a finding.
        /// </summary>
        /// <param name="kind">Violation category.</param>
        /// <param name="ruleId">Matched rule identifier.</param>
        /// <param name="path">Offending path.</param>
        /// <param name="line">Diff line number, or null.</param>
        public BoundaryFinding(string kind, string ruleId, string path, int? line)
        {
            Kind = kind ?? string.Empty;
            RuleId = ruleId ?? string.Empty;
            Path = path ?? string.Empty;
            Line = line;
        }

        #endregion
    }
}
