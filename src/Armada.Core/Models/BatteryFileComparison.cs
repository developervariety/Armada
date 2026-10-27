namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Result of one byte comparison.
    /// </summary>
    public class BatteryFileComparison
    {
        #region Public-Members

        /// <summary>
        /// Path in the worktree.
        /// </summary>
        public string Path
        {
            get => _Path;
            set => _Path = value ?? String.Empty;
        }

        /// <summary>
        /// Whether the bytes match.
        /// </summary>
        public bool Matched { get; set; } = false;

        /// <summary>
        /// Git blob hash of the expected bytes.
        /// </summary>
        public string? ExpectedHash { get; set; } = null;

        /// <summary>
        /// Git blob hash of the worktree file, or null when it is missing.
        /// </summary>
        public string? ActualHash { get; set; } = null;

        /// <summary>
        /// Why it does not match (for example the byte order mark was dropped).
        /// </summary>
        public string? Reason { get; set; } = null;

        #endregion

        #region Private-Members

        private string _Path = String.Empty;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryFileComparison()
        {
        }

        #endregion
    }
}
