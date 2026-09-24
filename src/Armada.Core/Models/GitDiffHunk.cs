namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// One hunk of a unified Git diff: its header and body lines with their line numbers.
    /// </summary>
    public sealed class GitDiffHunk
    {
        #region Public-Members

        /// <summary>
        /// The full <c>@@ -a,b +c,d @@ ...</c> header line.
        /// </summary>
        public string Header { get; set; } = String.Empty;

        /// <summary>
        /// First old-side line number the hunk covers.
        /// </summary>
        public int OldStart { get; set; } = 0;

        /// <summary>
        /// First new-side line number the hunk covers.
        /// </summary>
        public int NewStart { get; set; } = 0;

        /// <summary>
        /// Body lines in order. <c>\ No newline at end of file</c> markers are not lines.
        /// </summary>
        public List<GitDiffLine> Lines { get; } = new List<GitDiffLine>();

        #endregion
    }

    /// <summary>
    /// One body line of a unified-diff hunk, without its leading <c>+</c>, <c>-</c> or space.
    /// </summary>
    public sealed class GitDiffLine
    {
        #region Public-Members

        /// <summary>
        /// Whether the line was added, removed, or is unchanged context.
        /// </summary>
        public GitDiffLineKindEnum Kind { get; set; } = GitDiffLineKindEnum.Context;

        /// <summary>
        /// Line content without the diff marker column.
        /// </summary>
        public string Text { get; set; } = String.Empty;

        /// <summary>
        /// Old-side line number, or null for an added line.
        /// </summary>
        public int? OldNumber { get; set; } = null;

        /// <summary>
        /// New-side line number, or null for a removed line.
        /// </summary>
        public int? NewNumber { get; set; } = null;

        #endregion
    }

    /// <summary>
    /// One added line of a diff with the file it belongs to.
    /// </summary>
    public sealed class GitDiffAddedLine
    {
        #region Public-Members

        /// <summary>
        /// The file's one name (<see cref="GitDiffFileChange.DisplayPath"/>), or null when the text
        /// carried no file header.
        /// </summary>
        public string? Path { get; set; } = null;

        /// <summary>
        /// Line content without the leading <c>+</c>.
        /// </summary>
        public string Text { get; set; } = String.Empty;

        /// <summary>
        /// New-side line number, or null when the text carried no hunk header.
        /// </summary>
        public int? NewNumber { get; set; } = null;

        #endregion
    }
}
