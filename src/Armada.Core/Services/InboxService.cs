namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Builds the operator's "needs you" inbox: a single consolidated list of items awaiting a human
    /// decision or intervention across the fleet -- reviews to approve, failed landings, failed
    /// missions, and stalled captains -- ordered most-urgent first.
    /// </summary>
    public class InboxService
    {
        #region Private-Members

        private readonly string _Header = "[InboxService] ";
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private const int _MaxPerCategory = 100;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver. Required.</param>
        /// <param name="logging">Logging module. Required.</param>
        public InboxService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the inbox: actionable items ordered most-urgent first.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The list of inbox items.</returns>
        public async Task<List<InboxItem>> GetInboxAsync(CancellationToken token = default)
        {
            List<InboxItem> items = new List<InboxItem>();

            try
            {
                List<Mission> reviews = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.Review, token).ConfigureAwait(false);
                foreach (Mission mission in reviews.Take(_MaxPerCategory))
                {
                    bool overdue = mission.ReviewDeadlineUtc.HasValue && mission.ReviewDeadlineUtc.Value < DateTime.UtcNow;
                    items.Add(new InboxItem
                    {
                        Kind = "review",
                        Severity = overdue ? InboxSeverityEnum.Critical : InboxSeverityEnum.Warning,
                        Title = "Review: " + mission.Title,
                        Detail = overdue ? "Review is overdue -- awaiting your approval." : "Awaiting your review.",
                        EntityType = "mission",
                        EntityId = mission.Id,
                        Href = "/missions/" + mission.Id
                    });
                }

                List<Mission> landingFailed = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.LandingFailed, token).ConfigureAwait(false);
                foreach (Mission mission in landingFailed.Take(_MaxPerCategory))
                {
                    items.Add(new InboxItem
                    {
                        Kind = "landing_failed",
                        Severity = InboxSeverityEnum.Critical,
                        Title = "Landing failed: " + mission.Title,
                        Detail = String.IsNullOrWhiteSpace(mission.FailureReason) ? "The work could not be landed." : mission.FailureReason!,
                        EntityType = "mission",
                        EntityId = mission.Id,
                        Href = "/missions/" + mission.Id
                    });
                }

                List<Mission> failed = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.Failed, token).ConfigureAwait(false);
                foreach (Mission mission in failed.Take(_MaxPerCategory))
                {
                    items.Add(new InboxItem
                    {
                        Kind = "failed",
                        Severity = InboxSeverityEnum.Warning,
                        Title = "Failed: " + mission.Title,
                        Detail = String.IsNullOrWhiteSpace(mission.FailureReason) ? "The mission failed." : mission.FailureReason!,
                        EntityType = "mission",
                        EntityId = mission.Id,
                        Href = "/missions/" + mission.Id
                    });
                }

                List<Captain> stalled = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Stalled, token).ConfigureAwait(false);
                foreach (Captain captain in stalled.Take(_MaxPerCategory))
                {
                    items.Add(new InboxItem
                    {
                        Kind = "stalled_captain",
                        Severity = InboxSeverityEnum.Warning,
                        Title = "Stalled captain: " + captain.Name,
                        Detail = "This captain is stalled and may need recovery or a dock reclaim.",
                        EntityType = "captain",
                        EntityId = captain.Id,
                        Href = "/captains/" + captain.Id
                    });
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error building inbox: " + ex.Message);
            }

            return items
                .OrderByDescending(item => (int)item.Severity)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        #endregion
    }
}
