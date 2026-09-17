namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// What a duplicate comparison read and what it left out, so a short result can be told apart from a
    /// narrow one.
    /// </summary>
    public class CodeDuplicateCoverage
    {
        #region Public-Members

        /// <summary>
        /// Chunks in the persisted index.
        /// </summary>
        public int ChunksInIndex { get; set; } = 0;

        /// <summary>
        /// Chunks that took part in the exact-content comparison.
        /// </summary>
        public int ChunksCompared { get; set; } = 0;

        /// <summary>
        /// Chunks among <see cref="ChunksCompared"/> that also took part in the similarity comparison.
        /// </summary>
        public int ChunksWithEmbeddings { get; set; } = 0;

        /// <summary>
        /// Distinct files among the compared chunks.
        /// </summary>
        public int FilesCompared { get; set; } = 0;

        /// <summary>
        /// Chunks left out because they are marked reference-only.
        /// </summary>
        public int SkippedReferenceOnly { get; set; } = 0;

        /// <summary>
        /// Chunks left out by the path prefix or language filter.
        /// </summary>
        public int SkippedByFilter { get; set; } = 0;

        /// <summary>
        /// Chunks left out by an exclude path fragment.
        /// </summary>
        public int SkippedExcluded { get; set; } = 0;

        /// <summary>
        /// Chunks left out because they have fewer non-blank lines than the minimum.
        /// </summary>
        public int SkippedTooShort { get; set; } = 0;

        /// <summary>
        /// Compared chunks with no usable embedding vector; they take part in the exact-content comparison only.
        /// </summary>
        public int WithoutEmbedding { get; set; } = 0;

        /// <summary>
        /// Compared chunks per language.
        /// </summary>
        public Dictionary<string, int> Languages { get; set; } = new Dictionary<string, int>();

        #endregion
    }
}
