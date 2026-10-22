namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The git <c>info/exclude</c> file a checkout reads, and the entries Armada adds to it for files it writes
    /// into a dock. A linked worktree reads only the exclude file of the repository it shares objects with:
    /// git ignores <c>info/exclude</c> under the worktree's own git directory, so an entry written there hides
    /// nothing.
    /// </summary>
    public static class GitInfoExclude
    {
        #region Public-Members

        /// <summary>
        /// Dock paths Armada writes at provisioning and while it builds the brief. None is repository content,
        /// so each is excluded before a captain or a repository tool lists untracked files.
        /// </summary>
        public static readonly string[] ArmadaStagedDockPaths = new[]
        {
            "_briefing/",
            ".armada/instructions/",
            ".armada/playbooks/"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// The exclude file git reads for a checkout: <c>.git/info/exclude</c> for a plain clone, and the shared
        /// repository's <c>info/exclude</c> for a linked worktree, whose <c>.git</c> file names a git directory
        /// with a <c>commondir</c> pointer.
        /// </summary>
        /// <param name="worktreePath">Checkout path.</param>
        /// <returns>The exclude file path, or null when the path is not a git checkout.</returns>
        public static string? ResolvePath(string worktreePath)
        {
            if (String.IsNullOrEmpty(worktreePath)) return null;

            string dotGitPath = Path.Combine(worktreePath, ".git");
            if (Directory.Exists(dotGitPath)) return Path.Combine(dotGitPath, "info", "exclude");
            if (!File.Exists(dotGitPath)) return null;

            string pointer = File.ReadAllText(dotGitPath).Trim();
            const string prefix = "gitdir:";
            if (!pointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

            string gitDir = pointer.Substring(prefix.Length).Trim();
            if (!Path.IsPathRooted(gitDir)) gitDir = Path.GetFullPath(Path.Combine(worktreePath, gitDir));

            string commonDirFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonDirFile))
            {
                string commonDir = File.ReadAllText(commonDirFile).Trim();
                if (!String.IsNullOrEmpty(commonDir))
                {
                    if (!Path.IsPathRooted(commonDir)) commonDir = Path.GetFullPath(Path.Combine(gitDir, commonDir));
                    return Path.Combine(commonDir, "info", "exclude");
                }
            }

            return Path.Combine(gitDir, "info", "exclude");
        }

        /// <summary>
        /// Add an entry to an exclude file unless a line already holds it.
        /// </summary>
        /// <param name="excludePath">Exclude file from <see cref="ResolvePath(string)"/>.</param>
        /// <param name="entry">Pattern to add; backslashes are written as forward slashes.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task EnsureEntryAsync(string excludePath, string entry, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(excludePath) || String.IsNullOrWhiteSpace(entry)) return;

            string? dir = Path.GetDirectoryName(excludePath);
            if (!String.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            string existing = File.Exists(excludePath)
                ? await File.ReadAllTextAsync(excludePath, token).ConfigureAwait(false)
                : "";

            string normalizedEntry = entry.Trim().Replace("\\", "/");
            bool alreadyPresent = existing
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(line => String.Equals(line.Trim(), normalizedEntry, StringComparison.Ordinal));
            if (alreadyPresent) return;

            string separator = existing.Length > 0 && !existing.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "";
            await File.AppendAllTextAsync(excludePath, separator + normalizedEntry + "\n", token).ConfigureAwait(false);
        }

        #endregion
    }
}
