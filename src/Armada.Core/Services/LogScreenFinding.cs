namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// One thing a screening pass reported about one mission's log tail. A finding is advisory: it
    /// reaches the operator as a board note and an event, and carries no authority over the mission.
    /// </summary>
    public class LogScreenFinding
    {
        #region Public-Members

        /// <summary>
        /// The rule class this finding belongs to. Per-class counts are aggregated on this value,
        /// so it is a stable identifier rather than prose.
        /// </summary>
        public string RuleClass { get; set; } = String.Empty;

        /// <summary>
        /// Exactly one line of the tail that shows the finding. One line keeps the board note
        /// readable and keeps the quoted material bounded; any embedded line break is removed.
        /// </summary>
        public string EvidenceLine
        {
            get => _EvidenceLine;
            set => _EvidenceLine = Normalize(value);
        }

        /// <summary>
        /// Which kind of pass produced the finding: "rule" for a deterministic pass, "model" for a
        /// pass that consulted a model.
        /// </summary>
        public string Source { get; set; } = SourceRule;

        /// <summary>
        /// Confidence reported by the producing pass, when it has one. A deterministic rule leaves
        /// this null: it does not estimate, it matched or it did not.
        /// </summary>
        public double? Confidence { get; set; } = null;

        /// <summary>
        /// Optional short explanation of why the line matched.
        /// </summary>
        public string? Detail { get; set; } = null;

        #endregion

        #region Public-Constants

        /// <summary>
        /// <see cref="Source"/> value for a finding produced by a deterministic rule.
        /// </summary>
        public const string SourceRule = "rule";

        /// <summary>
        /// <see cref="Source"/> value for a finding produced by a pass that consulted a model.
        /// </summary>
        public const string SourceModel = "model";

        #endregion

        #region Private-Members

        private string _EvidenceLine = String.Empty;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public LogScreenFinding()
        {
        }

        #endregion

        #region Private-Methods

        private static string Normalize(string? value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            return value.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        #endregion
    }
}
