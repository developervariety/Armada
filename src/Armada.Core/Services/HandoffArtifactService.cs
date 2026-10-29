namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// Stores one handoff artifact per stage attempt. The stage outcome recorder writes it when the attempt's exit has
    /// been handled (with the stage check result and the outcome link); the next stage's launch reads it, and stores one
    /// built from the stage itself when the recorder has not run yet, which the recorder then completes instead of
    /// adding a second artifact for the same attempt. Storage problems are logged, never thrown: the artifact must not
    /// disturb exit handling or a launch.
    /// </summary>
    public class HandoffArtifactService
    {
        #region Private-Members

        private string _Header = "[HandoffArtifactService] ";
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        public HandoffArtifactService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record the artifact of a stage attempt whose exit was handled. An artifact already stored for the same attempt
        /// (same output and commit, no outcome yet) by a launch that ran first is completed instead of duplicated.
        /// </summary>
        /// <param name="mission">The stage as it is after the exit was handled.</param>
        /// <param name="check">The attempt's stage check result, or null.</param>
        /// <param name="captain">The captain that ran the attempt, or null.</param>
        /// <param name="outcome">The stored stage outcome of the attempt, or null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored artifact, or null when nothing could be stored.</returns>
        /// <exception cref="ArgumentNullException">Thrown when mission is null.</exception>
        public async Task<HandoffArtifact?> RecordAttemptAsync(Mission mission, StageCheckResult? check, Captain? captain, StageOutcome? outcome, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            try
            {
                HandoffArtifact built = HandoffArtifactBuilder.Build(mission, check, captain, outcome?.Id, outcome?.Attempt ?? 1);
                HandoffArtifact? latest = await _Database.HandoffArtifacts.ReadLatestByMissionAsync(mission.Id, token).ConfigureAwait(false);
                if (latest != null && latest.StageOutcomeId == null && IsSameAttempt(latest, mission))
                {
                    built.Id = latest.Id;
                    built.CreatedUtc = latest.CreatedUtc;
                    return await _Database.HandoffArtifacts.UpdateAsync(built, token).ConfigureAwait(false);
                }

                return await _Database.HandoffArtifacts.CreateAsync(built, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
            {
                _Logging.Warn(_Header + "could not record the handoff artifact for mission " + mission.Id + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The artifact of a stage's latest attempt for the next stage's launch: the stored one when it matches the stage
        /// as it is now, else one built from the stage and stored. When storing fails the built artifact is returned
        /// unstored, so the launch still gets a rendering.
        /// </summary>
        /// <param name="stage">The prior stage.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The artifact.</returns>
        /// <exception cref="ArgumentNullException">Thrown when stage is null.</exception>
        public async Task<HandoffArtifact> GetOrCreateForStageAsync(Mission stage, CancellationToken token = default)
        {
            if (stage == null) throw new ArgumentNullException(nameof(stage));
            HandoffArtifact built = HandoffArtifactBuilder.Build(stage);
            try
            {
                HandoffArtifact? latest = await _Database.HandoffArtifacts.ReadLatestByMissionAsync(stage.Id, token).ConfigureAwait(false);
                if (latest != null && IsSameAttempt(latest, stage)) return latest;
                return await _Database.HandoffArtifacts.CreateAsync(built, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
            {
                _Logging.Warn(_Header + "could not read or store the handoff artifact for mission " + stage.Id + "; delivering it unstored: " + ex.Message);
                return built;
            }
        }

        #endregion

        #region Private-Methods

        private static bool IsSameAttempt(HandoffArtifact artifact, Mission stage)
        {
            return artifact.OutputLength == (stage.AgentOutput?.Length ?? 0)
                && String.Equals(artifact.CommitHash ?? String.Empty, stage.CommitHash ?? String.Empty, StringComparison.Ordinal)
                && artifact.DiffLength == (stage.DiffSnapshot?.Length ?? 0);
        }

        #endregion
    }
}
