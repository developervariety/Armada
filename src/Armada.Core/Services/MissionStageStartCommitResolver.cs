namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule for the commit a mission's stage started from: the dock's stored Git anchor snapshot for this
    /// mission first, then the dock start record written at provisioning, then a start ref that is itself a full
    /// commit id. Only recorded evidence counts; a branch name is never resolved, because a branch moves and the
    /// stage's start does not. The dock record and its snapshot outlive the worktree, so the start stays readable
    /// after the dock is reclaimed.
    /// </summary>
    public static class MissionStageStartCommitResolver
    {
        #region Private-Members

        private static readonly string _Header = "[MissionStageStartCommitResolver] ";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Resolve the recorded start commit of a mission's stage.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logDirectory">Server log directory holding dock start records.</param>
        /// <param name="mission">Mission.</param>
        /// <param name="logging">Optional logging module.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The start commit and its source, or null when no recorded evidence names one.</returns>
        public static async Task<MissionStageStartCommit?> ResolveAsync(
            DatabaseDriver database,
            string? logDirectory,
            Mission mission,
            LoggingModule? logging = null,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            if (!String.IsNullOrWhiteSpace(mission.DockId))
            {
                try
                {
                    Dock? dock = await database.Docks.ReadAsync(mission.DockId!, token).ConfigureAwait(false);
                    DockGitAnchorSnapshot? snapshot = dock?.GitAnchorsSnapshot;
                    if (snapshot != null
                        && String.Equals(snapshot.MissionId, mission.Id, StringComparison.Ordinal)
                        && DockGitAnchorPersistence.IsCommit(snapshot.ProvisionedCommit))
                        return new MissionStageStartCommit { Commit = snapshot.ProvisionedCommit, Source = MissionStageStartCommit.DockAnchorSnapshotSource };
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logging?.Warn(_Header + "could not read dock anchors for mission " + mission.Id + ": " + ex.Message);
                }

                string? recorded = ReadDockStartRecord(logDirectory, mission.DockId!, logging);
                if (DockGitAnchorPersistence.IsCommit(recorded))
                    return new MissionStageStartCommit { Commit = recorded!, Source = MissionStageStartCommit.DockStartRecordSource };
            }

            string? startRef = mission.StartFromRef?.Trim();
            if (DockGitAnchorPersistence.IsCommit(startRef))
                return new MissionStageStartCommit { Commit = startRef!, Source = MissionStageStartCommit.StartRefCommitSource };
            return null;
        }

        /// <summary>
        /// True when a value, trimmed, is a full commit object id (40 or 64 hexadecimal characters). An abbreviation
        /// or a ref name is not: either can name a different commit later.
        /// </summary>
        /// <param name="value">Candidate value.</param>
        /// <returns>True for a full commit id.</returns>
        public static bool IsFullCommitId(string? value)
        {
            return value != null && DockGitAnchorPersistence.IsCommit(value.Trim());
        }

        /// <summary>
        /// Read the start record a dock wrote at provisioning, or null when there is none.
        /// </summary>
        /// <param name="logDirectory">Server log directory.</param>
        /// <param name="dockId">Dock id.</param>
        /// <param name="logging">Optional logging module.</param>
        /// <returns>The recorded commit text, trimmed, or null.</returns>
        public static string? ReadDockStartRecord(string? logDirectory, string dockId, LoggingModule? logging = null)
        {
            if (String.IsNullOrWhiteSpace(logDirectory) || String.IsNullOrWhiteSpace(dockId)) return null;
            try
            {
                string metadataPath = Path.Combine(logDirectory, "docks", dockId + ".start");
                if (!File.Exists(metadataPath)) return null;
                return File.ReadAllText(metadataPath).Trim();
            }
            catch (Exception ex)
            {
                logging?.Warn(_Header + "could not read dock start commit metadata for " + dockId + ": " + ex.Message);
                return null;
            }
        }

        #endregion
    }
}
