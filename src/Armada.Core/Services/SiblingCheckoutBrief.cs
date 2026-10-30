namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Reads every declared sibling checkout beside a dock once, after provisioning, and renders what it holds
    /// in the brief: the provisioned HEAD, uncommitted changes, absent required paths, and the HEAD against any
    /// pinned commit. A sibling that failed to provision is named as absent, so a captain never reads its
    /// missing files as missing source. The provisioned HEAD is the tree a captain reads and it controls.
    /// </summary>
    public static class SiblingCheckoutBrief
    {
        #region Private-Members

        private const int _MaxListedPaths = 10;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read the state of every sibling checkout the vessel declares.
        /// </summary>
        /// <param name="git">Git seam used to read the checkouts.</param>
        /// <param name="vessel">Vessel whose sibling declarations are read.</param>
        /// <param name="worktreePath">Dock worktree the sibling paths are relative to.</param>
        /// <param name="objective">Linked objective whose sibling inputs name required paths and pins; may be null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>One state per declared sibling, in declaration order.</returns>
        public static async Task<List<SiblingCheckoutState>> ReadAsync(
            IGitService git,
            Vessel vessel,
            string worktreePath,
            Objective? objective,
            CancellationToken token = default)
        {
            if (git == null) throw new ArgumentNullException(nameof(git));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            List<SiblingCheckoutState> states = new List<SiblingCheckoutState>();
            if (String.IsNullOrWhiteSpace(worktreePath)) return states;

            foreach (SiblingRepo sibling in vessel.GetSiblingRepos())
            {
                if (sibling == null || String.IsNullOrWhiteSpace(sibling.RelativePath)) continue;
                SiblingCheckoutState state = new SiblingCheckoutState
                {
                    RelativePath = NormalizeRelativePath(sibling.RelativePath),
                    FullPath = Path.GetFullPath(Path.Combine(worktreePath, sibling.RelativePath))
                };
                ObjectivePreparationSiblingInput? required = FindRequiredInput(objective, sibling);
                List<string> requiredPaths = required?.RequiredArtifactPaths ?? new List<string>();
                state.PinnedCommit = String.IsNullOrWhiteSpace(required?.PinnedCommit) ? null : required!.PinnedCommit!.Trim();
                state.PinProvisioned = state.PinnedCommit != null && required!.ProvisionPinnedCommit;
                state.Present = Directory.Exists(state.FullPath) && Directory.EnumerateFileSystemEntries(state.FullPath).Any();
                if (!state.Present)
                {
                    state.MissingPaths.AddRange(requiredPaths);
                    states.Add(state);
                    continue;
                }

                state.Head = await ReadOrNullAsync(() => git.GetHeadCommitHashAsync(state.FullPath, token)).ConfigureAwait(false);
                state.Clean = await ReadCleanAsync(git, state.FullPath, token).ConfigureAwait(false);
                foreach (string requiredPath in requiredPaths)
                {
                    string candidate = Path.Combine(state.FullPath, requiredPath);
                    if (!File.Exists(candidate) && !Directory.Exists(candidate)) state.MissingPaths.Add(requiredPath);
                }

                if (state.PinnedCommit != null && !String.IsNullOrWhiteSpace(state.Head))
                {
                    state.PinIsAncestor = await git.TryIsAncestorAsync(state.FullPath, state.PinnedCommit, state.Head!, token).ConfigureAwait(false);
                    if (state.PinIsAncestor == true)
                    {
                        ChangedPathsRead read = await git.ReadChangedPathsBetweenCommitsAsync(state.FullPath, state.PinnedCommit, state.Head!, token).ConfigureAwait(false);
                        if (read.Available)
                        {
                            state.ChangedSincePin = read.Paths
                                .Where(path => requiredPaths.Count == 0 || requiredPaths.Any(assigned => IsUnder(path, assigned)))
                                .ToList();
                        }
                    }
                }

                states.Add(state);
            }

            return states;
        }

        /// <summary>
        /// Render the sibling checkout section, or an empty string when no sibling is declared.
        /// </summary>
        /// <param name="states">Sibling states read for this dock.</param>
        /// <param name="workspaceRootsRegistered">True when the runtime receives the sibling checkouts as workspace roots.</param>
        /// <returns>The section text.</returns>
        public static string Render(IReadOnlyList<SiblingCheckoutState> states, bool workspaceRootsRegistered)
        {
            if (states == null || states.Count == 0) return String.Empty;
            StringBuilder sb = new StringBuilder();
            sb.Append("## Sibling Checkouts\n\n");
            sb.Append("Declared siblings sit beside this checkout. The provisioned HEAD below is the tree you read, and it controls over any other commit the brief names.\n\n");
            foreach (SiblingCheckoutState state in states)
            {
                if (!state.Present)
                {
                    sb.Append("- `").Append(state.RelativePath).Append("` is absent: provisioning did not produce it. Report that once; never report its files as missing source.\n");
                    continue;
                }

                sb.Append("- `").Append(state.RelativePath).Append("` HEAD `").Append(Short(state.Head)).Append("`");
                if (state.Clean == false) sb.Append(", with uncommitted changes: compare against HEAD, not working-tree status");
                sb.Append(".\n");
                if (state.PinnedCommit != null && state.PinProvisioned)
                {
                    sb.Append("  - Sibling pin override: the objective provisions this checkout at pinned commit `")
                        .Append(Short(state.PinnedCommit)).Append("`, which may not have landed. ");
                    sb.Append(state.Head != null && state.Head.StartsWith(state.PinnedCommit, StringComparison.OrdinalIgnoreCase)
                        ? "HEAD is that commit; build and measure against it. The consumer lands only after the sibling lands it.\n"
                        : "HEAD is not that commit; HEAD controls. Report the mismatch.\n");
                }
                else if (state.PinnedCommit != null)
                {
                    sb.Append("  - Pinned `").Append(Short(state.PinnedCommit)).Append("`: ");
                    if (state.PinIsAncestor == true)
                    {
                        sb.Append("an ancestor of HEAD");
                        if (state.ChangedSincePin != null)
                            sb.Append(state.ChangedSincePin.Count == 0 ? "; no assigned path changed since." : "; changed since: " + ListPaths(state.ChangedSincePin) + ".");
                        else
                            sb.Append(".");
                    }
                    else if (state.PinIsAncestor == false)
                    {
                        sb.Append("not an ancestor of HEAD; HEAD controls.");
                    }
                    else
                    {
                        sb.Append("ancestry unknown; HEAD controls.");
                    }
                    sb.Append("\n");
                }
                if (state.MissingPaths.Count > 0)
                    sb.Append("  - Required paths absent: ").Append(ListPaths(state.MissingPaths)).Append(".\n");
            }

            sb.Append(workspaceRootsRegistered
                ? "\nBuilt-in search reaches these checkouts by the paths above.\n"
                : "\nBuilt-in search stays inside this checkout; search a sibling with `grep -rn <pattern> <sibling path>`.\n");
            return sb.ToString();
        }

        #endregion

        #region Private-Methods

        internal static ObjectivePreparationSiblingInput? FindRequiredInput(Objective? objective, SiblingRepo sibling)
        {
            string declared = NormalizeRelativePath(sibling.RelativePath);
            foreach (ObjectivePreparationSiblingInput input in objective?.Preparation?.RequiredSiblingInputs ?? new List<ObjectivePreparationSiblingInput>())
            {
                if (input != null && String.Equals(NormalizeRelativePath(input.RelativePath), declared, StringComparison.OrdinalIgnoreCase))
                    return input;
            }
            return null;
        }

        private static async Task<string?> ReadOrNullAsync(Func<Task<string?>> read)
        {
            try
            {
                return await read().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // An unreadable HEAD renders as unknown in the brief; it never stops the brief.
                return null;
            }
        }

        private static async Task<bool?> ReadCleanAsync(IGitService git, string path, CancellationToken token)
        {
            try
            {
                return await git.IsWorkingDirectoryCleanAsync(path, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Unknown status is rendered as nothing rather than as clean.
                return null;
            }
        }

        private static bool IsUnder(string path, string assigned)
        {
            string normalizedPath = NormalizeRelativePath(path);
            string normalizedAssigned = NormalizeRelativePath(assigned);
            return String.Equals(normalizedPath, normalizedAssigned, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(normalizedAssigned + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeRelativePath(string? value)
        {
            return (value ?? String.Empty).Trim().Replace('\\', '/').TrimEnd('/');
        }

        private static string Short(string? commit)
        {
            if (String.IsNullOrWhiteSpace(commit)) return "unknown";
            string trimmed = commit.Trim();
            return trimmed.Length > 12 ? trimmed.Substring(0, 12) : trimmed;
        }

        private static string ListPaths(IReadOnlyList<string> paths)
        {
            string listed = String.Join(", ", paths.Take(_MaxListedPaths).Select(path => "`" + path + "`"));
            return paths.Count > _MaxListedPaths ? listed + " and " + (paths.Count - _MaxListedPaths) + " more" : listed;
        }

        #endregion
    }
}
