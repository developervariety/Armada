namespace Armada.Core.Memory
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
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
        private readonly PackUsageMiner? _PackUsageMiner;
        private const int _CharactersPerToken = 4;
        private const int _MaxDiffChars = 24000;
        private const int _MaxAgentOutputChars = 12000;
        private const int _MaxEventMessageChars = 4000;
        private const int _RecentCommitWindow = 20;
        private const string _ReflectionDispatchedEvent = "reflection.dispatched";

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
            : this(database, admiral, settings, memory, null)
        {
        }

        /// <summary>
        /// Instantiate with an explicit PackUsageMiner. Used by v2-F1 pack-curate brief assembly.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="admiral">Admiral service.</param>
        /// <param name="settings">Armada settings.</param>
        /// <param name="memory">Reflection memory service.</param>
        /// <param name="packUsageMiner">Pack-usage miner (null disables pack-curate brief assembly).</param>
        public ReflectionDispatcher(
            DatabaseDriver database,
            IAdmiralService admiral,
            ArmadaSettings settings,
            IReflectionMemoryService memory,
            PackUsageMiner? packUsageMiner)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _PackUsageMiner = packUsageMiner;
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
        /// Build the consolidation evidence brief for a vessel (legacy v1 single-mode entry point).
        /// Equivalent to <see cref="BuildEvidenceBundleAsync(Vessel, string?, int, ReflectionMode, CancellationToken)"/>
        /// with <see cref="ReflectionMode.Consolidate"/>.
        /// </summary>
        /// <param name="vessel">Vessel being consolidated.</param>
        /// <param name="sinceMissionId">Optional mission whose completion time starts the evidence window.</param>
        /// <param name="tokenBudget">Approximate token budget for the brief.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Evidence bundle result.</returns>
        public Task<EvidenceBundleResult> BuildEvidenceBundleAsync(
            Vessel vessel,
            string? sinceMissionId,
            int tokenBudget,
            CancellationToken token = default)
        {
            return BuildEvidenceBundleAsync(vessel, sinceMissionId, tokenBudget, ReflectionMode.Consolidate, token);
        }

        /// <summary>
        /// Build a mode-aware reflection brief for a vessel. Reorganize mode skips the evidence
        /// bundle and includes the playbook + recent commit subjects + reorganize-mode rejections.
        /// Combined mode appends reorganize instructions to a full v1 evidence brief.
        /// </summary>
        /// <param name="vessel">Vessel being consolidated.</param>
        /// <param name="sinceMissionId">Optional evidence-window anchor; ignored in pure reorganize mode.</param>
        /// <param name="tokenBudget">Approximate token budget for the brief.</param>
        /// <param name="mode">Reflection mode driving brief assembly.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Evidence bundle result.</returns>
        public async Task<EvidenceBundleResult> BuildEvidenceBundleAsync(
            Vessel vessel,
            string? sinceMissionId,
            int tokenBudget,
            ReflectionMode mode,
            CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (tokenBudget < 1)
            {
                tokenBudget = mode switch
                {
                    ReflectionMode.Reorganize => _Settings.DefaultReorganizeTokenBudget,
                    ReflectionMode.PackCurate => _Settings.DefaultPackCurateTokenBudget,
                    _ => _Settings.DefaultReflectionTokenBudget,
                };
            }

            if (mode == ReflectionMode.Reorganize)
            {
                return await BuildReorganizeBriefAsync(vessel, tokenBudget, token).ConfigureAwait(false);
            }

            if (mode == ReflectionMode.PackCurate)
            {
                return await BuildPackCurateBriefAsync(vessel, tokenBudget, token).ConfigureAwait(false);
            }

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

            List<string> recentCommitSubjects = mode == ReflectionMode.ConsolidateAndReorganize
                ? await _Memory.ReadRecentCommitSubjectsAsync(vessel, _RecentCommitWindow, token).ConfigureAwait(false)
                : new List<string>();

            List<string> included = new List<string>(records);
            int skipped = 0;
            string brief = ComposeBrief(vessel, learnedPlaybook, included, rejectedProposalNotes, skipped, mode, recentCommitSubjects);
            while (included.Count > 1 && brief.Length > maxChars)
            {
                included.RemoveAt(included.Count - 1);
                skipped++;
                brief = ComposeBrief(vessel, learnedPlaybook, included, rejectedProposalNotes, skipped, mode, recentCommitSubjects);
            }

            bool truncated = skipped > 0;
            return new EvidenceBundleResult
            {
                Brief = brief,
                EvidenceMissionCount = included.Count,
                RejectedProposalCount = rejectedProposalNotes.Count,
                Truncated = truncated,
                Mode = mode
            };
        }

        /// <summary>
        /// Build the reorganize-only brief: current playbook + recent commit subjects +
        /// reorganize-mode rejection feedback + reorganize-specific constraints. No evidence bundle.
        /// </summary>
        /// <param name="vessel">Vessel being reorganized.</param>
        /// <param name="tokenBudget">Approximate token budget for the brief.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Evidence bundle result with EvidenceMissionCount = 0 and Truncated = false.</returns>
        public async Task<EvidenceBundleResult> BuildReorganizeBriefAsync(
            Vessel vessel,
            int tokenBudget,
            CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (tokenBudget < 1) tokenBudget = _Settings.DefaultReorganizeTokenBudget;

            string learnedPlaybook = await _Memory.ReadLearnedPlaybookContentAsync(vessel, token).ConfigureAwait(false);
            List<string> reorganizeRejections = await _Memory
                .ReadRejectedProposalNotesByModeAsync(vessel, ReflectionMode.Reorganize, token).ConfigureAwait(false);
            List<string> recentCommitSubjects = await _Memory
                .ReadRecentCommitSubjectsAsync(vessel, _RecentCommitWindow, token).ConfigureAwait(false);

            string brief = ComposeReorganizeBrief(vessel, learnedPlaybook, reorganizeRejections, recentCommitSubjects);
            return new EvidenceBundleResult
            {
                Brief = brief,
                EvidenceMissionCount = 0,
                RejectedProposalCount = reorganizeRejections.Count,
                Truncated = false,
                Mode = ReflectionMode.Reorganize
            };
        }

        /// <summary>
        /// Build the pack-curate brief: current vessel_pack_hints rows, mined pack-usage
        /// evidence (four-bucket triples per terminal mission since last accepted pack-curate),
        /// and recently rejected pack-curate proposals (Reflections v2-F1).
        /// </summary>
        /// <param name="vessel">Vessel being curated.</param>
        /// <param name="tokenBudget">Approximate token budget for the brief.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Evidence bundle result with EvidenceMissionCount = mined-mission count.</returns>
        public async Task<EvidenceBundleResult> BuildPackCurateBriefAsync(
            Vessel vessel,
            int tokenBudget,
            CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (tokenBudget < 1) tokenBudget = _Settings.DefaultPackCurateTokenBudget;

            List<VesselPackHint> currentHints = await _Database.VesselPackHints
                .EnumerateActiveByVesselAsync(vessel.Id, token).ConfigureAwait(false);
            List<string> packCurateRejections = await _Memory
                .ReadRejectedProposalNotesByModeAsync(vessel, ReflectionMode.PackCurate, token).ConfigureAwait(false);

            List<Mission> evidenceMissions = await SelectPackCurateEvidenceMissionsAsync(vessel, token).ConfigureAwait(false);

            List<PackUsageTriple> triples = new List<PackUsageTriple>();
            if (_PackUsageMiner != null)
            {
                foreach (Mission m in evidenceMissions)
                {
                    PackUsageTriple t = await _PackUsageMiner.MineAsync(m, token).ConfigureAwait(false);
                    triples.Add(t);
                }
            }
            else
            {
                foreach (Mission m in evidenceMissions)
                {
                    triples.Add(new PackUsageTriple { MissionId = m.Id, LogAvailable = false });
                }
            }

            long maxChars = (long)tokenBudget * _CharactersPerToken;
            if (maxChars > Int32.MaxValue) maxChars = Int32.MaxValue;

            int includedCount = triples.Count;
            int skipped = 0;
            string brief = ComposePackCurateBrief(vessel, currentHints, evidenceMissions, triples, packCurateRejections, includedCount, skipped);
            while (includedCount > 1 && brief.Length > maxChars)
            {
                includedCount--;
                skipped++;
                brief = ComposePackCurateBrief(vessel, currentHints, evidenceMissions, triples, packCurateRejections, includedCount, skipped);
            }

            return new EvidenceBundleResult
            {
                Brief = brief,
                EvidenceMissionCount = includedCount,
                RejectedProposalCount = packCurateRejections.Count,
                Truncated = skipped > 0,
                Mode = ReflectionMode.PackCurate
            };
        }

        /// <summary>
        /// Dispatch a MemoryConsolidator mission through the Reflections pipeline (legacy v1 entry).
        /// </summary>
        /// <param name="vessel">Vessel being consolidated.</param>
        /// <param name="brief">Reflection mission brief.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Dispatch result.</returns>
        public Task<DispatchResult> DispatchReflectionAsync(Vessel vessel, string brief, CancellationToken token = default)
        {
            return DispatchReflectionAsync(vessel, brief, ReflectionMode.Consolidate, false, _Settings.DefaultReflectionTokenBudget, token);
        }

        /// <summary>
        /// Dispatch a MemoryConsolidator mission with mode + dual-Judge controls.
        /// Emits a <c>reflection.dispatched</c> ArmadaEvent with payload
        /// <c>{ mode, dualJudge, tokenBudget }</c>.
        /// </summary>
        /// <param name="vessel">Vessel being consolidated.</param>
        /// <param name="brief">Reflection mission brief.</param>
        /// <param name="mode">Reflection mode.</param>
        /// <param name="dualJudge">When true, dispatches the ReflectionsDualJudge pipeline.</param>
        /// <param name="tokenBudget">Token budget recorded in the dispatched event.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Dispatch result.</returns>
        public async Task<DispatchResult> DispatchReflectionAsync(
            Vessel vessel,
            string brief,
            ReflectionMode mode,
            bool dualJudge,
            int tokenBudget,
            CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (String.IsNullOrEmpty(brief)) throw new ArgumentNullException(nameof(brief));

            string title = mode switch
            {
                ReflectionMode.Reorganize => "Reorganize learned-facts playbook for " + vessel.Name,
                ReflectionMode.ConsolidateAndReorganize => "Consolidate and reorganize learned-facts playbook for " + vessel.Name,
                ReflectionMode.PackCurate => "Curate context-pack hints for " + vessel.Name,
                _ => "Consolidate learned-facts playbook for " + vessel.Name,
            };
            string pipelineId = dualJudge ? "ReflectionsDualJudge" : "Reflections";

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
                pipelineId,
                token).ConfigureAwait(false);

            List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
            Mission? reflectionMission = voyageMissions
                .Where(m => String.Equals(m.Persona, "MemoryConsolidator", StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.CreatedUtc)
                .FirstOrDefault()
                ?? voyageMissions.OrderBy(m => m.CreatedUtc).FirstOrDefault();

            if (reflectionMission == null)
                throw new InvalidOperationException("Reflection dispatch created no mission");

            await EmitDispatchedEventAsync(reflectionMission, vessel, mode, dualJudge, tokenBudget, token).ConfigureAwait(false);

            return new DispatchResult
            {
                MissionId = reflectionMission.Id,
                VoyageId = voyage.Id,
                Mode = mode,
                DualJudge = dualJudge
            };
        }

        /// <summary>
        /// After an audit queue drain, evaluates the vessel reflection mission-count threshold,
        /// in-flight concurrency, and evidence availability; dispatches a consolidate-mode
        /// reflection when due.
        /// </summary>
        /// <param name="vessel">Vessel to evaluate.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Dispatch result when a reflection mission was created; otherwise null.</returns>
        public async Task<DispatchResult?> TryAutoDispatchAfterAuditDrainAsync(Vessel vessel, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            int effectiveThreshold = vessel.ReflectionThreshold ?? _Settings.DefaultReflectionThreshold;
            List<Mission> evidenceMissions = await SelectEvidenceMissionsAsync(vessel, null, token).ConfigureAwait(false);
            if (evidenceMissions.Count < effectiveThreshold)
                return null;

            Mission? inFlight = await IsReflectionInFlightAsync(vessel.Id, token).ConfigureAwait(false);
            if (inFlight != null)
                return null;

            EvidenceBundleResult bundle = await BuildEvidenceBundleAsync(
                    vessel,
                    null,
                    _Settings.DefaultReflectionTokenBudget,
                    ReflectionMode.Consolidate,
                    token)
                .ConfigureAwait(false);

            if (bundle.EvidenceMissionCount == 0 && bundle.RejectedProposalCount == 0)
                return null;

            DispatchResult dispatched = await DispatchReflectionAsync(
                vessel,
                bundle.Brief,
                ReflectionMode.Consolidate,
                false,
                _Settings.DefaultReflectionTokenBudget,
                token).ConfigureAwait(false);
            return dispatched;
        }

        /// <summary>
        /// After an audit queue drain, evaluates the vessel pack-curate threshold + anti-thrash
        /// and dispatches a pack-curate-mode reflection when due. v2-F1 audit-drain auto-trigger.
        /// Anti-thrash: skip if the most recent pack-curate was accepted and no terminal mission
        /// since then has non-empty filesGrepDiscovered (no new pack-miss evidence).
        /// </summary>
        /// <param name="vessel">Vessel to evaluate.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Dispatch result when a pack-curate mission was created; otherwise null.</returns>
        public async Task<DispatchResult?> TryAutoDispatchPackCurateAfterAuditDrainAsync(Vessel vessel, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (!vessel.PackCurateThreshold.HasValue) return null;

            int threshold = vessel.PackCurateThreshold.Value;
            if (threshold <= 0) return null;

            DateTime? sinceUtc = await ResolveLastAcceptedPackCurateAtAsync(vessel, token).ConfigureAwait(false);
            List<Mission> terminalMissions = await _Database.Missions.EnumerateByVesselAsync(vessel.Id, token).ConfigureAwait(false);
            int countSince = 0;
            foreach (Mission m in terminalMissions)
            {
                if (!IsTerminal(m.Status)) continue;
                if (sinceUtc.HasValue && m.CompletedUtc.HasValue && m.CompletedUtc.Value <= sinceUtc.Value) continue;
                countSince++;
            }

            if (countSince < threshold) return null;

            Mission? inFlight = await IsReflectionInFlightAsync(vessel.Id, token).ConfigureAwait(false);
            if (inFlight != null) return null;

            if (!await PackCurateAntiThrashHasNewEvidenceAsync(vessel, sinceUtc, token).ConfigureAwait(false))
                return null;

            EvidenceBundleResult bundle = await BuildPackCurateBriefAsync(
                vessel, _Settings.DefaultPackCurateTokenBudget, token).ConfigureAwait(false);
            DispatchResult dispatched = await DispatchReflectionAsync(
                vessel,
                bundle.Brief,
                ReflectionMode.PackCurate,
                false,
                _Settings.DefaultPackCurateTokenBudget,
                token).ConfigureAwait(false);
            return dispatched;
        }

        /// <summary>
        /// After an audit queue drain, evaluates the vessel reorganize threshold + anti-thrash
        /// and dispatches a reorganize-mode reflection when due. v2-F4 audit-drain auto-trigger.
        /// </summary>
        /// <param name="vessel">Vessel to evaluate.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Dispatch result when a reorganize mission was created; otherwise null.</returns>
        public async Task<DispatchResult?> TryAutoDispatchReorganizeAfterAuditDrainAsync(Vessel vessel, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (!vessel.ReorganizeThreshold.HasValue) return null;

            int reorganizeThresholdTokens = vessel.ReorganizeThreshold.Value;
            if (reorganizeThresholdTokens <= 0) return null;

            string playbookContent = await _Memory.ReadLearnedPlaybookContentAsync(vessel, token).ConfigureAwait(false);
            int playbookChars = playbookContent.Length;
            int playbookTokensApprox = playbookChars / _CharactersPerToken;
            if (playbookTokensApprox < reorganizeThresholdTokens)
                return null;

            Mission? inFlight = await IsReflectionInFlightAsync(vessel.Id, token).ConfigureAwait(false);
            if (inFlight != null)
                return null;

            if (!await PassesAntiThrashAsync(vessel, playbookChars, token).ConfigureAwait(false))
                return null;

            EvidenceBundleResult bundle = await BuildReorganizeBriefAsync(vessel, _Settings.DefaultReorganizeTokenBudget, token).ConfigureAwait(false);
            DispatchResult dispatched = await DispatchReflectionAsync(
                vessel,
                bundle.Brief,
                ReflectionMode.Reorganize,
                false,
                _Settings.DefaultReorganizeTokenBudget,
                token).ConfigureAwait(false);
            return dispatched;
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
            int skipped,
            ReflectionMode mode,
            List<string> recentCommitSubjects)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Persona: MemoryConsolidator");
            sb.AppendLine("Pipeline: Reflections");
            sb.AppendLine("PreferredModel: high");
            sb.AppendLine("Title: " + (mode == ReflectionMode.ConsolidateAndReorganize
                ? "Consolidate and reorganize learned-facts playbook for "
                : "Consolidate learned-facts playbook for ") + vessel.Name);
            sb.AppendLine("Mode: " + ReflectionMemoryService.ModeToWireString(mode));
            sb.AppendLine();
            sb.AppendLine("## CURRENT PLAYBOOK");
            sb.AppendLine(learnedPlaybook);
            sb.AppendLine();
            sb.AppendLine("## INSTRUCTIONS");
            if (mode == ReflectionMode.ConsolidateAndReorganize)
            {
                sb.AppendLine("Mine the evidence below for new facts AND reorganize the resulting playbook structurally.");
                sb.AppendLine("Permitted in this combined mode: add facts grounded in evidence, dedupe against existing entries, replace stale entries with fresh evidence, group, merge near-duplicates, drop stale, reorder, reword.");
                sb.AppendLine("Forbidden: propose CLAUDE.md edits or code changes.");
            }
            else
            {
                sb.AppendLine("Review the evidence below and propose updates to the per-vessel learned-facts playbook.");
                sb.AppendLine("Only propose facts grounded in the evidence. Never propose CLAUDE.md edits. Never propose code changes.");
            }
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

            if (mode == ReflectionMode.ConsolidateAndReorganize && recentCommitSubjects.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## RECENT COMMIT CONTEXT");
                foreach (string line in recentCommitSubjects)
                {
                    sb.AppendLine("- " + line);
                }
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
            if (mode == ReflectionMode.ConsolidateAndReorganize)
            {
                sb.AppendLine("- The diff JSON `added` array MAY contain facts (combined mode permits the consolidate half to add).");
            }
            return sb.ToString();
        }

        private static string ComposeReorganizeBrief(
            Vessel vessel,
            string learnedPlaybook,
            List<string> reorganizeRejections,
            List<string> recentCommitSubjects)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Persona: MemoryConsolidator");
            sb.AppendLine("Pipeline: Reflections");
            sb.AppendLine("PreferredModel: high");
            sb.AppendLine("Title: Reorganize learned-facts playbook for " + vessel.Name);
            sb.AppendLine("Mode: reorganize");
            sb.AppendLine();
            sb.AppendLine("## CURRENT PLAYBOOK");
            sb.AppendLine(learnedPlaybook);
            sb.AppendLine();
            sb.AppendLine("## INSTRUCTIONS (reorganize-specific)");
            sb.AppendLine("Permitted: group, merge near-duplicates, drop stale entries (use commit context to spot staleness), reorder, reword without changing factual content.");
            sb.AppendLine("Forbidden: add new facts, change semantic content, propose CLAUDE.md edits, propose code changes.");
            sb.AppendLine("If the captain finds itself wanting to add a fact, the correct mode is consolidate (or consolidate-and-reorganize if combining), not reorganize.");
            sb.AppendLine();
            sb.AppendLine("## RECENT COMMIT CONTEXT");
            if (recentCommitSubjects.Count == 0)
            {
                sb.AppendLine("No recent commit subjects available.");
            }
            else
            {
                foreach (string line in recentCommitSubjects)
                {
                    sb.AppendLine("- " + line);
                }
            }

            sb.AppendLine();
            sb.AppendLine("## RECENTLY REJECTED REORGANIZE PROPOSALS");
            if (reorganizeRejections.Count == 0)
            {
                sb.AppendLine("No rejected reorganize proposals recorded.");
            }
            else
            {
                foreach (string note in reorganizeRejections)
                {
                    sb.AppendLine("- " + note);
                }
            }

            sb.AppendLine();
            sb.AppendLine("## CONSTRAINTS");
            sb.AppendLine("- Output must be exactly two fenced blocks named reflections-candidate and reflections-diff.");
            sb.AppendLine("- The diff JSON `added` array must be empty (or contain only structural/header markers, not facts).");
            sb.AppendLine("- Never add new facts. Never propose CLAUDE.md edits. Never propose code changes.");
            return sb.ToString();
        }

        private async Task EmitDispatchedEventAsync(
            Mission reflectionMission,
            Vessel vessel,
            ReflectionMode mode,
            bool dualJudge,
            int tokenBudget,
            CancellationToken token)
        {
            try
            {
                ArmadaEvent dispatched = new ArmadaEvent(_ReflectionDispatchedEvent, "Reflection memory mission dispatched.");
                dispatched.TenantId = reflectionMission.TenantId ?? vessel.TenantId ?? Constants.DefaultTenantId;
                dispatched.EntityType = "mission";
                dispatched.EntityId = reflectionMission.Id;
                dispatched.MissionId = reflectionMission.Id;
                dispatched.VesselId = vessel.Id;
                dispatched.VoyageId = reflectionMission.VoyageId;
                dispatched.Payload = JsonSerializer.Serialize(new
                {
                    mode = ReflectionMemoryService.ModeToWireString(mode),
                    dualJudge = dualJudge,
                    tokenBudget = tokenBudget,
                    missionId = reflectionMission.Id,
                    vesselId = vessel.Id
                });
                await _Database.Events.CreateAsync(dispatched, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort observability; never block dispatch on event persistence.
            }
        }

        private async Task<bool> PassesAntiThrashAsync(Vessel vessel, int currentPlaybookChars, CancellationToken token)
        {
            EnumerationQuery query = new EnumerationQuery
            {
                VesselId = vessel.Id,
                EventType = "reflection.accepted",
                PageNumber = 1,
                PageSize = 50
            };
            EnumerationResult<ArmadaEvent> page = await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false);
            ArmadaEvent? lastReorganizeAccept = null;
            int? lastAcceptCharsAfter = null;
            foreach (ArmadaEvent evt in page.Objects.OrderByDescending(e => e.CreatedUtc))
            {
                if (String.IsNullOrEmpty(evt.Payload)) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(evt.Payload);
                    JsonElement root = doc.RootElement;
                    string evtMode = "consolidate";
                    if (root.TryGetProperty("mode", out JsonElement modeEl) && modeEl.ValueKind == JsonValueKind.String)
                        evtMode = modeEl.GetString() ?? "consolidate";

                    if (!String.Equals(evtMode, "reorganize", StringComparison.OrdinalIgnoreCase)) continue;

                    lastReorganizeAccept = evt;
                    if (root.TryGetProperty("appliedContentLength", out JsonElement lenEl) && lenEl.ValueKind == JsonValueKind.Number)
                    {
                        if (lenEl.TryGetInt32(out int parsedLen)) lastAcceptCharsAfter = parsedLen;
                    }
                    break;
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            if (lastReorganizeAccept == null) return true;
            if (!lastAcceptCharsAfter.HasValue) return true;

            int charsAfter = lastAcceptCharsAfter.Value;
            int delta = currentPlaybookChars - charsAfter;
            if (delta < 0) return true;

            double growthRatio = charsAfter <= 0 ? 1.0 : (double)delta / charsAfter;
            if (growthRatio >= _Settings.ReorganizeAntiThrashGrowthRatio) return true;

            int approxNewEntries = delta / 64;
            if (approxNewEntries >= _Settings.ReorganizeAntiThrashMinNewEntries) return true;

            return false;
        }

        private async Task<List<Mission>> SelectPackCurateEvidenceMissionsAsync(Vessel vessel, CancellationToken token)
        {
            List<Mission> all = await _Database.Missions.EnumerateByVesselAsync(vessel.Id, token).ConfigureAwait(false);
            List<Mission> terminal = all
                .Where(m => IsTerminal(m.Status))
                .Where(m => !String.Equals(m.Persona, "MemoryConsolidator", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(GetEvidenceTime)
                .ToList();

            DateTime? sinceUtc = await ResolveLastAcceptedPackCurateAtAsync(vessel, token).ConfigureAwait(false);
            if (sinceUtc.HasValue)
            {
                return terminal
                    .Where(m => m.CompletedUtc.HasValue && m.CompletedUtc.Value > sinceUtc.Value)
                    .ToList();
            }

            return terminal.Take(_Settings.PackCurateInitialWindow).ToList();
        }

        private async Task<DateTime?> ResolveLastAcceptedPackCurateAtAsync(Vessel vessel, CancellationToken token)
        {
            EnumerationQuery query = new EnumerationQuery
            {
                VesselId = vessel.Id,
                EventType = "reflection.accepted",
                PageNumber = 1,
                PageSize = 50
            };
            EnumerationResult<ArmadaEvent> page = await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false);
            foreach (ArmadaEvent evt in page.Objects.OrderByDescending(e => e.CreatedUtc))
            {
                if (String.IsNullOrEmpty(evt.Payload)) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(evt.Payload);
                    if (doc.RootElement.TryGetProperty("mode", out JsonElement modeEl)
                        && modeEl.ValueKind == JsonValueKind.String
                        && String.Equals(modeEl.GetString(), "pack-curate", StringComparison.OrdinalIgnoreCase))
                    {
                        return evt.CreatedUtc;
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            return null;
        }

        private async Task<bool> PackCurateAntiThrashHasNewEvidenceAsync(Vessel vessel, DateTime? sinceUtc, CancellationToken token)
        {
            if (!sinceUtc.HasValue) return true;
            if (_PackUsageMiner == null) return true;

            List<Mission> all = await _Database.Missions.EnumerateByVesselAsync(vessel.Id, token).ConfigureAwait(false);
            foreach (Mission m in all)
            {
                if (!IsTerminal(m.Status)) continue;
                if (!m.CompletedUtc.HasValue || m.CompletedUtc.Value <= sinceUtc.Value) continue;

                PackUsageTriple triple = await _PackUsageMiner.MineAsync(m, token).ConfigureAwait(false);
                if (triple.LogAvailable && triple.FilesGrepDiscovered.Count > 0) return true;
            }

            return false;
        }

        private static string ComposePackCurateBrief(
            Vessel vessel,
            List<VesselPackHint> currentHints,
            List<Mission> evidenceMissions,
            List<PackUsageTriple> triples,
            List<string> packCurateRejections,
            int includedCount,
            int skipped)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Persona: MemoryConsolidator");
            sb.AppendLine("Pipeline: Reflections");
            sb.AppendLine("PreferredModel: high");
            sb.AppendLine("Title: Curate context-pack hints for " + vessel.Name);
            sb.AppendLine("Mode: pack-curate");
            sb.AppendLine();

            sb.AppendLine("## CURRENT VESSEL PACK HINTS");
            if (currentHints.Count == 0)
            {
                sb.AppendLine("No pack hints recorded for this vessel yet.");
            }
            else
            {
                List<object> hintObjects = new List<object>();
                foreach (VesselPackHint h in currentHints)
                {
                    hintObjects.Add(new
                    {
                        id = h.Id,
                        goalPattern = h.GoalPattern,
                        mustInclude = h.GetMustInclude(),
                        mustExclude = h.GetMustExclude(),
                        priority = h.Priority,
                        confidence = h.Confidence,
                        justification = h.Justification ?? "",
                        sourceMissionIds = h.GetSourceMissionIds(),
                        createdUtc = h.CreatedUtc.ToString("O")
                    });
                }
                sb.AppendLine(JsonSerializer.Serialize(hintObjects, new JsonSerializerOptions { WriteIndented = true }));
            }

            sb.AppendLine();
            sb.AppendLine("## INSTRUCTIONS (pack-curate-specific)");
            sb.AppendLine("Permitted operations:");
            sb.AppendLine("- Add new hints (proposed rows) backed by evidence in the bundle below.");
            sb.AppendLine("- Modify existing hints (paths, priority, justification) when evidence shows over- or under-inclusion.");
            sb.AppendLine("- Disable existing hints (active=false) when evidence shows they no longer match anything (e.g., file path renamed).");
            sb.AppendLine();
            sb.AppendLine("Forbidden operations:");
            sb.AppendLine("- Do NOT propose hints whose goalPattern is `.*`, empty, whitespace, or fewer than 3 characters (rejected as pack_hint_pattern_too_broad).");
            sb.AppendLine("- Do NOT propose paths that aren't in the vessel repo (the accept tool runs git ls-tree validation; non-matches surface as warnings).");
            sb.AppendLine("- Do NOT propose CLAUDE.md edits or code changes.");
            sb.AppendLine();
            sb.AppendLine("Confidence guidance:");
            sb.AppendLine("- high: same prestaged/grep-discovered pattern appears in 3+ missions for matching goals.");
            sb.AppendLine("- medium: appears in 2 missions.");
            sb.AppendLine("- low: appears in 1 mission OR evidence is mixed.");

            sb.AppendLine();
            sb.AppendLine("## PACK USAGE EVIDENCE BUNDLE");
            if (includedCount == 0)
            {
                sb.AppendLine("No terminal mission evidence available since last accepted pack-curate.");
            }
            else
            {
                int emitted = 0;
                for (int i = 0; i < triples.Count && emitted < includedCount; i++)
                {
                    Mission m = evidenceMissions[i];
                    PackUsageTriple t = triples[i];
                    sb.AppendLine();
                    sb.AppendLine("### Mission " + m.Id);
                    sb.AppendLine("- voyageId: " + (m.VoyageId ?? ""));
                    sb.AppendLine("- goal: " + (m.Title ?? ""));
                    sb.AppendLine("- persona: " + (m.Persona ?? "Worker"));
                    sb.AppendLine("- status: " + m.Status);
                    sb.AppendLine("- logAvailable: " + (t.LogAvailable ? "true" : "false"));
                    sb.AppendLine("- prestagedFiles:");
                    if (m.PrestagedFiles != null)
                    {
                        foreach (PrestagedFile pf in m.PrestagedFiles)
                            sb.AppendLine("    - " + pf.DestPath);
                    }
                    AppendBucket(sb, "filesReadFromPack", t.FilesReadFromPack);
                    AppendBucket(sb, "filesIgnoredFromPack", t.FilesIgnoredFromPack);
                    AppendBucket(sb, "filesGrepDiscovered", t.FilesGrepDiscovered);
                    AppendBucket(sb, "filesEdited", t.FilesEdited);
                    emitted++;
                }
            }

            if (skipped > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Evidence truncated, " + skipped + " missions skipped to fit token budget.");
            }

            sb.AppendLine();
            sb.AppendLine("## RECENTLY REJECTED PACK-CURATE PROPOSALS");
            if (packCurateRejections.Count == 0)
            {
                sb.AppendLine("No rejected pack-curate proposals recorded.");
            }
            else
            {
                foreach (string note in packCurateRejections)
                    sb.AppendLine("- " + note);
            }

            sb.AppendLine();
            sb.AppendLine("## CONSTRAINTS");
            sb.AppendLine("- Output must be exactly two fenced blocks named reflections-candidate and reflections-diff.");
            sb.AppendLine("- The reflections-candidate block is JSON in pack-curate mode (NOT markdown). Shape: {\"addHints\":[...], \"modifyHints\":[...], \"disableHints\":[...]}.");
            sb.AppendLine("- The reflections-diff block is JSON: {\"added\":[\"N new hints\"], \"modified\":[...], \"disabled\":[...], \"evidenceConfidence\":\"high|mixed|low\", \"missionsExamined\":N, \"notes\":\"one paragraph\"}.");
            sb.AppendLine("- Never propose CLAUDE.md edits or code changes.");
            sb.AppendLine("- Never add facts to the learned-facts playbook in this mode.");
            return sb.ToString();
        }

        private static void AppendBucket(StringBuilder sb, string label, List<string> items)
        {
            sb.AppendLine("- " + label + " (" + items.Count + "):");
            foreach (string p in items)
                sb.AppendLine("    - " + p);
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

            /// <summary>Mode that drove the brief assembly.</summary>
            public ReflectionMode Mode { get; set; } = ReflectionMode.Consolidate;
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

            /// <summary>Mode the dispatched mission ran under.</summary>
            public ReflectionMode Mode { get; set; } = ReflectionMode.Consolidate;

            /// <summary>Whether dual-Judge review was requested for this dispatch.</summary>
            public bool DualJudge { get; set; }
        }

        #endregion
    }
}
