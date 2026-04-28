namespace Armada.Core.Models
{
    /// <summary>
    /// Describes a single file-copy operation performed by the Admiral after a dock
    /// worktree has been created and before the captain process is spawned.
    /// </summary>
    /// <remarks>
    /// SourcePath is an absolute path on the Admiral host. DestPath is a relative
    /// path within the dock worktree. The Admiral copies SourcePath to
    /// {dock.WorktreePath}/{DestPath}, creating intermediate directories as needed.
    /// Suitable for the local single-host deployment topology where the orchestrator
    /// has direct filesystem access to the source files.
    /// </remarks>
    public class PrestagedFile
    {
        #region Public-Members

        /// <summary>
        /// Absolute path on the Admiral host to the source file.
        /// </summary>
        public string SourcePath { get; set; } = "";

        /// <summary>
        /// Path within the dock worktree at which the source file should be copied.
        /// Must be a relative path (never absolute) and must not contain "..".
        /// </summary>
        public string DestPath { get; set; } = "";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public PrestagedFile()
        {
        }

        /// <summary>
        /// Instantiate with source and destination paths.
        /// </summary>
        /// <param name="sourcePath">Absolute path on the Admiral host.</param>
        /// <param name="destPath">Relative path within the dock worktree.</param>
        public PrestagedFile(string sourcePath, string destPath)
        {
            SourcePath = sourcePath ?? "";
            DestPath = destPath ?? "";
        }

        #endregion
    }
}
