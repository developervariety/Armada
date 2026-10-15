namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// What <see cref="HarborShellPath"/> did to the Harbor process's PATH at startup.
    /// </summary>
    public class HarborPathImportResult
    {
        #region Public-Members

        /// <summary>
        /// The PATH the process had before the import.
        /// </summary>
        public string OriginalPath { get; set; } = String.Empty;

        /// <summary>
        /// The PATH after the import (the same as <see cref="OriginalPath"/> when nothing was added).
        /// </summary>
        public string Path { get; set; } = String.Empty;

        /// <summary>
        /// Whether the login shell's PATH was read.
        /// </summary>
        public bool LoginShellRead { get; set; } = false;

        /// <summary>
        /// The login shell that was asked, or null when none was (Windows, or no shell found).
        /// </summary>
        public string? Shell { get; set; } = null;

        /// <summary>
        /// Directories added to the original PATH, in order.
        /// </summary>
        public List<string> Added { get; set; } = new List<string>();

        /// <summary>
        /// Why the login shell's PATH could not be read, or null.
        /// </summary>
        public string? Error { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public HarborPathImportResult()
        {
        }

        #endregion
    }
}
