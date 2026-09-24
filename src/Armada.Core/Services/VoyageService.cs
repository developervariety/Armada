namespace Armada.Core.Services
{
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Service for voyage lifecycle management.
    /// </summary>
    public class VoyageService : IVoyageService
    {
        #region Private-Members

        private string _Header = "[VoyageService] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        public VoyageService(LoggingModule logging, DatabaseDriver database)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<List<Voyage>> CheckCompletionsAsync(CancellationToken token = default, Func<Voyage, Task>? onVoyageComplete = null)
        {
            // CheckCompletionsAsync is a background/system method (called from Admiral loop).
            // It scans all tenants' voyages, so unscoped calls are appropriate here.
            List<Voyage> completedVoyages = new List<Voyage>();
            List<Voyage> activeVoyages = await _Database.Voyages.EnumerateByStatusAsync(VoyageStatusEnum.InProgress, token).ConfigureAwait(false);
            List<Voyage> openVoyages = await _Database.Voyages.EnumerateByStatusAsync(VoyageStatusEnum.Open, token).ConfigureAwait(false);
            List<Voyage> failedVoyages = await _Database.Voyages.EnumerateByStatusAsync(VoyageStatusEnum.Failed, token).ConfigureAwait(false);
            activeVoyages.AddRange(openVoyages);
            DateTime nowUtc = DateTime.UtcNow;
            activeVoyages.AddRange(failedVoyages.Where(v => VoyageCompletionRule.IsSweepCandidate(v, nowUtc)));

            foreach (Voyage voyage in activeVoyages)
            {
                VoyageCompletionResult result = await VoyageCompletionRule.ApplyAsync(_Database, voyage.Id, onVoyageComplete, token).ConfigureAwait(false);
                if (result.HookException != null)
                    _Logging.Warn(_Header + "error in OnVoyageComplete callback for voyage " + voyage.Id + ": " + result.HookException.Message);
                if (result.EventException != null)
                    _Logging.Warn(_Header + "could not record voyage.completed for voyage " + voyage.Id + ": " + result.EventException.Message);
                if (!result.Written) continue;

                _Logging.Info(_Header + "voyage " + voyage.Id + " reached terminal status " + result.Voyage!.Status + " (" + result.Verdict.Reason + ")");
                completedVoyages.Add(result.Voyage);
            }

            return completedVoyages;
        }

        /// <inheritdoc />
        public async Task<VoyageProgress?> GetProgressAsync(string voyageId, string? tenantId = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(voyageId)) return null;

            Voyage? voyage = !String.IsNullOrEmpty(tenantId)
                ? await _Database.Voyages.ReadAsync(tenantId, voyageId, token).ConfigureAwait(false)
                : await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
            if (voyage == null) return null;

            Dictionary<MissionStatusEnum, int> missionCounts = await _Database.Missions.CountByVoyageStatusAsync(voyage.Id, token).ConfigureAwait(false);

            VoyageProgress progress = new VoyageProgress
            {
                Voyage = voyage,
                TotalMissions = missionCounts.Values.Sum(),
                CompletedMissions = CountStatus(missionCounts, MissionStatusEnum.Complete) +
                    CountStatus(missionCounts, MissionStatusEnum.WorkProduced),
                FailedMissions = CountStatus(missionCounts, MissionStatusEnum.Failed) +
                    CountStatus(missionCounts, MissionStatusEnum.LandingFailed),
                InProgressMissions = CountStatus(missionCounts, MissionStatusEnum.InProgress) +
                    CountStatus(missionCounts, MissionStatusEnum.Assigned) +
                    CountStatus(missionCounts, MissionStatusEnum.Testing) +
                    CountStatus(missionCounts, MissionStatusEnum.Review) +
                    CountStatus(missionCounts, MissionStatusEnum.PullRequestOpen)
            };

            return progress;
        }

        private static int CountStatus(Dictionary<MissionStatusEnum, int> counts, MissionStatusEnum status)
        {
            return counts.TryGetValue(status, out int count) ? count : 0;
        }

        #endregion
    }
}
