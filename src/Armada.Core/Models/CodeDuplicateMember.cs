namespace Armada.Core.Models
{
    /// <summary>
    /// One chunk in a group of similar code.
    /// </summary>
    public class CodeDuplicateMember
    {
        #region Public-Members

        /// <summary>
        /// Repo-relative path.
        /// </summary>
        public string Path { get; set; } = "";

        /// <summary>
        /// First line of the chunk, one-based.
        /// </summary>
        public int StartLine { get; set; } = 0;

        /// <summary>
        /// Last line of the chunk, one-based.
        /// </summary>
        public int EndLine { get; set; } = 0;

        /// <summary>
        /// Detected language.
        /// </summary>
        public string Language { get; set; } = "";

        /// <summary>
        /// Non-blank lines in the chunk.
        /// </summary>
        public int NonBlankLines { get; set; } = 0;

        /// <summary>
        /// Chunk content, when requested.
        /// </summary>
        public string? Content { get; set; } = null;

        #endregion
    }
}
