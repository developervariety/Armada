namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Describes a single read-context reference to be staged into the <c>_refs/</c>
    /// sub-tree of a dock worktree before the captain process is spawned.
    /// Each request expands to one or more read-only <see cref="PrestagedFile"/> entries.
    /// </summary>
    public class ReadContextRequest
    {
        #region Public-Members

        /// <summary>
        /// Host-side source: an absolute file path or a glob pattern
        /// (e.g. <c>/home/user/src/**/*.cs</c>). Required; null coerces to empty string.
        /// </summary>
        public string SourceGlob
        {
            get => _SourceGlob;
            set => _SourceGlob = value ?? "";
        }

        /// <summary>
        /// Optional sub-prefix appended under <c>_refs/</c> in the dock worktree.
        /// When null or empty the matched file's path relative to the host root is used
        /// directly; when set the dest path becomes <c>_refs/{DestSubPath}/{relativePath}</c>.
        /// Must not be absolute and must not contain <c>..</c>.
        /// </summary>
        public string? DestSubPath { get; set; }

        #endregion

        #region Private-Members

        private string _SourceGlob = "";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with default values.
        /// </summary>
        public ReadContextRequest()
        {
        }

        /// <summary>
        /// Instantiate with a source glob.
        /// </summary>
        /// <param name="sourceGlob">Host-side path or glob pattern.</param>
        public ReadContextRequest(string sourceGlob)
        {
            SourceGlob = sourceGlob ?? "";
        }

        /// <summary>
        /// Instantiate with a source glob and a destination sub-path prefix.
        /// </summary>
        /// <param name="sourceGlob">Host-side path or glob pattern.</param>
        /// <param name="destSubPath">Optional sub-prefix under <c>_refs/</c>.</param>
        public ReadContextRequest(string sourceGlob, string? destSubPath)
        {
            SourceGlob = sourceGlob ?? "";
            DestSubPath = destSubPath;
        }

        #endregion
    }
}
