namespace Armada.Core.Models
{
    /// <summary>
    /// A unified git diff of a vessel's working tree against HEAD, optionally scoped to one path.
    /// Used by the in-app review/diff viewer.
    /// </summary>
    public class WorkspaceDiffResult
    {
        /// <summary>
        /// The path the diff was scoped to, or null for the whole working tree.
        /// </summary>
        public string? Path { get; set; } = null;

        /// <summary>
        /// The unified diff text (empty when there are no tracked changes).
        /// </summary>
        public string Diff { get; set; } = string.Empty;

        /// <summary>
        /// An error message when the diff could not be produced (e.g. not a git repository).
        /// </summary>
        public string? Error { get; set; } = null;
    }
}
