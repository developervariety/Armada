namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// What Armada knows about the captain that will read a handoff: its served context and the tools it actually has.
    /// Decided from hard facts by <see cref="Armada.Core.Services.HandoffReaderResolver"/>.
    /// </summary>
    public class HandoffReader
    {
        #region Public-Members

        /// <summary>
        /// The reader's served context in tokens, or null when it is not known.
        /// </summary>
        public int? ContextTokens { get; set; } = null;

        /// <summary>
        /// Whether the reader has the Armada MCP tools (get_handoff_artifact).
        /// </summary>
        public bool HasMcpTools { get; set; } = true;

        /// <summary>
        /// Whether the reader can read files in its worktree.
        /// </summary>
        public bool CanReadFiles { get; set; } = true;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public HandoffReader()
        {
        }

        #endregion
    }
}
