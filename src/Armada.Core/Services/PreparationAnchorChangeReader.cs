namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Reads which repository paths changed when a preparation anchor moves from one commit to another, and
    /// decides whether a preparation claim cites any of them. The dispatch preview and the objective write path
    /// both ask this one rule, so a move that the preview reports as harmless never invalidates a claim on
    /// the write, and the reverse.
    /// </summary>
    public sealed class PreparationAnchorChangeReader
    {
        #region Public-Members

        /// <summary>Most changed paths read for one anchor move; a larger move cannot be shown harmless.</summary>
        public const int MaxChangedPaths = 2000;

        /// <summary>Most bytes of comparison output read for one anchor move.</summary>
        public const int OutputLimitBytes = 4 * 1024 * 1024;

        /// <summary>Deadline for each git process of one anchor-move read.</summary>
        public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

        #endregion

        #region Private-Members

        private readonly IGitService _Git;
        private readonly ArmadaSettings _Settings;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="git">Git seam that reads the vessel repository.</param>
        /// <param name="settings">Settings that locate a vessel repository without a local path.</param>
        public PreparationAnchorChangeReader(IGitService git, ArmadaSettings settings)
        {
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Repository that holds a vessel's commits: its local path, else its bare repository under the
        /// repositories directory.
        /// </summary>
        /// <param name="vessel">Vessel.</param>
        /// <param name="settings">Settings.</param>
        /// <returns>Repository path.</returns>
        public static string RepositoryPath(Vessel vessel, ArmadaSettings settings)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return vessel.LocalPath ?? Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
        }

        /// <summary>
        /// Every path that changed between two full commits of the vessel repository, a rename naming both its
        /// old and new path. Null when the change cannot be shown complete: a value that is not a full commit,
        /// a read that failed, or a move larger than <see cref="MaxChangedPaths"/>. Null never means "nothing
        /// changed".
        /// </summary>
        /// <param name="vessel">Vessel whose repository holds both commits.</param>
        /// <param name="fromCommit">Commit the anchor recorded before.</param>
        /// <param name="toCommit">Commit the anchor records now.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The changed paths, or null when they cannot be read in full.</returns>
        public async Task<IReadOnlyList<string>?> ReadChangedPathsAsync(Vessel vessel, string? fromCommit, string? toCommit, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (!IsFullCommit(fromCommit) || !IsFullCommit(toCommit)) return null;
            if (String.Equals(fromCommit!.Trim(), toCommit!.Trim(), StringComparison.OrdinalIgnoreCase))
                return Array.Empty<string>();

            ChangedPathStatusRead read = await _Git.ReadChangedPathStatusesBetweenCommitsAsync(
                RepositoryPath(vessel, _Settings), fromCommit!.Trim(), toCommit!.Trim(), MaxChangedPaths, ReadTimeout, OutputLimitBytes, token).ConfigureAwait(false);
            if (!read.Available || !read.Complete) return null;
            return ChangedPaths(read);
        }

        /// <summary>
        /// Resolve a revision (branch, tag, or commit) to its full 40-character commit SHA in the vessel repository,
        /// or null when the revision cannot be resolved.
        /// </summary>
        /// <param name="vessel">Vessel whose repository holds the revision.</param>
        /// <param name="revision">Revision to resolve.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The resolved 40-character commit SHA, or null.</returns>
        public async Task<string?> ResolveCommitAsync(Vessel vessel, string? revision, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (String.IsNullOrWhiteSpace(revision)) return null;

            string? sha = await _Git.GetRevisionCommitShaAsync(RepositoryPath(vessel, _Settings), revision!.Trim(), token).ConfigureAwait(false);
            return IsFullCommit(sha) ? sha!.Trim() : null;
        }

        /// <summary>
        /// Whether <paramref name="ancestorCommit"/> is an ancestor of <paramref name="descendantCommit"/> in the
        /// vessel repository, or null when ancestry cannot be determined.
        /// </summary>
        /// <param name="vessel">Vessel whose repository holds both commits.</param>
        /// <param name="ancestorCommit">Candidate ancestor commit.</param>
        /// <param name="descendantCommit">Candidate descendant commit.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when ancestor, false when not, null when unavailable.</returns>
        public async Task<bool?> IsAncestorAsync(Vessel vessel, string? ancestorCommit, string? descendantCommit, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (!IsFullCommit(ancestorCommit) || !IsFullCommit(descendantCommit)) return null;

            return await _Git.TryIsAncestorAsync(
                RepositoryPath(vessel, _Settings),
                ancestorCommit!.Trim(),
                descendantCommit!.Trim(),
                token).ConfigureAwait(false);
        }

        /// <summary>
        /// Return the effective start ref for an objective dispatch when a prepared target branch moved cleanly
        /// without touching any path the objective depends on. An explicit start ref that differs from the prepared
        /// target anchor (such as a <c>recover/</c> branch) is preserved unchanged.
        /// </summary>
        /// <param name="objectiveStartFromRef">Stored objective start ref, if any.</param>
        /// <param name="anchorMoves">Verified preparation anchor moves for the dispatch.</param>
        /// <returns>The effective start ref to use for dispatch.</returns>
        public static string? ResolveEffectiveStartFromRef(
            string? objectiveStartFromRef,
            IReadOnlyList<ObjectivePreparationAnchorMove>? anchorMoves)
        {
            ObjectivePreparationAnchorMove? targetMove = anchorMoves?
                .FirstOrDefault(move => move != null
                    && String.Equals(move.Anchor, "target", StringComparison.Ordinal)
                    && !String.IsNullOrWhiteSpace(move.CurrentCommit));
            if (targetMove == null) return objectiveStartFromRef;

            string currentCommit = targetMove.CurrentCommit.Trim();
            if (String.IsNullOrWhiteSpace(objectiveStartFromRef))
                return currentCommit;

            string trimmedStart = objectiveStartFromRef!.Trim();
            if ((!String.IsNullOrWhiteSpace(targetMove.RecordedCommit)
                    && String.Equals(trimmedStart, targetMove.RecordedCommit.Trim(), StringComparison.OrdinalIgnoreCase))
                || (!String.IsNullOrWhiteSpace(targetMove.Ref)
                    && String.Equals(trimmedStart, targetMove.Ref.Trim(), StringComparison.Ordinal)))
            {
                return currentCommit;
            }

            return objectiveStartFromRef;
        }

        /// <summary>
        /// Every path a change read names, a rename or copy naming both its old and new path.
        /// </summary>
        /// <param name="read">An available change read.</param>
        /// <returns>Distinct changed paths.</returns>
        public static List<string> ChangedPaths(ChangedPathStatusRead read)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            return read.Entries
                .SelectMany(entry => new[] { entry.Path, entry.PreviousPath })
                .Where(path => !String.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Repository paths a preparation claim cites in its text or evidence. Sibling paths are not in the
        /// repository and are left out.
        /// </summary>
        /// <param name="claim">Claim.</param>
        /// <param name="bounded">False when one text names more paths than are read, so the list may be partial.</param>
        /// <returns>Normalized cited paths; empty when the claim cites none.</returns>
        public static List<string> ClaimPaths(ObjectivePreparationClaim claim, out bool bounded)
        {
            bounded = true;
            List<string> paths = new List<string>();
            if (claim == null) return paths;
            List<string?> texts = new List<string?> { claim.Text };
            texts.AddRange(claim.EvidenceLinks ?? new List<string>());
            foreach (string? text in texts)
            {
                List<string> named = ContextPackManifest.ExtractAssignedPaths(text);
                if (named.Count >= ContextPackManifest.MaxAssignedPaths) bounded = false;
                paths.AddRange(named);
            }
            return NormalizePaths(paths);
        }

        /// <summary>
        /// Normalize repository paths for comparison with changed paths: forward slashes, no leading slash, no
        /// sibling (<c>../</c>) path, de-duplicated.
        /// </summary>
        /// <param name="paths">Paths.</param>
        /// <returns>Normalized paths.</returns>
        public static List<string> NormalizePaths(IEnumerable<string> paths)
        {
            return (paths ?? Enumerable.Empty<string>())
                .Where(path => path != null)
                .Select(path => path.Replace('\\', '/').Trim().TrimStart('/'))
                .Where(path => path.Length > 0 && !path.StartsWith("../", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Whether a cited path covers a changed path: it names the same file, names it by a trailing suffix
        /// (a bare file name or a partial path), or names a directory that holds it.
        /// </summary>
        /// <param name="dependent">Cited path.</param>
        /// <param name="changed">Changed path.</param>
        /// <returns>True when the change touches the cited path.</returns>
        public static bool PathCovers(string dependent, string changed)
        {
            if (String.IsNullOrWhiteSpace(dependent) || String.IsNullOrWhiteSpace(changed)) return false;
            string path = changed.Replace('\\', '/').TrimStart('/');
            string entry = dependent.TrimEnd('/');
            if (entry.Length == 0) return false;
            if (String.Equals(path, entry, StringComparison.OrdinalIgnoreCase)) return true;
            if (path.EndsWith("/" + entry, StringComparison.OrdinalIgnoreCase)) return true;
            return path.StartsWith(entry + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether a claim still holds after an anchor move that changed exactly <paramref name="changedPaths"/>:
        /// it cites at least one repository path, the citation list is complete, and no changed path touches a
        /// cited one. A claim that cites no path is never shown to hold, because nothing names what it rests on.
        /// </summary>
        /// <param name="claim">Claim.</param>
        /// <param name="changedPaths">Every path the move changed.</param>
        /// <returns>True when the move cannot have touched what the claim cites.</returns>
        public static bool ClaimUnaffected(ObjectivePreparationClaim claim, IReadOnlyCollection<string> changedPaths)
        {
            if (claim == null || changedPaths == null) return false;
            List<string> cited = ClaimPaths(claim, out bool bounded);
            if (!bounded || cited.Count == 0) return false;
            return !changedPaths.Any(changed => cited.Any(path => PathCovers(path, changed)));
        }

        /// <summary>
        /// Whether a value is a full 40-character commit id.
        /// </summary>
        /// <param name="value">Value.</param>
        /// <returns>True for a full commit id.</returns>
        public static bool IsFullCommit(string? value)
        {
            if (value == null) return false;
            string trimmed = value.Trim();
            return trimmed.Length == 40 && trimmed.All(Uri.IsHexDigit);
        }

        #endregion
    }
}
