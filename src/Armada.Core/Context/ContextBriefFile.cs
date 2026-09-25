namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// One AI-Memory file delivered into a captain's dock beside the mission brief. The brief lists
    /// every delivered file by its dock-relative path; the captain reads each one with its own file
    /// tool, so each file is bounded to fit a single read on every runtime.
    /// </summary>
    public sealed class ContextBriefFile
    {
        #region Public-Members

        /// <summary>Dock-relative path, using forward slashes (for example <c>_briefing/memory/01-core.md</c>).</summary>
        public string RelativePath { get; set; } = String.Empty;

        /// <summary>File content.</summary>
        public string Content { get; set; } = String.Empty;

        /// <summary>True when the captain must read the file before it starts; false for reference material.</summary>
        public bool ReadFirst { get; set; } = true;

        /// <summary>Topic ids of the memory chunks the file carries, in file order.</summary>
        public List<string> Topics { get; set; } = new List<string>();

        /// <summary>UTF-8 byte count of <see cref="Content"/>.</summary>
        public int Bytes => Encoding.UTF8.GetByteCount(Content ?? String.Empty);

        #endregion
    }
}
