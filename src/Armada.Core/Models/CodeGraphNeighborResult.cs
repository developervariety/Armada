namespace Armada.Core.Models
{
    /// <summary>
    /// A scored direct graph neighbor.
    /// </summary>
    public class CodeGraphNeighborResult
    {
        #region Public-Members

        /// <summary>
        /// Traversal score. Higher is better.
        /// </summary>
        public double Score { get; set; } = 0;

        /// <summary>
        /// Relationship kind used to resolve this neighbor.
        /// </summary>
        public CodeGraphEdgeKindEnum EdgeKind { get; set; } = CodeGraphEdgeKindEnum.Unknown;

        /// <summary>
        /// Traversal depth from the seed symbol.
        /// </summary>
        public int TraversalDepth { get; set; } = 1;

        /// <summary>
        /// Matched neighboring symbol.
        /// </summary>
        public CodeGraphSymbolRecord Symbol { get; set; } = new CodeGraphSymbolRecord();

        /// <summary>
        /// Short reason for ranking.
        /// </summary>
        public string Reason { get; set; } = "";

        #endregion
    }
}
