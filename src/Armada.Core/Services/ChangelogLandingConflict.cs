namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Finishes a stopped landing merge only when every unresolved path is CHANGELOG.md or Markdown
    /// under docs/, and neither side removes or replaces any base line. Target additions precede mission additions.
    /// </summary>
    public static class ChangelogLandingConflict
    {
        #region Public-Members

        /// <summary>
        /// The root release-note path accepted beside Markdown under docs/.
        /// </summary>
        public const string ChangelogPath = "CHANGELOG.md";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Resolve add-only Markdown conflicts and complete the merge. Refuse the whole set before changing
        /// a worktree file if any path, stage, file mode or deletion is unsupported.
        /// </summary>
        /// <param name="worktree">Worktree with a merge in progress.</param>
        /// <param name="git">Runs git and returns its exit code and standard output.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="warn">Receives temporary-file cleanup failures.</param>
        /// <param name="resolved">Receives each resolved path after the merge commit succeeds.</param>
        /// <param name="legacyChangelogOnly">Retain the sole-CHANGELOG union behavior of legacy sync calls.</param>
        /// <returns>True only when the merge completes.</returns>
        public static async Task<bool> TryResolveAsync(string worktree, Func<string[], CancellationToken, Task<ChangelogGitOutput>> git, CancellationToken token = default, Action<string>? warn = null, Action<string>? resolved = null, bool legacyChangelogOnly = false)
        {
            if (String.IsNullOrEmpty(worktree)) throw new ArgumentNullException(nameof(worktree));
            if (git == null) throw new ArgumentNullException(nameof(git));

            ChangelogGitOutput unresolved = await git(new[] { "diff", "--name-only", "-z", "--diff-filter=U" }, token).ConfigureAwait(false);
            if (unresolved.ExitCode != 0) return false;
            IReadOnlyList<string> paths = GitDiffPaths.SplitNulSeparated(unresolved.StandardOutput);
            if (paths.Count == 0) return false;
            if (legacyChangelogOnly && (paths.Count != 1 || paths[0] != ChangelogPath)) return false;
            foreach (string path in paths)
            {
                if (legacyChangelogOnly) continue;
                if (!IsMarkdownPath(path)) return false;
                ChangelogGitOutput modes = await git(new[] { "ls-files", "--stage", "-z", "--", path }, token).ConfigureAwait(false);
                string[] stages = modes.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (modes.ExitCode != 0 || stages.Length != 3) return false;
                foreach (string stage in stages)
                    if (!stage.StartsWith("100644 ", StringComparison.Ordinal)) return false;
                foreach (string side in new[] { ":2:", ":3:" })
                {
                    ChangelogGitOutput diff = await git(new[] { "diff", "--no-ext-diff", "--no-textconv", "--numstat", ":1:" + path, side + path, "--" }, token).ConfigureAwait(false);
                    string[] fields = diff.StandardOutput.TrimEnd('\r', '\n').Split('\t');
                    if (diff.ExitCode != 0 || fields.Length < 2
                        || !Int32.TryParse(fields[0], out int added) || added < 0 || fields[1] != "0") return false;
                }
            }

            List<string> temporaryFiles = new List<string>();
            List<string> mergedFiles = new List<string>();
            try
            {
                foreach (string path in paths)
                {
                    ChangelogGitOutput stages = await git(new[] { "checkout-index", "--stage=all", "--", path }, token).ConfigureAwait(false);
                    string[] tab = stages.StandardOutput.Trim().Split('\t');
                    string[] names = tab[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    foreach (string name in names) temporaryFiles.Add(Path.Combine(worktree, name));
                    if (stages.ExitCode != 0 || tab.Length != 2 || names.Length != 3) return false;
                    if ((await git(new[] { "merge-file", "--union", "--", names[1], names[0], names[2] }, token).ConfigureAwait(false)).ExitCode != 0) return false;
                    mergedFiles.Add(Path.Combine(worktree, names[1]));
                }
                for (int i = 0; i < paths.Count; i++)
                {
                    File.Copy(mergedFiles[i], Path.Combine(worktree, paths[i]), true);
                    if ((await git(new[] { "add", "--", paths[i] }, token).ConfigureAwait(false)).ExitCode != 0) return false;
                }
                if ((await git(new[] { "-c", "core.editor=true", "commit", "--no-edit" }, token).ConfigureAwait(false)).ExitCode != 0) return false;
                foreach (string path in paths) resolved?.Invoke(path);
                return true;
            }
            finally
            {
                foreach (string file in temporaryFiles)
                {
                    try { if (File.Exists(file)) File.Delete(file); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        warn?.Invoke("could not remove temporary stage file " + file + ": " + ex.Message);
                    }
                }
            }
        }

        private static bool IsMarkdownPath(string path)
        {
            if (String.Equals(path, ChangelogPath, StringComparison.Ordinal)) return true;
            if (!path.StartsWith("docs/", StringComparison.Ordinal) || !path.EndsWith(".md", StringComparison.Ordinal)) return false;
            foreach (string segment in path.Split('/'))
                if (segment.Length == 0 || segment == "." || segment == "..") return false;
            return true;
        }

        #endregion
    }
}
