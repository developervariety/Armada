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
    ///
    /// When Content is non-null the entry is content-based: the Admiral writes
    /// Content directly to DestPath (UTF-8 no BOM, LF line endings) without
    /// reading SourcePath. SourcePath may be empty or null for content-based entries.
    /// </remarks>
    public class PrestagedFile
    {
        #region Public-Members

        /// <summary>
        /// Absolute path on the Admiral host to the source file.
        /// Empty or null when the entry is content-based (Content is non-null).
        /// </summary>
        public string SourcePath { get; set; } = "";

        /// <summary>
        /// Path within the dock worktree at which the source file should be copied.
        /// Must be a relative path (never absolute) and must not contain "..".
        /// </summary>
        public string DestPath { get; set; } = "";

        /// <summary>
        /// When non-null, the file is materialized by writing this text to
        /// DestPath (UTF-8 no BOM, LF line endings) instead of copying SourcePath.
        /// Null for normal path-based entries.
        /// </summary>
        public string? Content { get; set; }

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

        /// <summary>
        /// Create a content-based entry that writes <paramref name="content"/> to
        /// <paramref name="destPath"/> without requiring a source file on disk.
        /// </summary>
        /// <param name="destPath">Relative path within the dock worktree.</param>
        /// <param name="content">Text to write. Line endings are normalized to LF on write.</param>
        /// <returns>A new <see cref="PrestagedFile"/> with Content set and SourcePath empty.</returns>
        public static PrestagedFile FromContent(string destPath, string content)
        {
            return new PrestagedFile
            {
                DestPath = destPath ?? "",
                Content = content ?? ""
            };
        }

        #endregion
    }
}
