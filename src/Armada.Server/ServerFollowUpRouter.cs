namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// The Server-side <see cref="IFollowUpRouter"/> the D12 <c>followup_routing</c> adapter uses to
    /// give a Judge follow-up a home. It performs at most one additive action per home and NEVER
    /// creates a voyage, dispatches, lands, or approves anything:
    /// <list type="bullet">
    /// <item>a Triaged objective is created with auto-dispatch OFF;</item>
    /// <item>a duplicate is linked by appending an evidence link to the existing open objective;</item>
    /// <item>an evidence note and a blocking item take no action here: the durable follow-up row and the
    /// recorded decision hold them.</item>
    /// </list>
    /// Every method is best-effort and never throws into the adapter.
    /// </summary>
    public sealed class ServerFollowUpRouter : IFollowUpRouter
    {
        #region Private-Members

        private const string _Header = "[ServerFollowUpRouter] ";

        private readonly DatabaseDriver _Database;
        private readonly ObjectiveService _Objectives;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create the Server-side follow-up router.
        /// </summary>
        /// <param name="database">Database driver for objective lookup.</param>
        /// <param name="objectives">Objective service used to create and link objectives.</param>
        /// <param name="logging">Logging module.</param>
        public ServerFollowUpRouter(
            DatabaseDriver database,
            ObjectiveService objectives,
            LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Objectives = objectives ?? throw new ArgumentNullException(nameof(objectives));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<IReadOnlyList<FollowUpDuplicateCandidate>> GetDuplicateCandidatesAsync(string? vesselId, int limit, CancellationToken token)
        {
            List<FollowUpDuplicateCandidate> candidates = new List<FollowUpDuplicateCandidate>();
            if (limit <= 0) return candidates;

            try
            {
                List<Objective> all = await _Database.Objectives.EnumerateAsync(token).ConfigureAwait(false);
                IEnumerable<Objective> open = all
                    .Where(objective => objective.Status != ObjectiveStatusEnum.Completed
                        && objective.Status != ObjectiveStatusEnum.Cancelled);
                if (!String.IsNullOrWhiteSpace(vesselId))
                    open = open.Where(objective => objective.VesselIds != null && objective.VesselIds.Contains(vesselId!, StringComparer.Ordinal));

                foreach (Objective objective in open.OrderByDescending(o => o.LastUpdateUtc).Take(limit))
                    candidates.Add(new FollowUpDuplicateCandidate { ObjectiveId = objective.Id, Title = objective.Title ?? String.Empty });
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "duplicate-candidate lookup failed: " + ex.Message);
            }

            return candidates;
        }

        /// <inheritdoc />
        public async Task<string?> CreateTriagedObjectiveAsync(FollowUpRouteRequest request, CancellationToken token)
        {
            if (request == null) return null;
            try
            {
                JudgeFollowUp followUp = request.FollowUp;
                ObjectiveUpsertRequest upsert = new ObjectiveUpsertRequest
                {
                    Title = BuildTitle(request.ItemText),
                    Description = BuildDescription(request),
                    // Triaged intake with auto-dispatch OFF: the operator scopes and enables it. The
                    // model never dispatches or creates a voyage.
                    BacklogState = ObjectiveBacklogStateEnum.Triaged,
                    AutoDispatchEnabled = false,
                    Kind = ObjectiveKindEnum.Chore,
                    VesselIds = String.IsNullOrWhiteSpace(followUp.VesselId) ? null : new List<string> { followUp.VesselId! },
                    Tags = new List<string> { "followup" }
                };

                Objective created = await _Objectives.CreateAsync(BuildAuth(followUp), upsert, token).ConfigureAwait(false);
                return created.Id;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not create Triaged objective: " + ex.Message);
                return null;
            }
        }

        /// <inheritdoc />
        public async Task LinkDuplicateAsync(FollowUpRouteRequest request, string existingObjectiveId, CancellationToken token)
        {
            if (request == null || String.IsNullOrWhiteSpace(existingObjectiveId)) return;
            try
            {
                Objective? existing = await _Database.Objectives.ReadAsync(existingObjectiveId, token).ConfigureAwait(false);
                if (existing == null) return;

                string link = "followup-duplicate: " + request.ItemText;
                List<string> links = new List<string>(existing.EvidenceLinks ?? new List<string>());
                if (!links.Contains(link, StringComparer.Ordinal)) links.Add(link);

                ObjectiveUpsertRequest upsert = new ObjectiveUpsertRequest { EvidenceLinks = links };
                await _Objectives.UpdateAsync(BuildAuth(request.FollowUp), existingObjectiveId, upsert, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not link duplicate follow-up: " + ex.Message);
            }
        }
        #endregion

        #region Private-Methods

        private static AuthContext BuildAuth(JudgeFollowUp followUp)
        {
            string tenantId = String.IsNullOrWhiteSpace(followUp.TenantId) ? Constants.DefaultTenantId : followUp.TenantId!;
            string userId = String.IsNullOrWhiteSpace(followUp.UserId) ? Constants.DefaultUserId : followUp.UserId!;
            return AuthContext.Authenticated(tenantId, userId, true, true, "followup_routing");
        }

        private static string BuildTitle(string itemText)
        {
            string text = (itemText ?? String.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length == 0) return "Judge follow-up";
            const int cap = 120;
            return text.Length <= cap ? text : text.Substring(0, cap).TrimEnd() + "...";
        }

        private static string BuildDescription(FollowUpRouteRequest request)
        {
            string objective = String.IsNullOrWhiteSpace(request.ObjectiveTitle) ? "" : "Reviewed objective: " + request.ObjectiveTitle + "\n";
            return objective
                + "Captured from a Judge " + request.FollowUp.JudgeVerdict + " Suggested Follow-ups item.\n\n"
                + request.ItemText;
        }

        #endregion
    }
}
