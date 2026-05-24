namespace Armada.Core.Models
{
    /// <summary>
    /// A directed relationship between two symbols in the code graph.
    /// Emitted to the <c>edges.jsonl</c> sidecar alongside <c>chunks.jsonl</c>.
    /// </summary>
    public class CodeGraphEdgeRecord
    {
        #region Public-Members

        /// <summary>
        /// Vessel identifier this edge belongs to.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Commit SHA at which this edge was extracted.
        /// </summary>
        public string CommitSha { get; set; } = "";

        /// <summary>
        /// Discriminated edge kind (contains, inherits, calls, etc.).
        /// </summary>
        public CodeGraphEdgeKindEnum Kind { get; set; } = CodeGraphEdgeKindEnum.Unknown;

        /// <summary>
        /// Qualified or simple name of the source symbol (the one that declares or initiates the relationship).
        /// </summary>
        public string SourceSymbol { get; set; } = "";

        /// <summary>
        /// Qualified or simple name of the target symbol (the one being referenced or inherited).
        /// </summary>
        public string TargetSymbol { get; set; } = "";

        /// <summary>
        /// Repository-relative path of the file where the source symbol lives.
        /// </summary>
        public string SourcePath { get; set; } = "";

        /// <summary>
        /// 1-based line in the source file where the relationship is declared or invoked.
        /// </summary>
        public int SourceLine { get; set; } = 1;

        #endregion
    }
}
