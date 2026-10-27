namespace Armada.Core.Models
{

    /// <summary>
    /// A file a byte comparison checks.
    /// </summary>
    public class BatteryExpectedFile
    {
        #region Public-Members

        /// <summary>
        /// Path in the worktree.
        /// </summary>
        public string? Path { get; set; } = null;

        /// <summary>
        /// Expected file, relative to the pack directory.
        /// </summary>
        public string? Expected { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryExpectedFile()
        {
        }

        #endregion
    }
}
