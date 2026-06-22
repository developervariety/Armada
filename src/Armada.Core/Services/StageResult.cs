namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Result returned by <see cref="ReadContextStager.Stage"/>.
    /// On success <see cref="Error"/> is null and <see cref="Entries"/> contains the
    /// produced read-only <see cref="PrestagedFile"/> entries.
    /// On failure <see cref="Error"/> is a non-null actionable error message and
    /// <see cref="Entries"/> is empty; no partial results are returned.
    /// </summary>
    public class StageResult
    {
        #region Public-Members

        /// <summary>
        /// Null on success; a human-readable actionable error message on any guard
        /// violation, missing source, or zero-match glob.
        /// </summary>
        public string? Error { get; set; }

        /// <summary>
        /// Produced read-only <see cref="PrestagedFile"/> entries whose
        /// <see cref="PrestagedFile.DestPath"/> values are relative paths under <c>_refs/</c>.
        /// Always non-null; empty when <see cref="Error"/> is non-null or when the request
        /// list was null or empty.
        /// </summary>
        public List<PrestagedFile> Entries { get; set; } = new List<PrestagedFile>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate a default (success with empty entries) result.
        /// </summary>
        public StageResult()
        {
        }

        #endregion
    }
}
