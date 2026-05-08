namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Memory;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Reads the per-vessel learned-facts playbook and reflection rejection notes.
    /// </summary>
    public class ReflectionMemoryService : IReflectionMemoryService
    {
        #region Private-Members

        private const string _ReflectionAcceptedEvent = "reflection.accepted";
        private const string _ReflectionRejectedEvent = "reflection.rejected";

        private readonly DatabaseDriver _Database;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        public ReflectionMemoryService(DatabaseDriver database)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<string> ReadLearnedPlaybookContentAsync(Vessel vessel, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (String.IsNullOrEmpty(vessel.TenantId))
                return "# Vessel Learned Facts\n\nNo accepted reflection facts yet.";

            string fileName = "vessel-" + SanitizeName(vessel.Name) + "-learned.md";
            Playbook? playbook = await _Database.Playbooks.ReadByFileNameAsync(vessel.TenantId!, fileName, token).ConfigureAwait(false);
            return playbook?.Content ?? "# Vessel Learned Facts\n\nNo accepted reflection facts yet.";
        }

        /// <inheritdoc />
        public Task<List<string>> ReadRejectedProposalNotesAsync(Vessel vessel, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            // Rejection persistence lands in a later milestone. Keep this as the shared
            // service seam so accept/reject can fill it without changing dispatcher callers.
            return Task.FromResult(new List<string>());
        }

        /// <inheritdoc />
        public async Task<ReflectionAcceptProposalResult> AcceptMemoryProposalAsync(
            string missionId,
            string? editsMarkdown,
            IReflectionOutputParser parser,
            CancellationToken token = default)
        {
            ReflectionAcceptProposalResult outcome = new ReflectionAcceptProposalResult();
            if (parser == null) throw new ArgumentNullException(nameof(parser));

            if (String.IsNullOrWhiteSpace(missionId))
            {
                outcome.Error = "mission_not_found";
                return outcome;
            }

            Mission? mission = await _Database.Missions.ReadAsync(missionId.Trim(), token).ConfigureAwait(false);
            if (mission == null)
            {
                outcome.Error = "mission_not_found";
                return outcome;
            }

            if (!String.Equals(mission.Persona, "MemoryConsolidator", StringComparison.OrdinalIgnoreCase))
            {
                outcome.Error = "mission_not_a_reflection";
                return outcome;
            }

            if (!IsMissionCompleteEnoughForAcceptance(mission.Status))
            {
                outcome.Error = "mission_not_complete";
                return outcome;
            }

            if (await MissionHasReflectionDispositionAsync(mission.Id, token).ConfigureAwait(false))
            {
                outcome.Error = "proposal_already_processed";
                return outcome;
            }

            if (String.IsNullOrEmpty(mission.VesselId))
            {
                outcome.Error = "mission_not_a_reflection";
                return outcome;
            }

            Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
            if (vessel == null)
            {
                outcome.Error = "mission_not_found";
                return outcome;
            }

            string tenantId = !String.IsNullOrEmpty(vessel.TenantId) ? vessel.TenantId! : Constants.DefaultTenantId;

            string contentToApply;
            if (!String.IsNullOrWhiteSpace(editsMarkdown))
            {
                contentToApply = editsMarkdown.Trim();
            }
            else
            {
                ReflectionOutputParseResult parsed = parser.Parse(mission.AgentOutput ?? "");
                if (parsed.Verdict != ReflectionOutputParseVerdict.Success)
                {
                    outcome.Error = "output_contract_violation";
                    return outcome;
                }

                contentToApply = parsed.CandidateMarkdown ?? "";
                if (String.IsNullOrWhiteSpace(contentToApply))
                {
                    outcome.Error = "output_contract_violation";
                    return outcome;
                }
            }

            if (String.IsNullOrWhiteSpace(contentToApply))
            {
                outcome.Error = "output_contract_violation";
                return outcome;
            }

            string fileName = "vessel-" + SanitizeName(vessel.Name) + "-learned.md";
            PlaybookService playbookService = new PlaybookService(_Database, CreateSilentLogging());
            Playbook? existing = await _Database.Playbooks.ReadByFileNameAsync(tenantId, fileName, token).ConfigureAwait(false);

            Playbook persisted;
            if (existing == null)
            {
                Playbook created = new Playbook(fileName, contentToApply);
                created.TenantId = tenantId;
                created.UserId = Constants.DefaultUserId;
                playbookService.Validate(created);
                persisted = await _Database.Playbooks.CreateAsync(created, token).ConfigureAwait(false);
            }
            else
            {
                existing.Content = contentToApply;
                playbookService.Validate(existing);
                persisted = await _Database.Playbooks.UpdateAsync(existing, token).ConfigureAwait(false);
            }

            vessel.LastReflectionMissionId = mission.Id;
            await _Database.Vessels.UpdateAsync(vessel, token).ConfigureAwait(false);

            ArmadaEvent accepted = new ArmadaEvent(_ReflectionAcceptedEvent, "Reflection memory proposal accepted.");
            accepted.TenantId = mission.TenantId ?? vessel.TenantId ?? tenantId;
            accepted.EntityType = "mission";
            accepted.EntityId = mission.Id;
            accepted.MissionId = mission.Id;
            accepted.VesselId = vessel.Id;
            accepted.VoyageId = mission.VoyageId;
            accepted.Payload = JsonSerializer.Serialize(new
            {
                playbookId = persisted.Id,
                missionId = mission.Id,
                vesselId = vessel.Id
            });
            await _Database.Events.CreateAsync(accepted, token).ConfigureAwait(false);

            outcome.PlaybookId = persisted.Id;
            outcome.PlaybookVersion = persisted.LastUpdateUtc.ToString("o");
            outcome.AppliedContent = contentToApply;
            return outcome;
        }

        #endregion

        #region Private-Methods

        private static LoggingModule CreateSilentLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static bool IsMissionCompleteEnoughForAcceptance(MissionStatusEnum status)
        {
            return status != MissionStatusEnum.Pending
                && status != MissionStatusEnum.Assigned
                && status != MissionStatusEnum.InProgress;
        }

        private async Task<bool> MissionHasReflectionDispositionAsync(string missionId, CancellationToken token)
        {
            List<ArmadaEvent> events = await _Database.Events.EnumerateByMissionAsync(missionId, 50, token).ConfigureAwait(false);
            foreach (ArmadaEvent armadaEvent in events)
            {
                if (String.Equals(armadaEvent.EventType, _ReflectionAcceptedEvent, StringComparison.Ordinal))
                    return true;
                if (String.Equals(armadaEvent.EventType, _ReflectionRejectedEvent, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string SanitizeName(string name)
        {
            string lower = name.ToLowerInvariant();
            string replaced = Regex.Replace(lower, "[^a-z0-9]+", "-");
            return replaced.Trim('-');
        }

        #endregion
    }
}
