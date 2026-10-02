namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Mission blocks read from an Architect's output by the shared block grammar.
    /// </summary>
    public sealed class ArchitectMissionBlockSplit
    {
        #region Public-Members

        /// <summary>Text before the first block: the plan narrative.</summary>
        public string Preamble { get; set; } = String.Empty;

        /// <summary>Body of each block, in order, without its markers.</summary>
        public List<string> Blocks { get; } = new List<string>();

        /// <summary>
        /// Index in <see cref="Blocks"/> of the first block that reached the next block or the end of the
        /// text without a closing marker; -1 when every block is closed.
        /// </summary>
        public int FirstUnterminatedIndex { get; set; } = -1;

        /// <summary>True when any block is missing its closing marker.</summary>
        public bool Unterminated => FirstUnterminatedIndex >= 0;

        #endregion
    }
}
