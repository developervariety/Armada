namespace Armada.Core.Memory
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Shared dispatcher for reflection consolidation missions.
    /// </summary>
    public class ReflectionDispatcher
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly IAdmiralService _Admiral;
        private readonly ArmadaSettings _Settings;
        private readonly IReflectionMemoryService _Memory;
        private const int _CharactersPerToken = 4;
        private const int _MaxDiffChars = 24000;
        private const int _MaxAgentOutputChars = 12000;
        private const int _MaxEventMessageChars = 4000;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="admiral">Admiral service.</param>
        /// <param name="settings">Armada settings.</param>
        /// <param name="memory">Reflection memory service.</param>
        public ReflectionDispatcher(
            DatabaseDriver database,
            IAdmiralService admiral,
            ArmadaSettings settings,
            IReflectionMemoryService memory)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Memory = memory ?? throw new ArgumentNullException(nameof(memory));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Search for an unfinished MemoryConsolidator mission for the vessel.
        /// This is an honest v1 guard with a TOCTOU race window: two concurrent
        /// callers can both observe no active mission before either dispatches.
        /// A database-level lock or uniqueness constraint is intentionally out of
        /// scope for the first reflection trigger.
        /// </summary>
        /// <param name="vesselId">Vessel identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The in-flight mission when present, otherwise null.</returns>
        public async Task<Mission?> IsReflectionInFlightAsync(string vesselId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(vesselId)) throw new ArgumentNullException(nameof(vesselId));

            List<Mission> missions = await _Database.Missions.EnumerateByVesselAsync(vesselId, token).ConfigureAwait(false);
            return missions
                .Where(m => String.Equals(m.Persona, "MemoryConsolidator", StringComparison.OrdinalIgnoreCase))
                .Where(m => !IsTerminal(m.Status))
                .OrderByDescending(m => m.CreatedUtc)
                .FirstOrDefault();
        }

        /// <summary>
        /// Build the consolidation evidence brief for a vessel.
        /// </summary>
        /// <param name="vessel">Vessel being consolidated.</param>
        /// <param name="sinceMissionId">Optional mission whose completion time starts the evidence window.</param>
        /// <param name="tokenBudget">Approximate token budget for the brief.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Evidence bundle result.</returns>
        public async Task<EvidenceBundleResult> BuildEvidenceBundleAsync(
            Vessel vessel,
            string? sinceMissionId,
            int tokenBudget,
            CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (tokenBudget < 1) tokenBudget = _Settings.DefaultReflectionTokenBudget;

            List<string> rejectedProposalNotes = await _Memory.ReadRejectedProposalNotesAsync(vessel, token).ConfigureAwait(false);
            string learnedPlaybook = await _Memory.ReadLearnedPlaybookContentAsync(vessel, token).ConfigureAwait(false);
            List<Mission> evidenceMissions = await SelectEvidenceMissionsAsync(vessel, sinceMissionId, token).ConfigureAwait(false);
            List<string> records = new List<string>();

            foreach (Mission mission in evidenceMissions)
            {
                records.Add(await BuildMissionRecordAsync(mission, token).ConfigureAwait(false));
            }

            long maxChars = (long)tokenBudget * _CharactersPerToken;
            if (maxChars > Int32.MaxValue) maxChars = Int32.MaxValue;

            List<string> included = new List<string>(records);
            int skipped = 0;
            string brief = ComposeBrief(vessel, learnedPlaybook, included, rejectedProposalNotes, skipped);
            while (included.Count > 1 && brief.Length > maxChars)
            {
                included.RemoveAt(included.Count - 1);
                skipped++;
                brief = ComposeBrief(vessel, learnedPlaybook, included, rejectedProposalNotes, skipped);
            }

            bool truncated = skipped > 0;
            return new EvidenceBundleResult
            {
                Brief = brief,
                EvidenceMissionCount = included.Count,
                RejectedProposalCount = rejectedProposalNotes.Count,
                Truncated = truncated
            };
        }

        /// <summary>
        /// Dispatch a MemoryConsolidator mission through the Reflections pipeline.
        /// </summary>
        /// <param name="vessel">Vessel being consolidated.</param>
        /// <param name="brief">Reflection mission brief.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Dispatch result.</returns>
        public async Task<DispatchResult> DispatchReflectionAsync(Vessel vessel, string brief, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (String.IsNullOrEmpty(brief)) throw new ArgumentNullException(nameof(brief));

            string title = "Consolidate learned-facts playbook for " + vessel.Name;
            List<MissionDescription> missions = new List<MissionDescription>
            {
                new MissionDescription(title, brief)
                {
                    PreferredModel = "high"
                }
            };

            Voyage voyage = await _Admiral.DispatchVoyageAsync(
                title,
                brief,
                vessel.Id,
                missions,
                "Reflections",
                token).ConfigureAwait(false);

            List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
            Mission? reflectionMission = voyageMissions
                .Where(m => String.Equals(m.Persona, "MemoryConsolidator", StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.CreatedUtc)
                .FirstOrDefault()
                ?? voyageMissions.OrderBy(m => m.CreatedUtc).FirstOrDefault();

            if (reflectionMission == null)
                throw new InvalidOperationException("Reflection dispatch created no mission");

            return new DispatchResult
            {
                MissionId = reflectionMission.Id,
                VoyageId = voyage.Id
            };
        }

        #endregion

        #region Private-Methods

        private async Task<List<Mission>> SelectEvidenceMissionsAsync(Vessel vessel, string? sinceMissionId, CancellationToken token)
        {
            List<Mission> all = await _Database.Missions.EnumerateByVesselAsync(vessel.Id, token).ConfigureAwait(false);
            List<Mission> terminal = all
                .Where(m => IsTerminal(m.Status))
                .OrderByDescending(GetEvidenceTime)
                .ToList();

            DateTime? sinceUtc = null;
            if (!String.IsNullOrEmpty(sinceMissionId))
            {
                Mission? sinceMission = await _Database.Missions.ReadAsync(sinceMissionId, token).ConfigureAwait(false);
                sinceUtc = sinceMission?.CompletedUtc;
            }
            else if (!String.IsNullOrEmpty(vessel.LastReflectionMissionId))
            {
                Mission? lastReflection = await _Database.Missions.ReadAsync(vessel.LastReflectionMissionId, token).ConfigureAwait(false);
                sinceUtc = lastReflection?.CompletedUtc;
            }

            if (sinceUtc.HasValue)
            {
                return terminal
                    .Where(m => m.CompletedUtc.HasValue && m.CompletedUtc.Value > sinceUtc.Value)
                    .ToList();
            }

            return terminal.Take(_Settings.InitialReflectionWindow).ToList();
        }

        private async Task<string> BuildMissionRecordAsync(Mission mission, CancellationToken token)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("### Mission " + mission.Id);
            sb.AppendLine("- voyageId: " + (mission.VoyageId ?? ""));
            sb.AppendLine("- persona: " + (mission.Persona ?? "Worker"));
            sb.AppendLine("- status: " + mission.Status);
            sb.AppendLine("- completedUtc: " + (mission.CompletedUtc.HasValue ? mission.CompletedUtc.Value.ToString("O") : ""));

            AppendBlock(sb, "Final diff snapshot or tail", Tail(mission.DiffSnapshot, _MaxDiffChars));
            AppendBlock(sb, "Agent output or judge notes", Tail(mission.AgentOutput, _MaxAgentOutputChars));
            await AppendAuditNotesAsync(sb, mission, token).ConfigureAwait(false);
            await AppendMissionEventsAsync(sb, mission, token).ConfigureAwait(false);
            return sb.ToString();
        }

        private async Task AppendAuditNotesAsync(StringBuilder sb, Mission mission, CancellationToken token)
        {
            List<MergeEntry> entries = await _Database.MergeEntries.EnumerateAsync(token).ConfigureAwait(false);
            List<MergeEntry> matches = entries.Where(e => e.MissionId == mission.Id).ToList();
            if (matches.Count == 0) return;

            sb.AppendLine("Audit queue notes:");
            foreach (MergeEntry entry in matches)
            {
                sb.AppendLine("- entryId: " + entry.Id + ", verdict: " + (entry.AuditDeepVerdict ?? ""));
                if (!String.IsNullOrEmpty(entry.AuditDeepNotes))
                    sb.AppendLine("  notes: " + Tail(entry.AuditDeepNotes, _MaxEventMessageChars));
                if (!String.IsNullOrEmpty(entry.AuditDeepRecommendedAction))
                    sb.AppendLine("  recommendedAction: " + Tail(entry.AuditDeepRecommendedAction, _MaxEventMessageChars));
            }
        }

        private async Task AppendMissionEventsAsync(StringBuilder sb, Mission mission, CancellationToken token)
        {
            List<ArmadaEvent> events = await _Database.Events.EnumerateByMissionAsync(mission.Id, 10, token).ConfigureAwait(false);
            if (events.Count == 0) return;

            sb.AppendLine("Mission events:");
            foreach (ArmadaEvent evt in events.OrderBy(e => e.CreatedUtc))
            {
                sb.AppendLine("- " + evt.EventType + ": " + Tail(evt.Message, _MaxEventMessageChars));
            }
        }

        private static string ComposeBrief(
            Vessel vessel,
            string learnedPlaybook,
            List<string> missionRecords,
            List<string> rejectedProposalNotes,
            int skipped)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Persona: MemoryConsolidator");
            sb.AppendLine("Pipeline: Reflections");
            sb.AppendLine("PreferredModel: high");
            sb.AppendLine("Title: Consolidate learned-facts playbook for " + vessel.Name);
            sb.AppendLine();
            sb.AppendLine("## CURRENT PLAYBOOK");
            sb.AppendLine(learnedPlaybook);
            sb.AppendLine();
            sb.AppendLine("## INSTRUCTIONS");
            sb.AppendLine("Review the evidence below and propose updates to the per-vessel learned-facts playbook.");
            sb.AppendLine("Only propose facts grounded in the evidence. Never propose CLAUDE.md edits. Never propose code changes.");
            sb.AppendLine("Output must be exactly two fenced blocks named reflections-candidate and reflections-diff.");
            sb.AppendLine();
            sb.AppendLine("## EVIDENCE BUNDLE");
            if (missionRecords.Count == 0)
            {
                sb.AppendLine("No terminal mission evidence selected.");
            }
            else
            {
                foreach (string record in missionRecords)
                {
                    sb.AppendLine(record);
                }
            }

            if (skipped > 0)
            {
                sb.AppendLine("Evidence truncated, " + skipped + " missions skipped.");
            }

            sb.AppendLine();
            sb.AppendLine("## RECENTLY REJECTED PROPOSALS");
            if (rejectedProposalNotes.Count == 0)
            {
                sb.AppendLine("No rejected reflection proposals recorded.");
            }
            else
            {
                foreach (string note in rejectedProposalNotes)
                {
                    sb.AppendLine("- " + note);
                }
            }

            sb.AppendLine();
            sb.AppendLine("## CONSTRAINTS");
            sb.AppendLine("- Never propose CLAUDE.md edits.");
            sb.AppendLine("- Never propose code changes.");
            sb.AppendLine("- Output must be exactly two fenced blocks: reflections-candidate and reflections-diff.");
            return sb.ToString();
        }

        private static void AppendBlock(StringBuilder sb, string title, string? value)
        {
            if (String.IsNullOrEmpty(value)) return;

            sb.AppendLine(title + ":");
            sb.AppendLine(value);
        }

        private static string? Tail(string? value, int maxChars)
        {
            if (String.IsNullOrEmpty(value)) return null;
            if (value.Length <= maxChars) return value;
            return "[tail of " + value.Length + " chars]\n" + value.Substring(value.Length - maxChars);
        }

        private static bool IsTerminal(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Complete
                || status == MissionStatusEnum.Failed
                || status == MissionStatusEnum.Cancelled;
        }

        private static DateTime GetEvidenceTime(Mission mission)
        {
            return mission.CompletedUtc ?? mission.LastUpdateUtc;
        }

        #endregion

        #region Public-Classes

        /// <summary>
        /// Result of evidence bundle construction.
        /// </summary>
        public sealed class EvidenceBundleResult
        {
            /// <summary>Brief text.</summary>
            public string Brief { get; set; } = "";

            /// <summary>Number of mission records included after token-budget capping.</summary>
            public int EvidenceMissionCount { get; set; }

            /// <summary>Number of rejected proposal notes included.</summary>
            public int RejectedProposalCount { get; set; }

            /// <summary>Whether older evidence records were evicted.</summary>
            public bool Truncated { get; set; }
        }

        /// <summary>
        /// Result of reflection dispatch.
        /// </summary>
        public sealed class DispatchResult
        {
            /// <summary>Created reflection mission identifier.</summary>
            public string MissionId { get; set; } = "";

            /// <summary>Created voyage identifier.</summary>
            public string VoyageId { get; set; } = "";
        }

        #endregion
    }
}
