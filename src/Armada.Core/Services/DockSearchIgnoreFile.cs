namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The <c>.rgignore</c> file Armada writes at a dock root so that search tools which honour ignore files list
    /// the folders Armada stages for the captain. Git excludes those folders (see <see cref="GitInfoExclude"/>),
    /// and ripgrep-based glob and grep tools read the git exclude file too, so without this file a captain's file
    /// search reports the staged briefing files as absent. Ripgrep gives <c>.rgignore</c> precedence over git's
    /// ignore rules, so a negation here lists the folders again while git still never sees them.
    /// </summary>
    public static class DockSearchIgnoreFile
    {
        #region Public-Members

        /// <summary>Dock-relative name of the file.</summary>
        public const string FileName = ".rgignore";

        #endregion

        #region Public-Methods

        /// <summary>
        /// The file text: one negation per Armada-staged dock folder.
        /// </summary>
        /// <returns>File content.</returns>
        public static string BuildContent()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("# Written by Armada. Git excludes the folders Armada stages for the captain; these lines let\n");
            sb.Append("# search tools that honour ignore files list them. This file is excluded from git too.\n");
            foreach (string stagedPath in GitInfoExclude.ArmadaStagedDockPaths)
                sb.Append("!/").Append(stagedPath.TrimStart('/')).Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// Write the file at a dock root and exclude it from git. A file the checkout already holds belongs to the
        /// repository and is left unchanged, so the dock never carries an edit to a tracked file.
        /// </summary>
        /// <param name="worktreePath">Dock worktree root.</param>
        /// <param name="excludePath">Git exclude file from <see cref="GitInfoExclude.ResolvePath(string)"/>.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when Armada wrote the file; false when the checkout already held one.</returns>
        public static async Task<bool> EnsureAsync(string worktreePath, string excludePath, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(worktreePath)) throw new ArgumentNullException(nameof(worktreePath));
            if (String.IsNullOrWhiteSpace(excludePath)) throw new ArgumentNullException(nameof(excludePath));

            string path = Path.Combine(worktreePath, FileName);
            if (File.Exists(path)) return false;

            await GitInfoExclude.EnsureEntryAsync(excludePath, "/" + FileName, token).ConfigureAwait(false);
            await File.WriteAllTextAsync(path, BuildContent(), token).ConfigureAwait(false);
            return true;
        }

        #endregion
    }
}
