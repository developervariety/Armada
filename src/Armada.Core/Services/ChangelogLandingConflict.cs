namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Finishes a stopped landing merge when <c>CHANGELOG.md</c> is the only unresolved path, keeping both sides
    /// with Git's union algorithm. Concurrent missions each add release-note lines to the same file, and that
    /// conflict carries no decision for a person. Any other unresolved path leaves the merge untouched.
    /// </summary>
    public static class ChangelogLandingConflict
    {
        #region Public-Members

        /// <summary>
        /// The only path this resolver rewrites.
        /// </summary>
        public const string ChangelogPath = "CHANGELOG.md";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Resolve a stopped merge in <paramref name="worktree"/> by union-merging <c>CHANGELOG.md</c> and completing the
        /// merge commit. Returns false, leaving the merge as it was, unless CHANGELOG.md is the sole unresolved path and
        /// the index holds all three stages (base, ours, theirs). An add/add or delete/modify conflict needs review.
        /// </summary>
        /// <param name="worktree">Worktree with a merge in progress.</param>
        /// <param name="git">Runs git in the worktree with the given arguments and returns its exit code and standard output.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="warn">Receives a message when a temporary stage file cannot be removed.</param>
        /// <returns>True when the merge was completed with both sides of CHANGELOG.md kept.</returns>
        public static async Task<bool> TryResolveAsync(string worktree, Func<string[], CancellationToken, Task<ChangelogGitOutput>> git, CancellationToken token = default, Action<string>? warn = null)
        {
            if (String.IsNullOrEmpty(worktree)) throw new ArgumentNullException(nameof(worktree));
            if (git == null) throw new ArgumentNullException(nameof(git));

            ChangelogGitOutput unresolved = await git(new[] { "diff", "--name-only", "-z", "--diff-filter=U" }, token).ConfigureAwait(false);
            if (unresolved.ExitCode != 0) return false;
            IReadOnlyList<string> paths = GitDiffPaths.SplitNulSeparated(unresolved.StandardOutput);
            if (paths.Count != 1 || !String.Equals(paths[0], ChangelogPath, StringComparison.Ordinal)) return false;

            // Writes the three stages to temporary files in the worktree and prints "<base> <ours> <theirs>\t<path>".
            // It fails when a stage is missing.
            ChangelogGitOutput stages = await git(new[] { "checkout-index", "--stage=all", "--", ChangelogPath }, token).ConfigureAwait(false);
            List<string> temporaryFiles = new List<string>();
            try
            {
                string[] tab = stages.StandardOutput.Trim().Split('\t');
                string[] names = tab[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (string name in names) temporaryFiles.Add(Path.Combine(worktree, name));
                if (stages.ExitCode != 0 || tab.Length != 2 || names.Length != 3) return false;

                string baseFile = names[0];
                string oursFile = names[1];
                string theirsFile = names[2];
                if ((await git(new[] { "merge-file", "--union", "--", oursFile, baseFile, theirsFile }, token).ConfigureAwait(false)).ExitCode != 0) return false;
                File.Copy(Path.Combine(worktree, oursFile), Path.Combine(worktree, ChangelogPath), true);
                if ((await git(new[] { "add", "--", ChangelogPath }, token).ConfigureAwait(false)).ExitCode != 0) return false;
                return (await git(new[] { "-c", "core.editor=true", "commit", "--no-edit" }, token).ConfigureAwait(false)).ExitCode == 0;
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

        #endregion
    }
}
