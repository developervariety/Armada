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
        private readonly HabitPatternMiner? _HabitPatternMiner;
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
            : this(database, admiral, settings, memory, packUsageMiner, null)
        {
        }

        /// <summary>
        /// Instantiate with explicit miners. v2-F2 wires both <see cref="PackUsageMiner"/>
        /// (pack-curate) and <see cref="HabitPatternMiner"/> (persona/captain-curate).
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="admiral">Admiral service.</param>
        /// <param name="settings">Armada settings.</param>
        /// <param name="memory">Reflection memory service.</param>
        /// <param name="packUsageMiner">Pack-usage miner; null disables pack-curate brief assembly.</param>
        /// <param name="habitPatternMiner">Habit-pattern miner; null disables persona/captain-curate brief assembly.</param>
        public ReflectionDispatcher(
            DatabaseDriver database,
            IAdmiralService admiral,
            ArmadaSettings settings,
            IReflectionMemoryService memory,
            PackUsageMiner? packUsageMiner,
            HabitPatternMiner? habitPatternMiner)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _PackUsageMiner = packUsageMiner;
            _HabitPatternMiner = habitPatternMiner;
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

            if (mode == ReflectionMode.PersonaCurate || mode == ReflectionMode.CaptainCurate)
            {
                // Identity-scope brief assembly does not pivot on a vessel; callers should use
                // BuildPersonaCurateBriefAsync / BuildCaptainCurateBriefAsync directly. Keep
                // the signature compatibility shim by returning an empty bundle so legacy
                // call sites do not crash.
                return new EvidenceBundleResult { Brief = "", EvidenceMissionCount = 0, Mode = mode };
            }

            if (mode == ReflectionMode.FleetCurate)
            {
                // Fleet-scope brief assembly does not pivot on a single vessel; callers should
                // use BuildFleetCurateBriefAsync(Fleet, ...) directly. Same compatibility shim
                // as the identity-scope branch above.
                return new EvidenceBundleResult { Brief = "", EvidenceMissionCount = 0, Mode = mode };
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

            // Cross-vessel suggestion pass (Reflections v2-F3 extension): scan every OTHER active
            // vessel in the same fleet for fact-strings whose 3-gram Jaccard similarity to the
            // current vessel's learned content or the new evidence bundle exceeds the
            // CrossVesselSuggestionThreshold; surface as a passive promotion-candidate hint.
            List<CrossVesselSuggestion> crossVesselSuggestions = mode == ReflectionMode.Consolidate || mode == ReflectionMode.ConsolidateAndReorganize
                ? await GatherCrossVesselSuggestionsAsync(vessel, learnedPlaybook, records, token).ConfigureAwait(false)
                : new List<CrossVesselSuggestion>();

            List<string> included = new List<string>(records);
            int skipped = 0;
            string brief = ComposeBrief(vessel, learnedPlaybook, included, rejectedProposalNotes, skipped, mode, recentCommitSubjects, crossVesselSuggestions);
            while (included.Count > 1 && brief.Length > maxChars)
            {
                included.RemoveAt(included.Count - 1);
                skipped++;
                brief = ComposeBrief(vessel, learnedPlaybook, included, rejectedProposalNotes, skipped, mode, recentCommitSubjects, crossVesselSuggestions);
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
        /// Build the persona-curate brief: persona-learned playbook content + cross-vessel
        /// mission-pattern aggregation produced by <see cref="HabitPatternMiner"/> +
        /// recently rejected persona-curate proposals (Reflections v2-F2).
        /// </summary>
        /// <param name="persona">Persona being curated.</param>
        /// <param name="tokenBudget">Approximate token budget for the brief.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Evidence bundle result; <see cref="EvidenceBundleResult.EvidenceMissionCount"/>
        ///   reflects the number of terminal missions aggregated by the miner.</returns>
        public async Task<EvidenceBundleResult> BuildPersonaCurateBriefAsync(
            Persona persona,
            int tokenBudget,
            CancellationToken token = default)
        {
            if (persona == null) throw new ArgumentNullException(nameof(persona));
            if (tokenBudget < 1) tokenBudget = _Settings.DefaultIdentityCurateTokenBudget;

            string playbookContent = await ReadPersonaLearnedPlaybookContentAsync(persona, token).ConfigureAwait(false);
            DateTime? sinceUtc = await ResolveLastAcceptedIdentityCurateAtAsync("persona-curate", persona.Name, token).ConfigureAwait(false);
            HabitPatternResult? aggregate = null;
            if (_HabitPatternMiner != null)
            {
                aggregate = await _HabitPatternMiner.MinePersonaAsync(persona.Name, sinceUtc, _Settings.IdentityCurateInitialWindow, token).ConfigureAwait(false);
            }

            List<string> rejections = await ReadIdentityRejectionsAsync("persona-curate", persona.Name, token).ConfigureAwait(false);
            string brief = ComposeIdentityCurateBrief(
                "persona-curate",
                persona.Name,
                playbookContent,
                aggregate,
                rejections);

            return new EvidenceBundleResult
            {
                Brief = brief,
                EvidenceMissionCount = aggregate?.MissionsExamined ?? 0,
                RejectedProposalCount = rejections.Count,
                Truncated = false,
                Mode = ReflectionMode.PersonaCurate
            };
        }

        /// <summary>
        /// Build the captain-curate brief: captain-learned playbook content (may be empty
        /// when no captain-curate has been accepted yet) + cross-vessel mission-pattern
        /// aggregation + recently rejected captain-curate proposals (Reflections v2-F2).
        /// </summary>
        /// <param name="captain">Captain being curated.</param>
        /// <param name="tokenBudget">Approximate token budget for the brief.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Evidence bundle result; <see cref="EvidenceBundleResult.EvidenceMissionCount"/>
        ///   reflects the number of terminal missions aggregated by the miner.</returns>
        public async Task<EvidenceBundleResult> BuildCaptainCurateBriefAsync(
            Captain captain,
            int tokenBudget,
            CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (tokenBudget < 1) tokenBudget = _Settings.DefaultIdentityCurateTokenBudget;

            string playbookContent = await ReadCaptainLearnedPlaybookContentAsync(captain, token).ConfigureAwait(false);
            DateTime? sinceUtc = await ResolveLastAcceptedIdentityCurateAtAsync("captain-curate", captain.Id, token).ConfigureAwait(false);
            HabitPatternResult? aggregate = null;
            if (_HabitPatternMiner != null)
            {
                aggregate = await _HabitPatternMiner.MineCaptainAsync(captain.Id, sinceUtc, _Settings.IdentityCurateInitialWindow, token).ConfigureAwait(false);
            }

            List<string> rejections = await ReadIdentityRejectionsAsync("captain-curate", captain.Id, token).ConfigureAwait(false);
            string brief = ComposeIdentityCurateBrief(
                "captain-curate",
                captain.Id,
                playbookContent,
                aggregate,
                rejections);

            return new EvidenceBundleResult
            {
                Brief = brief,
                EvidenceMissionCount = aggregate?.MissionsExamined ?? 0,
                RejectedProposalCount = rejections.Count,
                Truncated = false,
                Mode = ReflectionMode.CaptainCurate
            };
        }

        /// <summary>
        /// Search for an unfinished MemoryConsolidator mission for a specific persona
        /// (cross-vessel scope). Honest TOCTOU guard, same shape as the per-vessel check.
        /// </summary>
        /// <param name="personaName">Persona name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The in-flight mission when present, otherwise null.</returns>
        public async Task<Mission?> IsPersonaCurateInFlightAsync(string personaName, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(personaName)) throw new ArgumentNullException(nameof(personaName));

            return await FindIdentityCurateInFlightAsync("persona-curate", personaName, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Search for an unfinished MemoryConsolidator mission for a specific captain
        /// (cross-vessel scope). Honest TOCTOU guard.
        /// </summary>
        /// <param name="captainId">Captain id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The in-flight mission when present, otherwise null.</returns>
        public async Task<Mission?> IsCaptainCurateInFlightAsync(string captainId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));

            return await FindIdentityCurateInFlightAsync("captain-curate", captainId, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Search for an unfinished MemoryConsolidator mission for a specific fleet (Reflections
        /// v2-F3). Honest TOCTOU guard, same shape as the per-vessel and identity-scope checks.
        /// </summary>
        /// <param name="fleetId">Fleet id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The in-flight mission when present, otherwise null.</returns>
        public async Task<Mission?> IsFleetCurateInFlightAsync(string fleetId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(fleetId)) throw new ArgumentNullException(nameof(fleetId));

            return await FindIdentityCurateInFlightAsync("fleet-curate", fleetId, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Build the fleet-curate brief (Reflections v2-F3): current fleet-learned playbook
        /// content (empty placeholder until first accepted fleet-curate) + cross-vessel mission
        /// evidence aggregated by <see cref="HabitPatternMiner.MineFleetAsync"/> across all
        /// active vessels in the fleet + verbatim content of every active vessel's
        /// <c>vessel-&lt;repo&gt;-learned</c> playbook (the section 4 surface used to spot
        /// promotion candidates) + recently rejected fleet-curate proposals for this fleet.
        /// Active-vessel filter consistent with the M-fix1 audit-drain auto-dispatch fix.
        /// </summary>
        /// <param name="fleet">Fleet being curated.</param>
        /// <param name="tokenBudget">Approximate token budget for the brief.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Evidence bundle result; <see cref="EvidenceBundleResult.EvidenceMissionCount"/>
        ///   reflects the number of terminal missions aggregated across active vessels.</returns>
        public async Task<EvidenceBundleResult> BuildFleetCurateBriefAsync(
            Fleet fleet,
            int tokenBudget,
            CancellationToken token = default)
        {
            if (fleet == null) throw new ArgumentNullException(nameof(fleet));
            if (tokenBudget < 1) tokenBudget = _Settings.DefaultFleetCurateTokenBudget;

            string fleetPlaybookContent = await ReadFleetLearnedPlaybookContentAsync(fleet, token).ConfigureAwait(false);
            DateTime? sinceUtc = await ResolveLastAcceptedIdentityCurateAtAsync("fleet-curate", fleet.Id, token).ConfigureAwait(false);

            HabitPatternResult? aggregate = null;
            if (_HabitPatternMiner != null)
            {
                aggregate = await _HabitPatternMiner.MineFleetAsync(
                    fleet.Id,
                    sinceUtc,
                    _Settings.FleetCurateInitialWindow,
                    token).ConfigureAwait(false);
            }

            List<VesselLearnedSnapshot> vesselLearned = await ReadActiveVesselLearnedPlaybooksAsync(fleet, token).ConfigureAwait(false);
            List<string> rejections = await ReadIdentityRejectionsAsync("fleet-curate", fleet.Id, token).ConfigureAwait(false);

            string brief = ComposeFleetCurateBrief(
                fleet,
                fleetPlaybookContent,
                aggregate,
                vesselLearned,
                rejections);

            return new EvidenceBundleResult
            {
                Brief = brief,
                EvidenceMissionCount = aggregate?.MissionsExamined ?? 0,
                RejectedProposalCount = rejections.Count,
                Truncated = false,
                Mode = ReflectionMode.FleetCurate
            };
        }

        /// <summary>
        /// Dispatch a fleet-scoped MemoryConsolidator mission (Reflections v2-F3). The mission
        /// is created without a vesselId pin because the brief is fleet-wide; admiral picks an
        /// anchor vessel for worktree provisioning. Emits a <c>reflection.dispatched</c> event
        /// whose payload includes <c>targetType: "fleet"</c> and <c>targetId: fleet.Id</c>.
        /// </summary>
        /// <param name="fleet">Fleet being curated.</param>
        /// <param name="title">Mission title.</param>
        /// <param name="brief">Mission brief (assembled by <see cref="BuildFleetCurateBriefAsync"/>).</param>
        /// <param name="dualJudge">When true, dispatches the ReflectionsDualJudge pipeline.</param>
        /// <param name="tokenBudget">Token budget recorded in the dispatched event.</param>
        /// <param name="anchorVessel">Vessel used as the worktree pivot for the mission.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Dispatch result.</returns>
        public async Task<DispatchResult> DispatchFleetCurateAsync(
            Fleet fleet,
            string title,
            string brief,
            bool dualJudge,
            int tokenBudget,
            Vessel anchorVessel,
            CancellationToken token = default)
        {
            if (fleet == null) throw new ArgumentNullException(nameof(fleet));
            if (String.IsNullOrEmpty(title)) throw new ArgumentNullException(nameof(title));
            if (String.IsNullOrEmpty(brief)) throw new ArgumentNullException(nameof(brief));
            if (anchorVessel == null) throw new ArgumentNullException(nameof(anchorVessel));

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
                anchorVessel.Id,
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
                throw new InvalidOperationException("Fleet-curate dispatch created no mission");

            await EmitFleetCurateDispatchedEventAsync(reflectionMission, anchorVessel, fleet.Id, dualJudge, tokenBudget, token).ConfigureAwait(false);

            return new DispatchResult
            {
                MissionId = reflectionMission.Id,
                VoyageId = voyage.Id,
                Mode = ReflectionMode.FleetCurate,
                DualJudge = dualJudge
            };
        }

        /// <summary>
        /// Dispatch an identity-scoped MemoryConsolidator mission (persona-curate or captain-
        /// curate). The mission is created without a vesselId pin because the brief is cross-
        /// vessel; admiral's voyage-creation path will pick a representative vessel for
        /// worktree provisioning. Emits a <c>reflection.dispatched</c> event whose payload
        /// includes <c>targetType</c> + <c>targetId</c> for downstream filtering.
        /// </summary>
        /// <param name="mode">Mode (persona-curate or captain-curate).</param>
        /// <param name="targetId">Persona name or captain id.</param>
        /// <param name="title">Mission title.</param>
        /// <param name="brief">Mission brief (assembled by Build*CurateBriefAsync).</param>
        /// <param name="dualJudge">When true, dispatches the ReflectionsDualJudge pipeline.</param>
        /// <param name="tokenBudget">Token budget recorded in the dispatched event.</param>
        /// <param name="anchorVessel">Vessel used as the worktree pivot for the mission.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Dispatch result.</returns>
        public async Task<DispatchResult> DispatchIdentityCurateAsync(
            ReflectionMode mode,
            string targetId,
            string title,
            string brief,
            bool dualJudge,
            int tokenBudget,
            Vessel anchorVessel,
            CancellationToken token = default)
        {
            if (mode != ReflectionMode.PersonaCurate && mode != ReflectionMode.CaptainCurate)
                throw new ArgumentOutOfRangeException(nameof(mode), "DispatchIdentityCurateAsync requires PersonaCurate or CaptainCurate");
            if (String.IsNullOrEmpty(targetId)) throw new ArgumentNullException(nameof(targetId));
            if (String.IsNullOrEmpty(title)) throw new ArgumentNullException(nameof(title));
            if (String.IsNullOrEmpty(brief)) throw new ArgumentNullException(nameof(brief));
            if (anchorVessel == null) throw new ArgumentNullException(nameof(anchorVessel));

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
                anchorVessel.Id,
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
                throw new InvalidOperationException("Identity-curate dispatch created no mission");

            await EmitIdentityDispatchedEventAsync(reflectionMission, anchorVessel, mode, targetId, dualJudge, tokenBudget, token).ConfigureAwait(false);

            return new DispatchResult
            {
                MissionId = reflectionMission.Id,
                VoyageId = voyage.Id,
                Mode = mode,
                DualJudge = dualJudge
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
        /// After an audit queue drain, evaluates the persona-level CurateThreshold and
        /// anti-thrash and dispatches a persona-curate reflection when due (Reflections v2-F2).
        /// Anti-thrash: skip when the most recent persona-curate accept for this persona had
        /// at least one [high]-confidence note and no terminal mission since then has surfaced
        /// new failure-mode tags or a fresh judge FAIL/NEEDS_REVISION.
        /// </summary>
        /// <param name="persona">Persona to evaluate.</param>
        /// <param name="anchorVessel">Vessel used as the worktree pivot if a dispatch fires.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Dispatch result when a persona-curate mission was created; otherwise null.</returns>
        public async Task<DispatchResult?> TryAutoDispatchPersonaCurateAfterAuditDrainAsync(
            Persona persona,
            Vessel anchorVessel,
            CancellationToken token = default)
        {
            if (persona == null) throw new ArgumentNullException(nameof(persona));
            if (anchorVessel == null) throw new ArgumentNullException(nameof(anchorVessel));
            if (!persona.CurateThreshold.HasValue) return null;
            int threshold = persona.CurateThreshold.Value;
            if (threshold <= 0) return null;

            DateTime? sinceUtc = await ResolveLastAcceptedIdentityCurateAtAsync("persona-curate", persona.Name, token).ConfigureAwait(false);
            int countSince = await CountTerminalMissionsForPersonaSinceAsync(persona.Name, sinceUtc, token).ConfigureAwait(false);
            if (countSince < threshold) return null;

            Mission? inFlight = await IsPersonaCurateInFlightAsync(persona.Name, token).ConfigureAwait(false);
            if (inFlight != null) return null;

            // Anti-thrash for identity-curate: re-mine and require either a fresh failure tag
            // or a judge FAIL/NEEDS_REVISION since last accept; otherwise skip.
            if (_HabitPatternMiner != null && sinceUtc.HasValue)
            {
                HabitPatternResult result = await _HabitPatternMiner.MinePersonaAsync(persona.Name, sinceUtc, _Settings.IdentityCurateInitialWindow, token).ConfigureAwait(false);
                if (result.MissionsExamined == 0) return null;
                bool freshSignal = result.FailureModeTags.Count > 0 || result.JudgeFailCount > 0 || result.JudgeNeedsRevisionCount > 0;
                if (!freshSignal) return null;
            }

            EvidenceBundleResult bundle = await BuildPersonaCurateBriefAsync(persona, _Settings.DefaultIdentityCurateTokenBudget, token).ConfigureAwait(false);
            return await DispatchIdentityCurateAsync(
                ReflectionMode.PersonaCurate,
                persona.Name,
                "Curate persona-learned notes for " + persona.Name,
                bundle.Brief,
                false,
                _Settings.DefaultIdentityCurateTokenBudget,
                anchorVessel,
                token).ConfigureAwait(false);
        }

        /// <summary>
        /// After an audit queue drain, evaluates the captain-level CurateThreshold and
        /// anti-thrash and dispatches a captain-curate reflection when due (Reflections v2-F2).
        /// </summary>
        /// <param name="captain">Captain to evaluate.</param>
        /// <param name="anchorVessel">Vessel used as the worktree pivot if a dispatch fires.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Dispatch result when a captain-curate mission was created; otherwise null.</returns>
        public async Task<DispatchResult?> TryAutoDispatchCaptainCurateAfterAuditDrainAsync(
            Captain captain,
            Vessel anchorVessel,
            CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (anchorVessel == null) throw new ArgumentNullException(nameof(anchorVessel));
            if (!captain.CurateThreshold.HasValue) return null;
            int threshold = captain.CurateThreshold.Value;
            if (threshold <= 0) return null;

            DateTime? sinceUtc = await ResolveLastAcceptedIdentityCurateAtAsync("captain-curate", captain.Id, token).ConfigureAwait(false);
            int countSince = await CountTerminalMissionsForCaptainSinceAsync(captain.Id, sinceUtc, token).ConfigureAwait(false);
            if (countSince < threshold) return null;

            Mission? inFlight = await IsCaptainCurateInFlightAsync(captain.Id, token).ConfigureAwait(false);
            if (inFlight != null) return null;

            if (_HabitPatternMiner != null && sinceUtc.HasValue)
            {
                HabitPatternResult result = await _HabitPatternMiner.MineCaptainAsync(captain.Id, sinceUtc, _Settings.IdentityCurateInitialWindow, token).ConfigureAwait(false);
                if (result.MissionsExamined == 0) return null;
                bool freshSignal = result.FailureModeTags.Count > 0 || result.JudgeFailCount > 0 || result.JudgeNeedsRevisionCount > 0;
                if (!freshSignal) return null;
            }

            EvidenceBundleResult bundle = await BuildCaptainCurateBriefAsync(captain, _Settings.DefaultIdentityCurateTokenBudget, token).ConfigureAwait(false);
            return await DispatchIdentityCurateAsync(
                ReflectionMode.CaptainCurate,
                captain.Id,
                "Curate captain-learned notes for " + captain.Id,
                bundle.Brief,
                false,
                _Settings.DefaultIdentityCurateTokenBudget,
                anchorVessel,
                token).ConfigureAwait(false);
        }

        /// <summary>
        /// After an audit queue drain, evaluates the fleet-level <see cref="Fleet.CurateThreshold"/>
        /// + anti-thrash and dispatches a fleet-curate reflection when due (Reflections v2-F3).
        /// Anti-thrash for fleet-curate is keyed on FLEET ACTIVITY rather than mission count
        /// alone: skip when the most recent vessel-curate (consolidate) accept on any member
        /// vessel happened BEFORE the last accepted fleet-curate -- the consolidator's
        /// promotion candidates come from vessel-learned playbooks, so without fresh
        /// vessel-learned acceptances there are no new shared facts to discover.
        /// </summary>
        /// <param name="fleet">Fleet to evaluate.</param>
        /// <param name="anchorVessel">Vessel used as the worktree pivot if a dispatch fires.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Dispatch result when a fleet-curate mission was created; otherwise null.</returns>
        public async Task<DispatchResult?> TryAutoDispatchFleetCurateAfterAuditDrainAsync(
            Fleet fleet,
            Vessel anchorVessel,
            CancellationToken token = default)
        {
            if (fleet == null) throw new ArgumentNullException(nameof(fleet));
            if (anchorVessel == null) throw new ArgumentNullException(nameof(anchorVessel));
            if (!fleet.CurateThreshold.HasValue) return null;
            int threshold = fleet.CurateThreshold.Value;
            if (threshold <= 0) return null;

            DateTime? sinceUtc = await ResolveLastAcceptedIdentityCurateAtAsync("fleet-curate", fleet.Id, token).ConfigureAwait(false);

            // Threshold: count terminal missions across all ACTIVE vessels in the fleet since
            // the last accepted fleet-curate. Active-vessel filter consistent with M-fix1.
            int countSince = await CountTerminalMissionsForFleetSinceAsync(fleet.Id, sinceUtc, token).ConfigureAwait(false);
            if (countSince < threshold) return null;

            Mission? inFlight = await IsFleetCurateInFlightAsync(fleet.Id, token).ConfigureAwait(false);
            if (inFlight != null) return null;

            // Anti-thrash: when a fleet-curate has accepted before, require a vessel-curate
            // (consolidate) accept on any member vessel SINCE that timestamp; otherwise
            // promotion candidates haven't moved.
            if (sinceUtc.HasValue)
            {
                bool hasFreshVesselCurate = await AnyVesselCurateAcceptedSinceAsync(fleet.Id, sinceUtc.Value, token).ConfigureAwait(false);
                if (!hasFreshVesselCurate) return null;
            }

            EvidenceBundleResult bundle = await BuildFleetCurateBriefAsync(fleet, _Settings.DefaultFleetCurateTokenBudget, token).ConfigureAwait(false);
            if (bundle.EvidenceMissionCount == 0 && bundle.RejectedProposalCount == 0) return null;

            return await DispatchFleetCurateAsync(
                fleet,
                "Curate fleet-learned facts for " + fleet.Name,
                bundle.Brief,
                false,
                _Settings.DefaultFleetCurateTokenBudget,
                anchorVessel,
                token).ConfigureAwait(false);
        }

        private async Task<int> CountTerminalMissionsForFleetSinceAsync(string fleetId, DateTime? sinceUtc, CancellationToken token)
        {
            List<Vessel> vessels = await _Database.Vessels.EnumerateByFleetAsync(fleetId, token).ConfigureAwait(false);
            HashSet<string> activeIds = new HashSet<string>(
                vessels.Where(v => v.Active).Select(v => v.Id),
                StringComparer.Ordinal);
            if (activeIds.Count == 0) return 0;

            List<Mission> all = await _Database.Missions.EnumerateAsync(token).ConfigureAwait(false);
            int count = 0;
            foreach (Mission m in all)
            {
                if (!IsTerminal(m.Status)) continue;
                if (String.IsNullOrEmpty(m.VesselId) || !activeIds.Contains(m.VesselId!)) continue;
                if (sinceUtc.HasValue && (!m.CompletedUtc.HasValue || m.CompletedUtc.Value <= sinceUtc.Value)) continue;
                count++;
            }
            return count;
        }

        private async Task<bool> AnyVesselCurateAcceptedSinceAsync(string fleetId, DateTime sinceUtc, CancellationToken token)
        {
            List<Vessel> vessels = await _Database.Vessels.EnumerateByFleetAsync(fleetId, token).ConfigureAwait(false);
            HashSet<string> activeIds = new HashSet<string>(
                vessels.Where(v => v.Active).Select(v => v.Id),
                StringComparer.Ordinal);
            if (activeIds.Count == 0) return false;

            EnumerationQuery query = new EnumerationQuery
            {
                EventType = "reflection.accepted",
                PageNumber = 1,
                PageSize = 200
            };
            EnumerationResult<ArmadaEvent> page = await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false);
            foreach (ArmadaEvent evt in page.Objects)
            {
                if (evt.CreatedUtc <= sinceUtc) continue;
                if (String.IsNullOrEmpty(evt.Payload)) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(evt.Payload);
                    JsonElement root = doc.RootElement;
                    if (!root.TryGetProperty("mode", out JsonElement modeEl)
                        || modeEl.ValueKind != JsonValueKind.String) continue;
                    string? modeStr = modeEl.GetString();
                    if (!String.Equals(modeStr, "consolidate", StringComparison.OrdinalIgnoreCase)
                        && !String.Equals(modeStr, "consolidate-and-reorganize", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!String.IsNullOrEmpty(evt.VesselId) && activeIds.Contains(evt.VesselId!))
                    {
                        return true;
                    }
                }
                catch (JsonException) { continue; }
            }
            return false;
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
            List<string> recentCommitSubjects,
            List<CrossVesselSuggestion> crossVesselSuggestions)
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

            if (crossVesselSuggestions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## CROSS-VESSEL SUGGESTION (passive hint)");
                sb.AppendLine("The following facts appear in OTHER vessels' learned playbooks AND look related to the work covered by this curate.");
                sb.AppendLine("Consider whether any of them belong at fleet scope rather than vessel scope -- you don't have to act on this; it's a heads-up.");
                sb.AppendLine("If a pattern looks fleet-worthy, mention it in your diff `notes` paragraph or include a [CLAUDE.MD-PROPOSAL] block in your final report suggesting a fleet-curate run; the orchestrator decides.");
                foreach (CrossVesselSuggestion s in crossVesselSuggestions)
                {
                    sb.AppendLine("- From vessel-" + s.VesselName + "-learned: " + s.SuggestedFact);
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

        private async Task<string> ReadPersonaLearnedPlaybookContentAsync(Persona persona, CancellationToken token)
        {
            string tenantId = !String.IsNullOrEmpty(persona.TenantId) ? persona.TenantId! : Constants.DefaultTenantId;
            string fileName = "persona-" + SanitizeIdentityName(persona.Name) + "-learned.md";
            Playbook? playbook = await _Database.Playbooks.ReadByFileNameAsync(tenantId, fileName, token).ConfigureAwait(false);
            return playbook?.Content ?? "# Persona Learned Notes -- " + persona.Name + "\n\nNo accepted persona-curate notes yet.";
        }

        private async Task<string> ReadCaptainLearnedPlaybookContentAsync(Captain captain, CancellationToken token)
        {
            string tenantId = !String.IsNullOrEmpty(captain.TenantId) ? captain.TenantId! : Constants.DefaultTenantId;
            string fileName = "captain-" + SanitizeIdentityName(captain.Id) + "-learned.md";
            Playbook? playbook = await _Database.Playbooks.ReadByFileNameAsync(tenantId, fileName, token).ConfigureAwait(false);
            return playbook?.Content ?? "# Captain Learned Notes -- " + captain.Id + "\n\nNo accepted captain-curate notes yet.";
        }

        private async Task<string> ReadFleetLearnedPlaybookContentAsync(Fleet fleet, CancellationToken token)
        {
            // Prefer the explicit FleetId-pinned playbook when set; fall back to the lazy
            // filename probe so a deleted playbook id (CLAUDE.md spec failure-mode #11) still
            // resolves to a sensible empty placeholder.
            if (!String.IsNullOrEmpty(fleet.LearnedPlaybookId))
            {
                Playbook? byId = await _Database.Playbooks.ReadAsync(fleet.LearnedPlaybookId!, token).ConfigureAwait(false);
                if (byId != null) return byId.Content;
            }

            string tenantId = !String.IsNullOrEmpty(fleet.TenantId) ? fleet.TenantId! : Constants.DefaultTenantId;
            string fileName = "fleet-" + SanitizeIdentityName(fleet.Id) + "-learned.md";
            Playbook? playbook = await _Database.Playbooks.ReadByFileNameAsync(tenantId, fileName, token).ConfigureAwait(false);
            return playbook?.Content ?? "# Fleet Learned Notes -- " + fleet.Name + "\n\nNo accepted fleet-curate notes yet.";
        }

        private async Task<List<CrossVesselSuggestion>> GatherCrossVesselSuggestionsAsync(
            Vessel currentVessel,
            string currentLearnedPlaybook,
            List<string> evidenceRecords,
            CancellationToken token)
        {
            List<CrossVesselSuggestion> suggestions = new List<CrossVesselSuggestion>();
            if (String.IsNullOrEmpty(currentVessel.FleetId)) return suggestions;

            double threshold = _Settings.CrossVesselSuggestionThreshold;
            string evidenceJoined = String.Join("\n", evidenceRecords);

            List<Vessel> siblings;
            try
            {
                siblings = await _Database.Vessels.EnumerateByFleetAsync(currentVessel.FleetId!, token).ConfigureAwait(false);
            }
            catch
            {
                return suggestions;
            }

            HashSet<string> seenFacts = new HashSet<string>(StringComparer.Ordinal);
            foreach (Vessel sibling in siblings)
            {
                if (!sibling.Active) continue;
                if (String.Equals(sibling.Id, currentVessel.Id, StringComparison.Ordinal)) continue;

                string siblingContent;
                try
                {
                    siblingContent = await _Memory.ReadLearnedPlaybookContentAsync(sibling, token).ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }
                if (String.IsNullOrEmpty(siblingContent)) continue;

                foreach (string line in siblingContent.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length < 30) continue;
                    if (trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
                    if (trimmed.StartsWith(">", StringComparison.Ordinal)) continue;
                    if (trimmed.StartsWith("Source:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (seenFacts.Contains(trimmed)) continue;

                    double simToCurrent = HabitPatternMiner.Jaccard3GramSimilarity(trimmed, currentLearnedPlaybook);
                    double simToEvidence = HabitPatternMiner.Jaccard3GramSimilarity(trimmed, evidenceJoined);
                    double sim = Math.Max(simToCurrent, simToEvidence);
                    if (sim < threshold) continue;

                    seenFacts.Add(trimmed);
                    suggestions.Add(new CrossVesselSuggestion
                    {
                        VesselId = sibling.Id,
                        VesselName = sibling.Name,
                        SuggestedFact = trimmed.Length > 240 ? trimmed.Substring(0, 240) + "..." : trimmed,
                        Similarity = sim
                    });
                    if (suggestions.Count >= 8) return suggestions;
                }
            }
            return suggestions;
        }

        private async Task<List<VesselLearnedSnapshot>> ReadActiveVesselLearnedPlaybooksAsync(Fleet fleet, CancellationToken token)
        {
            List<VesselLearnedSnapshot> result = new List<VesselLearnedSnapshot>();
            List<Vessel> vessels = await _Database.Vessels.EnumerateByFleetAsync(fleet.Id, token).ConfigureAwait(false);
            foreach (Vessel vessel in vessels)
            {
                if (!vessel.Active) continue;
                string content = await _Memory.ReadLearnedPlaybookContentAsync(vessel, token).ConfigureAwait(false);
                result.Add(new VesselLearnedSnapshot
                {
                    VesselId = vessel.Id,
                    VesselName = vessel.Name,
                    LearnedPlaybookContent = content
                });
            }
            return result;
        }

        /// <summary>Sanitize a persona name or captain id into a learned-playbook filename segment.</summary>
        /// <param name="raw">Raw identity string.</param>
        /// <returns>Sanitized lowercase kebab-case form.</returns>
        public static string SanitizeIdentityName(string raw)
        {
            if (String.IsNullOrEmpty(raw)) return "unknown";
            string lower = raw.ToLowerInvariant();
            string replaced = System.Text.RegularExpressions.Regex.Replace(lower, "[^a-z0-9]+", "-");
            string trimmed = replaced.Trim('-');
            return String.IsNullOrEmpty(trimmed) ? "unknown" : trimmed;
        }

        private async Task<int> CountTerminalMissionsForPersonaSinceAsync(string personaName, DateTime? sinceUtc, CancellationToken token)
        {
            List<Mission> all = await _Database.Missions.EnumerateAsync(token).ConfigureAwait(false);
            int count = 0;
            foreach (Mission m in all)
            {
                if (!IsTerminal(m.Status)) continue;
                if (!String.Equals(m.Persona, personaName, StringComparison.OrdinalIgnoreCase)) continue;
                if (sinceUtc.HasValue && (!m.CompletedUtc.HasValue || m.CompletedUtc.Value <= sinceUtc.Value)) continue;
                count++;
            }
            return count;
        }

        private async Task<int> CountTerminalMissionsForCaptainSinceAsync(string captainId, DateTime? sinceUtc, CancellationToken token)
        {
            List<Mission> all = await _Database.Missions.EnumerateAsync(token).ConfigureAwait(false);
            int count = 0;
            foreach (Mission m in all)
            {
                if (!IsTerminal(m.Status)) continue;
                if (!String.Equals(m.CaptainId, captainId, StringComparison.Ordinal)) continue;
                if (sinceUtc.HasValue && (!m.CompletedUtc.HasValue || m.CompletedUtc.Value <= sinceUtc.Value)) continue;
                count++;
            }
            return count;
        }

        private async Task<DateTime?> ResolveLastAcceptedIdentityCurateAtAsync(string mode, string targetId, CancellationToken token)
        {
            EnumerationQuery query = new EnumerationQuery
            {
                EventType = "reflection.accepted",
                PageNumber = 1,
                PageSize = 100
            };
            EnumerationResult<ArmadaEvent> page = await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false);
            foreach (ArmadaEvent evt in page.Objects.OrderByDescending(e => e.CreatedUtc))
            {
                if (String.IsNullOrEmpty(evt.Payload)) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(evt.Payload);
                    JsonElement root = doc.RootElement;
                    if (!root.TryGetProperty("mode", out JsonElement modeEl)
                        || modeEl.ValueKind != JsonValueKind.String
                        || !String.Equals(modeEl.GetString(), mode, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!root.TryGetProperty("targetId", out JsonElement tEl)
                        || tEl.ValueKind != JsonValueKind.String
                        || !String.Equals(tEl.GetString(), targetId, StringComparison.Ordinal))
                        continue;

                    return evt.CreatedUtc;
                }
                catch (JsonException) { continue; }
            }
            return null;
        }

        private async Task<List<string>> ReadIdentityRejectionsAsync(string mode, string targetId, CancellationToken token)
        {
            EnumerationQuery query = new EnumerationQuery
            {
                EventType = "reflection.rejected",
                PageNumber = 1,
                PageSize = 100
            };
            EnumerationResult<ArmadaEvent> page = await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false);
            List<string> notes = new List<string>();
            foreach (ArmadaEvent evt in page.Objects.OrderByDescending(e => e.CreatedUtc))
            {
                if (String.IsNullOrEmpty(evt.Payload)) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(evt.Payload);
                    JsonElement root = doc.RootElement;
                    if (!root.TryGetProperty("mode", out JsonElement modeEl)
                        || modeEl.ValueKind != JsonValueKind.String
                        || !String.Equals(modeEl.GetString(), mode, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!root.TryGetProperty("targetId", out JsonElement tEl)
                        || tEl.ValueKind != JsonValueKind.String
                        || !String.Equals(tEl.GetString(), targetId, StringComparison.Ordinal))
                        continue;

                    string missionId = root.TryGetProperty("missionId", out JsonElement mEl) && mEl.ValueKind == JsonValueKind.String
                        ? mEl.GetString() ?? "" : "";
                    string reason = root.TryGetProperty("reason", out JsonElement rEl) && rEl.ValueKind == JsonValueKind.String
                        ? rEl.GetString() ?? "" : "";
                    notes.Add("missionId: " + missionId + " -- " + reason);
                }
                catch (JsonException) { continue; }
            }
            return notes;
        }

        private async Task<Mission?> FindIdentityCurateInFlightAsync(string mode, string targetId, CancellationToken token)
        {
            EnumerationQuery query = new EnumerationQuery
            {
                EventType = "reflection.dispatched",
                PageNumber = 1,
                PageSize = 200
            };
            EnumerationResult<ArmadaEvent> page = await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false);
            foreach (ArmadaEvent evt in page.Objects.OrderByDescending(e => e.CreatedUtc))
            {
                if (String.IsNullOrEmpty(evt.Payload) || String.IsNullOrEmpty(evt.MissionId)) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(evt.Payload);
                    JsonElement root = doc.RootElement;
                    if (!root.TryGetProperty("mode", out JsonElement modeEl)
                        || modeEl.ValueKind != JsonValueKind.String
                        || !String.Equals(modeEl.GetString(), mode, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!root.TryGetProperty("targetId", out JsonElement tEl)
                        || tEl.ValueKind != JsonValueKind.String
                        || !String.Equals(tEl.GetString(), targetId, StringComparison.Ordinal))
                        continue;
                }
                catch (JsonException) { continue; }

                Mission? mission = await _Database.Missions.ReadAsync(evt.MissionId!, token).ConfigureAwait(false);
                if (mission == null) continue;
                if (IsTerminal(mission.Status)) continue;
                if (!String.Equals(mission.Persona, "MemoryConsolidator", StringComparison.OrdinalIgnoreCase)) continue;
                return mission;
            }
            return null;
        }

        private async Task EmitIdentityDispatchedEventAsync(
            Mission mission,
            Vessel anchorVessel,
            ReflectionMode mode,
            string targetId,
            bool dualJudge,
            int tokenBudget,
            CancellationToken token)
        {
            try
            {
                ArmadaEvent dispatched = new ArmadaEvent(_ReflectionDispatchedEvent, "Identity-curate reflection dispatched.");
                dispatched.TenantId = mission.TenantId ?? anchorVessel.TenantId ?? Constants.DefaultTenantId;
                dispatched.EntityType = "mission";
                dispatched.EntityId = mission.Id;
                dispatched.MissionId = mission.Id;
                dispatched.VesselId = anchorVessel.Id;
                dispatched.VoyageId = mission.VoyageId;
                dispatched.Payload = JsonSerializer.Serialize(new
                {
                    mode = ReflectionMemoryService.ModeToWireString(mode),
                    dualJudge = dualJudge,
                    tokenBudget = tokenBudget,
                    missionId = mission.Id,
                    targetType = mode == ReflectionMode.PersonaCurate ? "persona" : "captain",
                    targetId = targetId
                });
                await _Database.Events.CreateAsync(dispatched, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort observability.
            }
        }

        private static string ComposeIdentityCurateBrief(
            string modeWire,
            string targetId,
            string playbookContent,
            HabitPatternResult? aggregate,
            List<string> rejections)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Persona: MemoryConsolidator");
            sb.AppendLine("Pipeline: Reflections");
            sb.AppendLine("PreferredModel: high");
            sb.AppendLine("Title: Curate identity-learned notes for " + targetId);
            sb.AppendLine("Mode: " + modeWire);
            sb.AppendLine("Target: " + targetId);
            sb.AppendLine();

            sb.AppendLine("## CURRENT IDENTITY-LEARNED NOTES");
            sb.AppendLine(playbookContent);
            sb.AppendLine();

            sb.AppendLine("## INSTRUCTIONS (" + modeWire + "-specific)");
            sb.AppendLine("Permitted operations:");
            sb.AppendLine("- Add new notes about identity-wide patterns observed across the missions in section 3.");
            sb.AppendLine("- Modify existing notes when evidence shows the pattern has changed (update the [confidence] tag).");
            sb.AppendLine("- Disable existing notes when evidence shows the pattern no longer holds (the bias-correction loop).");
            sb.AppendLine();
            sb.AppendLine("Forbidden operations:");
            if (modeWire == "persona-curate")
            {
                sb.AppendLine("- Do NOT name a specific captain id. If a pattern only applies to one captain, that is captain-curate territory.");
                sb.AppendLine("- Do NOT add notes derived from fewer than 3 missions or fewer than 2 captains.");
            }
            else
            {
                sb.AppendLine("- Do NOT make absolute routing claims. Phrase preferences as biases the admiral's tier-routing may consider.");
            }
            sb.AppendLine("- Do NOT propose CLAUDE.md edits or code changes.");
            sb.AppendLine("- Do NOT propose vessel-pinned facts in this mode -- the per-vessel learned-facts playbook is a separate store curated by mode=consolidate.");
            sb.AppendLine();
            sb.AppendLine("Confidence guidance:");
            sb.AppendLine("- high: pattern observed in >=5 missions across >=3 captains (persona) or in >=5 missions for the captain (captain).");
            sb.AppendLine("- medium: 3-4 missions across >=2 captains (persona) or 3-4 missions for the captain.");
            sb.AppendLine("- low: not allowed for persona-curate (rejected as persona_note_confidence_too_low). Permitted for captain-curate at first observation, but flag for re-confirmation next cycle.");
            sb.AppendLine();
            sb.AppendLine("Re-evaluation handling (REQUIRED):");
            sb.AppendLine("- Each existing note in section 1 has a `Source: msn_*` line listing supporting missions. If section 3 contains contradicting evidence for any of those missions, you MUST emit a `disabled` or `modified` entry in the diff for that note. Failure to address counter-evidence is rejected at accept time as " + (modeWire == "persona-curate" ? "persona_curate_ignored_counter_evidence" : "captain_curate_ignored_counter_evidence") + ".");

            sb.AppendLine();
            sb.AppendLine("## CROSS-VESSEL EVIDENCE BUNDLE");
            if (aggregate == null)
            {
                sb.AppendLine("HabitPatternMiner unavailable -- no aggregated evidence bundled.");
            }
            else
            {
                sb.AppendLine("- missionsExamined: " + aggregate.MissionsExamined);
                sb.AppendLine("- complete: " + aggregate.MissionsComplete + ", failed: " + aggregate.MissionsFailed + ", cancelled: " + aggregate.MissionsCancelled);
                sb.AppendLine("- judgeVerdicts: PASS=" + aggregate.JudgePassCount + ", FAIL=" + aggregate.JudgeFailCount + ", NEEDS_REVISION=" + aggregate.JudgeNeedsRevisionCount + ", PENDING=" + aggregate.JudgePendingCount);
                sb.AppendLine("- averageRecoveryAttempts: " + aggregate.AverageRecoveryAttempts.ToString("0.00"));
                sb.AppendLine("- signalMailReceivedTotal: " + aggregate.SignalMailReceivedTotal);

                if (aggregate.FailureModeTags.Count > 0)
                {
                    sb.AppendLine("- failureModeTags:");
                    foreach (FailureModeTagCount tag in aggregate.FailureModeTags)
                        sb.AppendLine("    - " + tag.Tag + ": " + tag.MissionCount);
                }

                if (aggregate.TopTouchedFiles.Count > 0)
                {
                    sb.AppendLine("- topTouchedFiles:");
                    foreach (FileTouchCount f in aggregate.TopTouchedFiles)
                        sb.AppendLine("    - " + f.Path + " (" + f.EditCount + ")");
                }

                if (aggregate.CaptainContributions.Count > 0)
                {
                    sb.AppendLine("- captainContributions:");
                    foreach (CaptainContribution c in aggregate.CaptainContributions)
                    {
                        sb.AppendLine("    - " + c.CaptainId
                            + " runtime=" + (c.Runtime ?? "")
                            + " model=" + (c.Model ?? "")
                            + " missions=" + c.MissionCount
                            + " complete=" + c.CompleteCount
                            + " failed=" + c.FailedCount
                            + " cancelled=" + c.CancelledCount);
                    }
                }

                if (aggregate.PersonaRoleDistribution.Count > 0)
                {
                    sb.AppendLine("- personaRoleDistribution:");
                    foreach (PersonaRoleCount p in aggregate.PersonaRoleDistribution)
                        sb.AppendLine("    - " + p.PersonaName + ": " + p.MissionCount);
                }
            }

            sb.AppendLine();
            sb.AppendLine("## RECENTLY REJECTED " + modeWire.ToUpperInvariant() + " PROPOSALS");
            if (rejections.Count == 0)
            {
                sb.AppendLine("No rejected " + modeWire + " proposals recorded for this target.");
            }
            else
            {
                foreach (string note in rejections)
                    sb.AppendLine("- " + note);
            }

            sb.AppendLine();
            sb.AppendLine("## CONSTRAINTS");
            sb.AppendLine("- Output must be exactly two fenced blocks named reflections-candidate and reflections-diff.");
            sb.AppendLine("- The reflections-candidate block is markdown identity-pinned learned-notes content; each note has a `[high]/[medium]/[low]` confidence tag and a `Source: msn_xxx` attribution line.");
            sb.AppendLine("- The reflections-diff block is JSON: {\"added\":[{section,summary,confidence}], \"modified\":[{noteRef,change,supportingMissions}], \"disabled\":[{noteRef,reason}], \"evidenceConfidence\":\"high|mixed|low\", \"missionsExamined\":N" + (modeWire == "persona-curate" ? ", \"captainsInScope\":N" : "") + ", \"notes\":\"one paragraph\"}.");
            sb.AppendLine("- Never propose CLAUDE.md edits or code changes.");

            return sb.ToString();
        }

        private static string ComposeFleetCurateBrief(
            Fleet fleet,
            string fleetPlaybookContent,
            HabitPatternResult? aggregate,
            List<VesselLearnedSnapshot> vesselLearned,
            List<string> rejections)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Persona: MemoryConsolidator");
            sb.AppendLine("Pipeline: Reflections");
            sb.AppendLine("PreferredModel: high");
            sb.AppendLine("Title: Curate fleet-learned facts for " + fleet.Name);
            sb.AppendLine("Mode: fleet-curate");
            sb.AppendLine("Target: fleet = " + fleet.Id);
            sb.AppendLine();

            sb.AppendLine("## 1. CURRENT FLEET-LEARNED PLAYBOOK");
            sb.AppendLine(fleetPlaybookContent);
            sb.AppendLine();

            sb.AppendLine("## 2. INSTRUCTIONS (fleet-curate-specific)");
            sb.AppendLine("Permitted operations:");
            sb.AppendLine("- Add new fleet-scope facts that apply across multiple vessels in this fleet, backed by evidence in section 3 + section 4.");
            sb.AppendLine("- Modify existing fleet entries (re-evaluation: weaken confidence, refresh attribution, update wording).");
            sb.AppendLine("- Disable existing fleet entries when evidence shows the fact no longer holds across vessels.");
            sb.AppendLine();
            sb.AppendLine("Permitted ripple actions on vessel scopes:");
            sb.AppendLine("- For each promoted fact, list disableFromVessels entries specifying which vessel-learned playbook entries to deactivate (avoids duplicate content when a fact lives at fleet scope).");
            sb.AppendLine();
            sb.AppendLine("Forbidden operations:");
            sb.AppendLine("- No fact promoted with <2 contributing vessels. Single-vessel facts belong in vessel-curate.");
            sb.AppendLine("- No fact promoted with <3 supporting missions across the contributing vessels.");
            sb.AppendLine("- No fact that contradicts existing vessel-learned content (the accept tool BLOCKS this with strict validation; do not even propose).");
            sb.AppendLine("- No CLAUDE.md edits, no code changes, no playbook proposals beyond fleet-<id>-learned and the explicit disableFromVessels ripple.");
            sb.AppendLine();
            sb.AppendLine("Confidence guidance:");
            sb.AppendLine("- high -- pattern observed in >=3 vessels with >=5 supporting missions total.");
            sb.AppendLine("- medium -- 2 vessels, 3-4 missions.");
            sb.AppendLine("- low -- only 1 vessel meaningfully contributes (this is vessel-curate territory; reject).");
            sb.AppendLine();
            sb.AppendLine("Re-evaluation handling (REQUIRED):");
            sb.AppendLine("- Each existing fleet entry in section 1 includes attribution. If recent vessel evidence (section 3) contradicts an entry on any vessel, propose disabling or weakening. If a vessel that used to contribute no longer does, update attribution accordingly. Failure to address counter-evidence is rejected as fleet_curate_ignored_counter_evidence at accept time.");
            sb.AppendLine();

            sb.AppendLine("## 3. CROSS-VESSEL MISSION EVIDENCE BUNDLE");
            if (aggregate == null)
            {
                sb.AppendLine("HabitPatternMiner unavailable -- no aggregated evidence bundled.");
            }
            else
            {
                sb.AppendLine("- missionsExamined: " + aggregate.MissionsExamined);
                sb.AppendLine("- complete: " + aggregate.MissionsComplete + ", failed: " + aggregate.MissionsFailed + ", cancelled: " + aggregate.MissionsCancelled);
                sb.AppendLine("- judgeVerdicts: PASS=" + aggregate.JudgePassCount + ", FAIL=" + aggregate.JudgeFailCount + ", NEEDS_REVISION=" + aggregate.JudgeNeedsRevisionCount + ", PENDING=" + aggregate.JudgePendingCount);
                sb.AppendLine("- averageRecoveryAttempts: " + aggregate.AverageRecoveryAttempts.ToString("0.00"));
                sb.AppendLine("- signalMailReceivedTotal: " + aggregate.SignalMailReceivedTotal);

                if (aggregate.FailureModeTags.Count > 0)
                {
                    sb.AppendLine("- failureModeTags:");
                    foreach (FailureModeTagCount tag in aggregate.FailureModeTags)
                        sb.AppendLine("    - " + tag.Tag + ": " + tag.MissionCount);
                }

                if (aggregate.TopTouchedFiles.Count > 0)
                {
                    sb.AppendLine("- topTouchedFiles:");
                    foreach (FileTouchCount f in aggregate.TopTouchedFiles)
                        sb.AppendLine("    - " + f.Path + " (" + f.EditCount + ")");
                }

                if (aggregate.VesselContributions.Count > 0)
                {
                    sb.AppendLine("- vesselContributions:");
                    foreach (VesselContribution v in aggregate.VesselContributions)
                    {
                        sb.AppendLine("    - " + v.VesselId
                            + " name=" + v.VesselName
                            + " missions=" + v.MissionCount
                            + " complete=" + v.CompleteCount
                            + " failed=" + v.FailedCount
                            + " cancelled=" + v.CancelledCount);
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("## 4. ALL VESSEL LEARNED PLAYBOOKS IN THIS FLEET (active vessels only)");
            if (vesselLearned.Count == 0)
            {
                sb.AppendLine("No active vessels with learned playbooks in this fleet.");
            }
            else
            {
                foreach (VesselLearnedSnapshot snap in vesselLearned)
                {
                    sb.AppendLine();
                    sb.AppendLine("### Vessel: " + snap.VesselName + " (" + snap.VesselId + ")");
                    sb.AppendLine(snap.LearnedPlaybookContent);
                }
            }

            sb.AppendLine();
            sb.AppendLine("## 5. RECENTLY REJECTED FLEET-CURATE PROPOSALS");
            if (rejections.Count == 0)
            {
                sb.AppendLine("No rejected fleet-curate proposals recorded for this fleet.");
            }
            else
            {
                foreach (string note in rejections)
                    sb.AppendLine("- " + note);
            }

            sb.AppendLine();
            sb.AppendLine("## 6. CONSTRAINTS");
            sb.AppendLine("- Output must be exactly two fenced blocks named reflections-candidate and reflections-diff.");
            sb.AppendLine("- The reflections-candidate block is the dual-section fleet shape: literal `=== FLEET PLAYBOOK CONTENT ===` and `=== END FLEET PLAYBOOK CONTENT ===` markers wrap the markdown fleet playbook (with `Source: vessel <name> (msn_xxx)` per-vessel attribution); literal `=== RIPPLE DISABLES (JSON) ===` and `=== END RIPPLE DISABLES ===` markers wrap a JSON object with one key `disableFromVessels` whose value is an array of `{vesselId, noteRef, reason}` entries (empty array permitted).");
            sb.AppendLine("- The reflections-diff block is JSON: {\"added\":[{section,summary,confidence,vesselsContributing,missionsSupporting}], \"modified\":[{noteRef,change}], \"disabled\":[{noteRef,reason}], \"rippleDisables\":N, \"evidenceConfidence\":\"high|mixed|low\", \"missionsExamined\":N, \"vesselsInScope\":N, \"notes\":\"one paragraph\"}.");
            sb.AppendLine("- Never propose CLAUDE.md edits or code changes.");

            return sb.ToString();
        }

        private async Task EmitFleetCurateDispatchedEventAsync(
            Mission mission,
            Vessel anchorVessel,
            string fleetId,
            bool dualJudge,
            int tokenBudget,
            CancellationToken token)
        {
            try
            {
                ArmadaEvent dispatched = new ArmadaEvent(_ReflectionDispatchedEvent, "Fleet-curate reflection dispatched.");
                dispatched.TenantId = mission.TenantId ?? anchorVessel.TenantId ?? Constants.DefaultTenantId;
                dispatched.EntityType = "mission";
                dispatched.EntityId = mission.Id;
                dispatched.MissionId = mission.Id;
                dispatched.VesselId = anchorVessel.Id;
                dispatched.VoyageId = mission.VoyageId;
                dispatched.Payload = JsonSerializer.Serialize(new
                {
                    mode = ReflectionMemoryService.ModeToWireString(ReflectionMode.FleetCurate),
                    dualJudge = dualJudge,
                    tokenBudget = tokenBudget,
                    missionId = mission.Id,
                    targetType = "fleet",
                    targetId = fleetId
                });
                await _Database.Events.CreateAsync(dispatched, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort observability.
            }
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
        /// Cross-vessel promotion-candidate hint surfaced in the consolidate-mode brief
        /// (Reflections v2-F3 extension to v1's vessel-curate flow). Passive nudge: the
        /// vessel-curate captain may mention the suggestion in its diff notes or a
        /// [CLAUDE.MD-PROPOSAL] block, but accept semantics are unchanged.
        /// </summary>
        public sealed class CrossVesselSuggestion
        {
            /// <summary>Sibling vessel id whose learned playbook contains the candidate fact.</summary>
            public string VesselId { get; set; } = "";

            /// <summary>Sibling vessel name (used to compose the human-readable hint line).</summary>
            public string VesselName { get; set; } = "";

            /// <summary>Trimmed fact line copied from the sibling vessel's learned playbook.</summary>
            public string SuggestedFact { get; set; } = "";

            /// <summary>Maximum 3-gram Jaccard similarity observed (vs current playbook or evidence bundle).</summary>
            public double Similarity { get; set; }
        }

        /// <summary>
        /// Snapshot of one active vessel's vessel-&lt;repo&gt;-learned playbook content used by the
        /// fleet-curate brief assembler (Reflections v2-F3) to surface section 4 (all vessel
        /// learned playbooks in the fleet) for promotion-candidate detection.
        /// </summary>
        public sealed class VesselLearnedSnapshot
        {
            /// <summary>Vessel id.</summary>
            public string VesselId { get; set; } = "";

            /// <summary>Vessel name.</summary>
            public string VesselName { get; set; } = "";

            /// <summary>Verbatim content of the vessel-learned playbook (or the empty placeholder).</summary>
            public string LearnedPlaybookContent { get; set; } = "";
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
