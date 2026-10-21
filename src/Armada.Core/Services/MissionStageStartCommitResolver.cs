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
    /// The one rule for the commit a mission's stage started from: the stage start record written for this mission
    /// when its dock was assigned first, then the dock's stored Git anchor snapshot for this mission, then the dock
    /// start record written at provisioning, then a start ref that is itself a full commit id. Only recorded evidence
    /// counts; a branch name is never resolved, because a branch moves and the stage's start does not.
    /// </summary>
    /// <remarks>
    /// A reclaim clears the dock's snapshot and deletes the dock start record, so for a stage that has ended only
    /// the stage start record remains. It is keyed by mission id, survives the reclaim, and expires with the dock
    /// metadata retention.
    /// </remarks>
    public static class MissionStageStartCommitResolver
    {
        #region Public-Members

        /// <summary>
        /// Directory, under the server log directory, that holds one stage start record per mission.
        /// </summary>
        public const string StageStartDirectoryName = "stage-starts";

        #endregion

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

            string? stageStart = ReadStageStartRecord(logDirectory, mission.Id, logging);
            if (DockGitAnchorPersistence.IsCommit(stageStart))
                return new MissionStageStartCommit { Commit = stageStart!, Source = MissionStageStartCommit.StageStartRecordSource };

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
        /// Write the commit a mission's stage started from, keyed by mission id. A later attempt of the same mission
        /// replaces the record, because its dock is the one whose work the mission's final commit carries.
        /// </summary>
        /// <param name="logDirectory">Server log directory.</param>
        /// <param name="missionId">Mission id.</param>
        /// <param name="commit">Full commit id the stage's dock sat at when the stage was assigned.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task WriteStageStartRecordAsync(string logDirectory, string missionId, string commit, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(logDirectory)) throw new ArgumentNullException(nameof(logDirectory));
            if (String.IsNullOrWhiteSpace(missionId)) throw new ArgumentNullException(nameof(missionId));
            if (!DockGitAnchorPersistence.IsCommit(commit?.Trim()))
                throw new ArgumentException("A stage start record holds a full commit id.", nameof(commit));

            string directory = Path.Combine(logDirectory, StageStartDirectoryName);
            Directory.CreateDirectory(directory);
            string path = StageStartRecordPath(logDirectory, missionId);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, commit!.Trim() + "\n", token).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }

        /// <summary>
        /// Read the stage start record written for a mission, or null when there is none.
        /// </summary>
        /// <param name="logDirectory">Server log directory.</param>
        /// <param name="missionId">Mission id.</param>
        /// <param name="logging">Optional logging module.</param>
        /// <returns>The recorded commit text, trimmed, or null.</returns>
        public static string? ReadStageStartRecord(string? logDirectory, string missionId, LoggingModule? logging = null)
        {
            if (String.IsNullOrWhiteSpace(logDirectory) || String.IsNullOrWhiteSpace(missionId)) return null;
            try
            {
                string path = StageStartRecordPath(logDirectory!, missionId);
                if (!File.Exists(path)) return null;
                return File.ReadAllText(path).Trim();
            }
            catch (Exception ex)
            {
                logging?.Warn(_Header + "could not read the stage start record for mission " + missionId + ": " + ex.Message);
                return null;
            }
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

        #region Private-Methods

        private static string StageStartRecordPath(string logDirectory, string missionId)
        {
            // A mission id is a generated identifier; the file name is its last path segment only.
            return Path.Combine(logDirectory, StageStartDirectoryName, Path.GetFileName(missionId.Trim()) + ".start");
        }

        #endregion
    }
}
