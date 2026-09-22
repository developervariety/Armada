namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// The one evidence reader for every landing gate (direct landing and the merge queue). It reads
    /// the vessel, the changed paths and the unified diff, and reports the evidence as unavailable
    /// when any read fails, so a gate never scans an empty change it did not verify. A cancelled
    /// read is not converted: cancellation propagates to the caller.
    /// </summary>
    public sealed class LandingEvidenceCollector
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly IGitService _Git;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver used to read the vessel.</param>
        /// <param name="git">Git service used to read the change.</param>
        public LandingEvidenceCollector(DatabaseDriver database, IGitService git)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read the evidence for a change in <paramref name="worktreePath"/> against
        /// <paramref name="targetBranch"/>. The vessel is read first, so a null target branch uses the
        /// vessel's default branch (<c>main</c> when the landing names no vessel).
        /// </summary>
        /// <param name="vesselId">Vessel whose rules apply; null or empty when the landing names none.</param>
        /// <param name="tenantId">Tenant scope for the vessel read, or null for an unscoped read.</param>
        /// <param name="worktreePath">Worktree holding the change at HEAD.</param>
        /// <param name="targetBranch">Branch the change lands on, or null for the vessel's default branch.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The evidence, or unavailable evidence naming the part that failed.</returns>
        public async Task<LandingEvidence> CollectAsync(
            string? vesselId,
            string? tenantId,
            string? worktreePath,
            string? targetBranch,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(worktreePath)) return LandingEvidence.Unavailable("no worktree path to read the change from");

            Vessel? vessel = null;
            if (!String.IsNullOrEmpty(vesselId))
            {
                try
                {
                    vessel = !String.IsNullOrEmpty(tenantId)
                        ? await _Database.Vessels.ReadAsync(tenantId, vesselId, token).ConfigureAwait(false)
                        : await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    return LandingEvidence.Unavailable("vessel_unreadable: vessel " + vesselId + ": " + ex.Message);
                }

                if (vessel == null)
                {
                    return LandingEvidence.Unavailable("vessel_not_found: vessel " + vesselId + " has no record, so its protected paths are unknown");
                }
            }

            string baseBranch = !String.IsNullOrWhiteSpace(targetBranch)
                ? targetBranch
                : (!String.IsNullOrWhiteSpace(vessel?.DefaultBranch) ? vessel!.DefaultBranch : "main");

            IReadOnlyList<string> changedFiles;
            try
            {
                changedFiles = await _Git.ReadChangedPathsAgainstBaseAsync(worktreePath, baseBranch, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                return LandingEvidence.Unavailable("changed_files_unreadable: " + ex.Message);
            }

            string unifiedDiff;
            try
            {
                unifiedDiff = await _Git.DiffAsync(worktreePath, baseBranch, token).ConfigureAwait(false) ?? String.Empty;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                return LandingEvidence.Unavailable("diff_unreadable: " + ex.Message);
            }

            return new LandingEvidence
            {
                Available = true,
                Vessel = vessel,
                ChangedFiles = new List<string>(changedFiles ?? Array.Empty<string>()),
                UnifiedDiff = unifiedDiff
            };
        }

        #endregion
    }
}
