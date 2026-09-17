namespace Armada.Core.Models
{
    /// <summary>
    /// One chunk of a source file as a zero-based, end-exclusive line range.
    /// </summary>
    public class CodeChunkRange
    {
        #region Public-Members

        /// <summary>
        /// Zero-based index of the first line in the chunk.
        /// </summary>
        public int StartIndex { get; set; } = 0;

        /// <summary>
        /// Zero-based index one past the last line in the chunk.
        /// </summary>
        public int EndIndexExclusive { get; set; } = 0;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate an empty range.
        /// </summary>
        public CodeChunkRange()
        {
        }

        /// <summary>
        /// Instantiate a range.
        /// </summary>
        /// <param name="startIndex">Zero-based index of the first line.</param>
        /// <param name="endIndexExclusive">Zero-based index one past the last line.</param>
        public CodeChunkRange(int startIndex, int endIndexExclusive)
        {
            StartIndex = startIndex;
            EndIndexExclusive = endIndexExclusive;
        }

        #endregion
    }
}
