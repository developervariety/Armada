namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.FileSystemGlobbing;
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
        private const string _ReflectionDispatchedEvent = "reflection.dispatched";

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
        public async Task<List<string>> ReadRejectedProposalNotesAsync(Vessel vessel, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            List<ArmadaEvent> events = await _Database.Events.EnumerateByVesselAsync(vessel.Id, 50, token).ConfigureAwait(false);
            List<string> notes = new List<string>();
            foreach (ArmadaEvent evt in events)
            {
                if (!String.Equals(evt.EventType, _ReflectionRejectedEvent, StringComparison.Ordinal))
                    continue;

                string missionId = evt.MissionId ?? "";
                string reason = "";
                if (!String.IsNullOrEmpty(evt.Payload))
                {
                    try
                    {
                        RejectionPayload? payload = System.Text.Json.JsonSerializer.Deserialize<RejectionPayload>(evt.Payload);
                        reason = payload?.Reason ?? "";
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        reason = "";
                    }
                }

                notes.Add("missionId: " + missionId + " -- " + reason);
            }

            return notes;
        }

        /// <inheritdoc />
        public async Task<List<string>> ReadRejectedProposalNotesByModeAsync(
            Vessel vessel,
            ReflectionMode mode,
            CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            List<ArmadaEvent> events = await _Database.Events.EnumerateByVesselAsync(vessel.Id, 100, token).ConfigureAwait(false);
            List<string> notes = new List<string>();
            foreach (ArmadaEvent evt in events)
            {
                if (!String.Equals(evt.EventType, _ReflectionRejectedEvent, StringComparison.Ordinal))
                    continue;

                string missionId = evt.MissionId ?? "";
                string reason = "";
                ReflectionMode? rejectedMode = null;

                if (!String.IsNullOrEmpty(evt.Payload))
                {
                    try
                    {
                        RejectionPayload? payload = JsonSerializer.Deserialize<RejectionPayload>(evt.Payload);
                        reason = payload?.Reason ?? "";
                        rejectedMode = ParseModeString(payload?.Mode);
                    }
                    catch (JsonException)
                    {
                        reason = "";
                    }
                }

                if (!rejectedMode.HasValue && !String.IsNullOrEmpty(missionId))
                {
                    rejectedMode = await ResolveMissionDispatchedModeAsync(missionId, token).ConfigureAwait(false);
                }

                ReflectionMode effectiveMode = rejectedMode ?? ReflectionMode.Consolidate;
                if (effectiveMode != mode)
                    continue;

                notes.Add("missionId: " + missionId + " -- " + reason);
            }

            return notes;
        }

        /// <inheritdoc />
        public async Task<List<string>> ReadRecentCommitSubjectsAsync(
            Vessel vessel,
            int limit,
            CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (limit < 1) return new List<string>();

            string? workingDirectory = !String.IsNullOrEmpty(vessel.WorkingDirectory) ? vessel.WorkingDirectory : vessel.LocalPath;
            if (String.IsNullOrEmpty(workingDirectory))
                return new List<string>();

            try
            {
                if (!System.IO.Directory.Exists(workingDirectory))
                    return new List<string>();

                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = workingDirectory!,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("log");
                psi.ArgumentList.Add("--max-count=" + limit.ToString());
                psi.ArgumentList.Add("--pretty=format:%h %s (%aI)");
                if (!String.IsNullOrEmpty(vessel.DefaultBranch))
                {
                    psi.ArgumentList.Add(vessel.DefaultBranch);
                }

                using (System.Diagnostics.Process? process = System.Diagnostics.Process.Start(psi))
                {
                    if (process == null) return new List<string>();
                    string stdout = await process.StandardOutput.ReadToEndAsync(token).ConfigureAwait(false);
                    await process.WaitForExitAsync(token).ConfigureAwait(false);
                    if (process.ExitCode != 0) return new List<string>();

                    List<string> subjects = new List<string>();
                    foreach (string raw in stdout.Split('\n'))
                    {
                        string line = raw.TrimEnd('\r');
                        if (!String.IsNullOrWhiteSpace(line)) subjects.Add(line);
                        if (subjects.Count >= limit) break;
                    }

                    return subjects;
                }
            }
            catch (Exception)
            {
                return new List<string>();
            }
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

            DispatchedPayload? dispatchedPayload = await ReadDispatchedPayloadAsync(mission.Id, token).ConfigureAwait(false);
            ReflectionMode missionMode = ParseModeString(dispatchedPayload?.Mode) ?? ReflectionMode.Consolidate;
            bool dualJudge = dispatchedPayload?.DualJudge ?? false;
            outcome.Mode = ModeToWireString(missionMode);

            if (missionMode == ReflectionMode.PackCurate)
            {
                return await AcceptPackCurateProposalAsync(mission, vessel, tenantId, editsMarkdown, parser, dualJudge, outcome, token).ConfigureAwait(false);
            }

            if (missionMode == ReflectionMode.PersonaCurate || missionMode == ReflectionMode.CaptainCurate)
            {
                return await AcceptIdentityCurateProposalAsync(mission, vessel, tenantId, editsMarkdown, parser, dualJudge, missionMode, dispatchedPayload, outcome, token).ConfigureAwait(false);
            }

            if (missionMode == ReflectionMode.FleetCurate)
            {
                return await AcceptFleetCurateProposalAsync(mission, vessel, tenantId, editsMarkdown, parser, dualJudge, dispatchedPayload, outcome, token).ConfigureAwait(false);
            }

            bool overrideActive = !String.IsNullOrWhiteSpace(editsMarkdown);
            string contentToApply;
            string? candidateMarkdown = null;
            string? diffText = null;
            if (overrideActive)
            {
                contentToApply = editsMarkdown!.Trim();
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
                candidateMarkdown = parsed.CandidateMarkdown;
                diffText = parsed.ReflectionsDiffText;
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

            string priorPlaybookContent = await ReadLearnedPlaybookContentAsync(vessel, token).ConfigureAwait(false);

            if (!overrideActive && missionMode == ReflectionMode.Reorganize && !String.IsNullOrEmpty(diffText))
            {
                ReorganizeGateResult gate = ValidateReorganizeDiff(diffText!, priorPlaybookContent);
                if (!String.IsNullOrEmpty(gate.Error))
                {
                    outcome.Error = gate.Error;
                    outcome.ErrorDetails = gate.Details;
                    return outcome;
                }
            }

            if (!overrideActive && dualJudge)
            {
                DualJudgeGateResult judgeGate = await EvaluateDualJudgeGateAsync(mission, token).ConfigureAwait(false);
                outcome.JudgeVerdicts = judgeGate.Verdicts;
                if (!judgeGate.AllPassed)
                {
                    outcome.Error = "dual_judge_not_passed";
                    outcome.ErrorDetails = new { judgeVerdicts = judgeGate.Verdicts };
                    return outcome;
                }
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

            ReflectionMetrics metrics = ComputeReflectionMetrics(
                priorPlaybookContent,
                contentToApply,
                missionMode,
                overrideActive ? null : diffText);

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
                vesselId = vessel.Id,
                appliedContentLength = contentToApply.Length,
                mode = ModeToWireString(missionMode),
                dualJudge = dualJudge,
                entriesBefore = metrics.EntriesBefore,
                entriesAfter = metrics.EntriesAfter,
                removed = metrics.Removed,
                merged = metrics.Merged,
                addedFromReorganize = metrics.AddedFromReorganize,
                tokensBefore = metrics.TokensBefore,
                tokensAfter = metrics.TokensAfter,
                judgeVerdicts = outcome.JudgeVerdicts
            });
            await _Database.Events.CreateAsync(accepted, token).ConfigureAwait(false);

            outcome.PlaybookId = persisted.Id;
            outcome.PlaybookVersion = persisted.LastUpdateUtc.ToString("o");
            outcome.AppliedContent = contentToApply;
            return outcome;
        }

        /// <inheritdoc />
        public async Task<string?> RejectMemoryProposalAsync(
            string missionId,
            string reason,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(missionId))
                return "mission_not_found";

            if (String.IsNullOrWhiteSpace(reason))
                return "mission_not_found";

            Mission? mission = await _Database.Missions.ReadAsync(missionId.Trim(), token).ConfigureAwait(false);
            if (mission == null)
                return "mission_not_found";

            if (!String.Equals(mission.Persona, "MemoryConsolidator", StringComparison.OrdinalIgnoreCase))
                return "mission_not_a_reflection";

            if (!IsMissionCompleteEnoughForAcceptance(mission.Status))
                return "mission_not_complete";

            if (await MissionHasReflectionDispositionAsync(mission.Id, token).ConfigureAwait(false))
                return "proposal_already_processed";

            string vesselId = mission.VesselId ?? "";
            string tenantId = mission.TenantId ?? Constants.DefaultTenantId;
            DispatchedPayload? dispatchedPayload = await ReadDispatchedPayloadAsync(mission.Id, token).ConfigureAwait(false);
            ReflectionMode missionMode = ParseModeString(dispatchedPayload?.Mode) ?? ReflectionMode.Consolidate;

            ArmadaEvent rejected = new ArmadaEvent(_ReflectionRejectedEvent, "Reflection memory proposal rejected.");
            rejected.TenantId = tenantId;
            rejected.EntityType = "mission";
            rejected.EntityId = mission.Id;
            rejected.MissionId = mission.Id;
            rejected.VesselId = vesselId;
            rejected.VoyageId = mission.VoyageId;
            rejected.Payload = JsonSerializer.Serialize(new
            {
                missionId = mission.Id,
                reason = reason.Trim(),
                vesselId = vesselId,
                playbookId = (string?)null,
                appliedContentLength = 0,
                mode = ModeToWireString(missionMode)
            });
            await _Database.Events.CreateAsync(rejected, token).ConfigureAwait(false);

            return null;
        }

        #endregion

        #region Private-Methods

        private async Task<ReflectionAcceptProposalResult> AcceptPackCurateProposalAsync(
            Mission mission,
            Vessel vessel,
            string tenantId,
            string? editsMarkdown,
            IReflectionOutputParser parser,
            bool dualJudge,
            ReflectionAcceptProposalResult outcome,
            CancellationToken token)
        {
            bool overrideActive = !String.IsNullOrWhiteSpace(editsMarkdown);
            string candidateJson;
            if (overrideActive)
            {
                candidateJson = editsMarkdown!.Trim();
            }
            else
            {
                ReflectionOutputParseResult parsed = parser.Parse(mission.AgentOutput ?? "");
                if (parsed.Verdict != ReflectionOutputParseVerdict.Success)
                {
                    outcome.Error = "output_contract_violation";
                    return outcome;
                }
                candidateJson = parsed.CandidateMarkdown ?? "";
            }

            if (String.IsNullOrWhiteSpace(candidateJson))
            {
                outcome.Error = "output_contract_violation";
                return outcome;
            }

            PackCurateCandidate? proposed;
            try
            {
                proposed = JsonSerializer.Deserialize<PackCurateCandidate>(candidateJson, _PackCurateJsonOptions);
            }
            catch (JsonException)
            {
                outcome.Error = "output_contract_violation";
                return outcome;
            }
            if (proposed == null)
            {
                outcome.Error = "output_contract_violation";
                return outcome;
            }

            if (!overrideActive)
            {
                string? antiPatternError = ValidatePackCurateAntiPatterns(proposed);
                if (antiPatternError != null)
                {
                    outcome.Error = antiPatternError;
                    return outcome;
                }
            }

            // Verify modify/disable hint ids exist on this vessel.
            HashSet<string> existingIdSet = new HashSet<string>(StringComparer.Ordinal);
            List<VesselPackHint> existingHints = await _Database.VesselPackHints
                .EnumerateByVesselAsync(vessel.Id, token).ConfigureAwait(false);
            foreach (VesselPackHint h in existingHints) existingIdSet.Add(h.Id);

            if (proposed.ModifyHints != null)
            {
                foreach (PackCurateModifyHint mh in proposed.ModifyHints)
                {
                    if (String.IsNullOrEmpty(mh.Id) || !existingIdSet.Contains(mh.Id))
                    {
                        outcome.Error = "pack_hint_id_not_found";
                        outcome.ErrorDetails = new { id = mh.Id ?? "" };
                        return outcome;
                    }
                }
            }
            if (proposed.DisableHints != null)
            {
                foreach (PackCurateDisableHint dh in proposed.DisableHints)
                {
                    if (String.IsNullOrEmpty(dh.Id) || !existingIdSet.Contains(dh.Id))
                    {
                        outcome.Error = "pack_hint_id_not_found";
                        outcome.ErrorDetails = new { id = dh.Id ?? "" };
                        return outcome;
                    }
                }
            }

            if (!overrideActive && dualJudge)
            {
                DualJudgeGateResult judgeGate = await EvaluateDualJudgeGateAsync(mission, token).ConfigureAwait(false);
                outcome.JudgeVerdicts = judgeGate.Verdicts;
                if (!judgeGate.AllPassed)
                {
                    outcome.Error = "dual_judge_not_passed";
                    outcome.ErrorDetails = new { judgeVerdicts = judgeGate.Verdicts };
                    return outcome;
                }
            }

            List<object> pathWarnings = overrideActive
                ? new List<object>()
                : RunPackCuratePathValidation(proposed, vessel);

            List<object> conflictWarnings = overrideActive
                ? new List<object>()
                : RunPackCurateConflictDetection(proposed, existingHints);

            // Apply: insert/update/disable. Best-effort sequential -- IVesselPackHintMethods
            // does not currently expose a transaction primitive, but each operation is
            // independently idempotent on retry (insert by new id, update by id, deactivate
            // by id) so a partial failure is recoverable.
            List<string> appliedHintIds = new List<string>();
            int hintsAdded = 0;
            int hintsModified = 0;
            int hintsDisabled = 0;

            if (proposed.AddHints != null)
            {
                foreach (PackCurateAddHint ah in proposed.AddHints)
                {
                    VesselPackHint hint = new VesselPackHint
                    {
                        VesselId = vessel.Id,
                        GoalPattern = ah.GoalPattern ?? "",
                        MustIncludeJson = VesselPackHint.SerializeStringList(ah.MustInclude),
                        MustExcludeJson = VesselPackHint.SerializeStringList(ah.MustExclude),
                        Priority = ah.Priority ?? 0,
                        Confidence = String.IsNullOrEmpty(ah.Confidence) ? "medium" : ah.Confidence!,
                        Justification = ah.Justification,
                        SourceMissionIdsJson = VesselPackHint.SerializeStringList(ah.SourceMissionIds),
                        Active = true
                    };
                    await _Database.VesselPackHints.CreateAsync(hint, token).ConfigureAwait(false);
                    appliedHintIds.Add(hint.Id);
                    hintsAdded++;
                }
            }

            if (proposed.ModifyHints != null)
            {
                foreach (PackCurateModifyHint mh in proposed.ModifyHints)
                {
                    VesselPackHint? existing = await _Database.VesselPackHints.ReadAsync(mh.Id!, token).ConfigureAwait(false);
                    if (existing == null) continue;
                    if (mh.Changes == null) continue;

                    if (mh.Changes.MustInclude != null)
                        existing.MustIncludeJson = VesselPackHint.SerializeStringList(mh.Changes.MustInclude);
                    if (mh.Changes.MustExclude != null)
                        existing.MustExcludeJson = VesselPackHint.SerializeStringList(mh.Changes.MustExclude);
                    if (mh.Changes.GoalPattern != null) existing.GoalPattern = mh.Changes.GoalPattern;
                    if (mh.Changes.Priority.HasValue) existing.Priority = mh.Changes.Priority.Value;
                    if (mh.Changes.Confidence != null) existing.Confidence = mh.Changes.Confidence;
                    if (mh.Changes.Justification != null) existing.Justification = mh.Changes.Justification;

                    await _Database.VesselPackHints.UpdateAsync(existing, token).ConfigureAwait(false);
                    appliedHintIds.Add(existing.Id);
                    hintsModified++;
                }
            }

            if (proposed.DisableHints != null)
            {
                foreach (PackCurateDisableHint dh in proposed.DisableHints)
                {
                    await _Database.VesselPackHints.DeactivateAsync(dh.Id!, token).ConfigureAwait(false);
                    appliedHintIds.Add(dh.Id!);
                    hintsDisabled++;
                }
            }

            vessel.LastReflectionMissionId = mission.Id;
            await _Database.Vessels.UpdateAsync(vessel, token).ConfigureAwait(false);

            ArmadaEvent accepted = new ArmadaEvent(_ReflectionAcceptedEvent, "Reflection memory proposal accepted (pack-curate).");
            accepted.TenantId = mission.TenantId ?? vessel.TenantId ?? tenantId;
            accepted.EntityType = "mission";
            accepted.EntityId = mission.Id;
            accepted.MissionId = mission.Id;
            accepted.VesselId = vessel.Id;
            accepted.VoyageId = mission.VoyageId;
            accepted.Payload = JsonSerializer.Serialize(new
            {
                missionId = mission.Id,
                vesselId = vessel.Id,
                mode = "pack-curate",
                dualJudge = dualJudge,
                hintsAdded = hintsAdded,
                hintsModified = hintsModified,
                hintsDisabled = hintsDisabled,
                missionsExamined = ExtractMissionsExaminedFromDiff(mission.AgentOutput, parser),
                evidenceConfidence = ExtractEvidenceConfidenceFromDiff(mission.AgentOutput, parser),
                pathValidationWarnings = pathWarnings.Count,
                conflictWarnings = conflictWarnings.Count,
                judgeVerdicts = outcome.JudgeVerdicts
            });
            await _Database.Events.CreateAsync(accepted, token).ConfigureAwait(false);

            outcome.AppliedHintIds = appliedHintIds;
            outcome.PathWarnings = pathWarnings;
            outcome.ConflictWarnings = conflictWarnings;
            return outcome;
        }

        private async Task<ReflectionAcceptProposalResult> AcceptIdentityCurateProposalAsync(
            Mission mission,
            Vessel anchorVessel,
            string tenantId,
            string? editsMarkdown,
            IReflectionOutputParser parser,
            bool dualJudge,
            ReflectionMode missionMode,
            DispatchedPayload? dispatched,
            ReflectionAcceptProposalResult outcome,
            CancellationToken token)
        {
            string? targetType = dispatched?.TargetType;
            string? targetId = dispatched?.TargetId;
            if (String.IsNullOrEmpty(targetId))
            {
                outcome.Error = "identity_target_unresolved";
                return outcome;
            }

            bool overrideActive = !String.IsNullOrWhiteSpace(editsMarkdown);
            string contentToApply;
            string? diffText = null;
            if (overrideActive)
            {
                contentToApply = editsMarkdown!.Trim();
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
                diffText = parsed.ReflectionsDiffText;
                if (String.IsNullOrWhiteSpace(contentToApply))
                {
                    outcome.Error = "output_contract_violation";
                    return outcome;
                }
            }

            // Validation pipeline: confidence, captain-id-in-persona, evidence floor.
            if (!overrideActive)
            {
                string? validationError = ValidateIdentityCandidateNotes(contentToApply, missionMode);
                if (!String.IsNullOrEmpty(validationError))
                {
                    outcome.Error = validationError;
                    return outcome;
                }

                // Counter-evidence handling: when the prior playbook had notes with `Source:`
                // attribution, the new diff MUST address those notes (modified or disabled).
                string? counterError = await ValidateCounterEvidenceHandlingAsync(missionMode, targetId!, anchorVessel, diffText, contentToApply, token).ConfigureAwait(false);
                if (!String.IsNullOrEmpty(counterError))
                {
                    outcome.Error = counterError;
                    return outcome;
                }
            }

            // Dual-judge gate (shared with v1).
            if (!overrideActive && dualJudge)
            {
                DualJudgeGateResult judgeGate = await EvaluateDualJudgeGateAsync(mission, token).ConfigureAwait(false);
                outcome.JudgeVerdicts = judgeGate.Verdicts;
                if (!judgeGate.AllPassed)
                {
                    outcome.Error = "dual_judge_not_passed";
                    outcome.ErrorDetails = new { judgeVerdicts = judgeGate.Verdicts };
                    return outcome;
                }
            }

            // Vessel-vs-identity conflict detection (non-blocking).
            List<object> conflictWarnings = overrideActive
                ? new List<object>()
                : await DetectVesselIdentityConflictsAsync(targetId!, missionMode, contentToApply, anchorVessel, token).ConfigureAwait(false);

            // Apply: write to persona-/captain-learned playbook (lazy-create captain side).
            string fileName = (missionMode == ReflectionMode.PersonaCurate ? "persona-" : "captain-")
                + Armada.Core.Memory.ReflectionDispatcher.SanitizeIdentityName(targetId!)
                + "-learned.md";
            PlaybookService playbookService = new PlaybookService(_Database, CreateSilentLogging());
            Playbook? existing = await _Database.Playbooks.ReadByFileNameAsync(tenantId, fileName, token).ConfigureAwait(false);

            Playbook persisted;
            if (existing == null)
            {
                Playbook created = new Playbook(fileName, contentToApply);
                created.TenantId = tenantId;
                created.UserId = Constants.DefaultUserId;
                created.Description = (missionMode == ReflectionMode.PersonaCurate ? "Persona-learned notes for " : "Captain-learned notes for ") + targetId;
                playbookService.Validate(created);
                persisted = await _Database.Playbooks.CreateAsync(created, token).ConfigureAwait(false);
            }
            else
            {
                existing.Content = contentToApply;
                playbookService.Validate(existing);
                persisted = await _Database.Playbooks.UpdateAsync(existing, token).ConfigureAwait(false);
            }

            // Wire LearnedPlaybookId + DefaultPlaybooks on the identity row (lazy-create captain).
            await EnsureIdentityLearnedPlaybookWiringAsync(missionMode, targetId!, persisted.Id, token).ConfigureAwait(false);

            // Identity-curate event uses targetType/targetId rather than vesselId.
            ReflectionDiffMetrics metrics = ParseIdentityDiffMetrics(diffText);

            ArmadaEvent accepted = new ArmadaEvent(_ReflectionAcceptedEvent, "Identity-curate proposal accepted.");
            accepted.TenantId = mission.TenantId ?? tenantId;
            accepted.EntityType = "mission";
            accepted.EntityId = mission.Id;
            accepted.MissionId = mission.Id;
            accepted.VesselId = anchorVessel.Id;
            accepted.VoyageId = mission.VoyageId;
            accepted.Payload = JsonSerializer.Serialize(new
            {
                playbookId = persisted.Id,
                missionId = mission.Id,
                appliedContentLength = contentToApply.Length,
                mode = ModeToWireString(missionMode),
                targetType = targetType,
                targetId = targetId,
                notesAdded = metrics.NotesAdded,
                notesModified = metrics.NotesModified,
                notesDisabled = metrics.NotesDisabled,
                missionsExamined = metrics.MissionsExamined,
                captainsInScope = metrics.CaptainsInScope,
                evidenceConfidence = metrics.EvidenceConfidence,
                vesselConflictWarnings = conflictWarnings.Count,
                dualJudge = dualJudge,
                judgeVerdicts = outcome.JudgeVerdicts
            });
            await _Database.Events.CreateAsync(accepted, token).ConfigureAwait(false);

            outcome.PlaybookId = persisted.Id;
            outcome.PlaybookVersion = persisted.LastUpdateUtc.ToString("o");
            outcome.AppliedContent = contentToApply;
            outcome.ConflictWarnings = conflictWarnings;
            return outcome;
        }

        private async Task<ReflectionAcceptProposalResult> AcceptFleetCurateProposalAsync(
            Mission mission,
            Vessel anchorVessel,
            string tenantId,
            string? editsMarkdown,
            IReflectionOutputParser parser,
            bool dualJudge,
            DispatchedPayload? dispatched,
            ReflectionAcceptProposalResult outcome,
            CancellationToken token)
        {
            string? fleetId = dispatched?.TargetId;
            if (String.IsNullOrEmpty(fleetId))
            {
                outcome.Error = "fleet_target_unresolved";
                return outcome;
            }
            Fleet? fleet = await _Database.Fleets.ReadAsync(fleetId!, token).ConfigureAwait(false);
            if (fleet == null)
            {
                outcome.Error = "fleet_not_found";
                return outcome;
            }
            string fleetTenantId = !String.IsNullOrEmpty(fleet.TenantId) ? fleet.TenantId! : tenantId;

            bool overrideActive = !String.IsNullOrWhiteSpace(editsMarkdown);
            string fleetPlaybookContent;
            string ripplesJson;
            string? diffText = null;

            if (overrideActive)
            {
                FleetCandidateSplit overrideSplit = ParseFleetCandidateBlock(editsMarkdown!.Trim());
                if (overrideSplit.Verdict != FleetCandidateSplitVerdict.Success)
                {
                    outcome.Error = "output_contract_violation";
                    return outcome;
                }
                fleetPlaybookContent = overrideSplit.PlaybookContent;
                ripplesJson = overrideSplit.RipplesJson;
            }
            else
            {
                ReflectionOutputParseResult parsed = parser.Parse(mission.AgentOutput ?? "");
                if (parsed.Verdict != ReflectionOutputParseVerdict.Success)
                {
                    outcome.Error = "output_contract_violation";
                    return outcome;
                }
                FleetCandidateSplit split = ParseFleetCandidateBlock(parsed.CandidateMarkdown ?? "");
                if (split.Verdict != FleetCandidateSplitVerdict.Success)
                {
                    outcome.Error = "output_contract_violation";
                    return outcome;
                }
                fleetPlaybookContent = split.PlaybookContent;
                ripplesJson = split.RipplesJson;
                diffText = parsed.ReflectionsDiffText;
            }

            // Parse ripple disables JSON (sidecar). Empty array permitted.
            List<FleetRippleDisable> ripples;
            try
            {
                ripples = ParseRippleDisables(ripplesJson);
            }
            catch
            {
                outcome.Error = "output_contract_violation";
                return outcome;
            }

            List<Vessel> fleetVessels = await _Database.Vessels.EnumerateByFleetAsync(fleet.Id, token).ConfigureAwait(false);
            List<Vessel> activeFleetVessels = fleetVessels.Where(v => v.Active).ToList();

            if (!overrideActive)
            {
                // Promotion-evidence gates: parse diff `added` array.
                FleetPromotionGateError? gateErr = ValidateFleetPromotionGates(diffText);
                if (gateErr.HasValue)
                {
                    outcome.Error = gateErr.Value.Error;
                    outcome.ErrorDetails = gateErr.Value.Details;
                    return outcome;
                }

                // Vessel-fleet conflict detection (BLOCKING per Q3 brainstorm choice).
                FleetConflictResult conflictResult = await DetectFleetVesselConflictAsync(
                    fleetPlaybookContent,
                    activeFleetVessels,
                    token).ConfigureAwait(false);
                if (conflictResult.Conflict != null)
                {
                    outcome.Error = "fleet_curate_vessel_conflict";
                    outcome.ErrorDetails = conflictResult.Conflict;
                    return outcome;
                }

                // Counter-evidence handling (mirrors F2). Read current fleet playbook content;
                // if it had tagged notes and the new diff has zero modified+disabled, BLOCK.
                string priorFleetContent = await ReadFleetLearnedPlaybookContentForAcceptAsync(fleet, fleetTenantId, token).ConfigureAwait(false);
                if (HasTaggedNotes(priorFleetContent))
                {
                    int modDis = CountDiffModifiedAndDisabled(diffText);
                    if (modDis == 0 && CountTaggedNotes(priorFleetContent) > CountTaggedNotes(fleetPlaybookContent))
                    {
                        outcome.Error = "fleet_curate_ignored_counter_evidence";
                        return outcome;
                    }
                }

                // Ripple validation: each ripple's vesselId must be in fleet; noteRef must
                // resolve to an existing entry in that vessel's learned playbook.
                foreach (FleetRippleDisable ripple in ripples)
                {
                    Vessel? rippleVessel = activeFleetVessels.FirstOrDefault(v => String.Equals(v.Id, ripple.VesselId, StringComparison.Ordinal));
                    if (rippleVessel == null)
                    {
                        outcome.Error = "fleet_ripple_invalid_vessel";
                        outcome.ErrorDetails = new { vesselId = ripple.VesselId };
                        return outcome;
                    }
                    string vesselContent = await ReadLearnedPlaybookContentAsync(rippleVessel, token).ConfigureAwait(false);
                    if (!HasNoteAt(vesselContent, ripple.NoteRef))
                    {
                        outcome.Error = "fleet_ripple_invalid_note_ref";
                        outcome.ErrorDetails = new { vesselId = ripple.VesselId, noteRef = ripple.NoteRef };
                        return outcome;
                    }
                }
            }

            // Dual-judge gate (shared with v1).
            if (!overrideActive && dualJudge)
            {
                DualJudgeGateResult judgeGate = await EvaluateDualJudgeGateAsync(mission, token).ConfigureAwait(false);
                outcome.JudgeVerdicts = judgeGate.Verdicts;
                if (!judgeGate.AllPassed)
                {
                    outcome.Error = "dual_judge_not_passed";
                    outcome.ErrorDetails = new { judgeVerdicts = judgeGate.Verdicts };
                    return outcome;
                }
            }

            // Apply transactionally (best-effort -- the database driver may not expose explicit
            // transactions across mixed table writes, so we sequence: 1) update or lazy-create
            // fleet learned playbook, 2) wire FleetId.LearnedPlaybookId + DefaultPlaybooks,
            // 3) apply ripple disables to each vessel-learned playbook).
            string fileName = "fleet-" + Armada.Core.Memory.ReflectionDispatcher.SanitizeIdentityName(fleet.Id) + "-learned.md";
            PlaybookService playbookService = new PlaybookService(_Database, CreateSilentLogging());
            Playbook? existing = null;
            if (!String.IsNullOrEmpty(fleet.LearnedPlaybookId))
            {
                existing = await _Database.Playbooks.ReadAsync(fleet.LearnedPlaybookId!, token).ConfigureAwait(false);
            }
            if (existing == null)
            {
                existing = await _Database.Playbooks.ReadByFileNameAsync(fleetTenantId, fileName, token).ConfigureAwait(false);
            }

            Playbook persisted;
            if (existing == null)
            {
                Playbook created = new Playbook(fileName, fleetPlaybookContent);
                created.TenantId = fleetTenantId;
                created.UserId = Constants.DefaultUserId;
                created.Description = "Fleet-learned notes for " + fleet.Name;
                playbookService.Validate(created);
                persisted = await _Database.Playbooks.CreateAsync(created, token).ConfigureAwait(false);
            }
            else
            {
                existing.Content = fleetPlaybookContent;
                playbookService.Validate(existing);
                persisted = await _Database.Playbooks.UpdateAsync(existing, token).ConfigureAwait(false);
            }

            // Wire the fleet row: LearnedPlaybookId + DefaultPlaybooks (lazy-add the FK entry).
            await EnsureFleetLearnedPlaybookWiringAsync(fleet, persisted.Id, token).ConfigureAwait(false);

            // Apply ripple disables.
            int rippleApplied = 0;
            foreach (FleetRippleDisable ripple in ripples)
            {
                Vessel? rippleVessel = activeFleetVessels.FirstOrDefault(v => String.Equals(v.Id, ripple.VesselId, StringComparison.Ordinal));
                if (rippleVessel == null) continue;
                string vesselContent = await ReadLearnedPlaybookContentAsync(rippleVessel, token).ConfigureAwait(false);
                string newVesselContent = DisableNoteAt(vesselContent, ripple.NoteRef, ripple.Reason ?? "Promoted to fleet scope.");
                if (String.Equals(newVesselContent, vesselContent, StringComparison.Ordinal)) continue;

                string vesselFile = "vessel-" + SanitizeName(rippleVessel.Name) + "-learned.md";
                string vesselTenant = !String.IsNullOrEmpty(rippleVessel.TenantId) ? rippleVessel.TenantId! : Constants.DefaultTenantId;
                Playbook? vp = await _Database.Playbooks.ReadByFileNameAsync(vesselTenant, vesselFile, token).ConfigureAwait(false);
                if (vp == null) continue;
                vp.Content = newVesselContent;
                playbookService.Validate(vp);
                await _Database.Playbooks.UpdateAsync(vp, token).ConfigureAwait(false);
                rippleApplied++;
            }

            ReflectionDiffMetrics metrics = ParseIdentityDiffMetrics(diffText);
            int vesselsInScope = activeFleetVessels.Count;

            ArmadaEvent accepted = new ArmadaEvent(_ReflectionAcceptedEvent, "Fleet-curate proposal accepted.");
            accepted.TenantId = mission.TenantId ?? fleetTenantId;
            accepted.EntityType = "mission";
            accepted.EntityId = mission.Id;
            accepted.MissionId = mission.Id;
            accepted.VesselId = anchorVessel.Id;
            accepted.VoyageId = mission.VoyageId;
            accepted.Payload = JsonSerializer.Serialize(new
            {
                playbookId = persisted.Id,
                missionId = mission.Id,
                appliedContentLength = fleetPlaybookContent.Length,
                mode = ModeToWireString(ReflectionMode.FleetCurate),
                targetType = "fleet",
                targetId = fleet.Id,
                factsAdded = metrics.NotesAdded,
                factsModified = metrics.NotesModified,
                factsDisabled = metrics.NotesDisabled,
                rippleDisablesApplied = rippleApplied,
                missionsExamined = metrics.MissionsExamined,
                vesselsInScope = vesselsInScope,
                evidenceConfidence = metrics.EvidenceConfidence,
                vesselConflictBlocked = 0,
                dualJudge = dualJudge,
                judgeVerdicts = outcome.JudgeVerdicts
            });
            await _Database.Events.CreateAsync(accepted, token).ConfigureAwait(false);

            outcome.PlaybookId = persisted.Id;
            outcome.PlaybookVersion = persisted.LastUpdateUtc.ToString("o");
            outcome.AppliedContent = fleetPlaybookContent;
            return outcome;
        }

        private async Task<string> ReadFleetLearnedPlaybookContentForAcceptAsync(Fleet fleet, string tenantId, CancellationToken token)
        {
            if (!String.IsNullOrEmpty(fleet.LearnedPlaybookId))
            {
                Playbook? byId = await _Database.Playbooks.ReadAsync(fleet.LearnedPlaybookId!, token).ConfigureAwait(false);
                if (byId != null) return byId.Content;
            }
            string fileName = "fleet-" + Armada.Core.Memory.ReflectionDispatcher.SanitizeIdentityName(fleet.Id) + "-learned.md";
            Playbook? p = await _Database.Playbooks.ReadByFileNameAsync(tenantId, fileName, token).ConfigureAwait(false);
            return p?.Content ?? "";
        }

        private async Task EnsureFleetLearnedPlaybookWiringAsync(Fleet fleet, string playbookId, CancellationToken token)
        {
            bool dirty = false;
            if (!String.Equals(fleet.LearnedPlaybookId, playbookId, StringComparison.Ordinal))
            {
                fleet.LearnedPlaybookId = playbookId;
                dirty = true;
            }
            List<SelectedPlaybook> defaults = fleet.GetDefaultPlaybooks();
            if (!defaults.Exists(sp => String.Equals(sp.PlaybookId, playbookId, StringComparison.Ordinal)))
            {
                defaults.Add(new SelectedPlaybook { PlaybookId = playbookId, DeliveryMode = PlaybookDeliveryModeEnum.InstructionWithReference });
                fleet.DefaultPlaybooks = JsonSerializer.Serialize(defaults);
                dirty = true;
            }
            if (dirty) await _Database.Fleets.UpdateAsync(fleet, token).ConfigureAwait(false);
        }

        private static FleetCandidateSplit ParseFleetCandidateBlock(string raw)
        {
            FleetCandidateSplit split = new FleetCandidateSplit();
            if (String.IsNullOrEmpty(raw))
            {
                split.Verdict = FleetCandidateSplitVerdict.Malformed;
                return split;
            }

            const string playbookOpen = "=== FLEET PLAYBOOK CONTENT ===";
            const string playbookClose = "=== END FLEET PLAYBOOK CONTENT ===";
            const string ripplesOpen = "=== RIPPLE DISABLES (JSON) ===";
            const string ripplesClose = "=== END RIPPLE DISABLES ===";

            int pOpen = raw.IndexOf(playbookOpen, StringComparison.Ordinal);
            int pClose = raw.IndexOf(playbookClose, StringComparison.Ordinal);
            int rOpen = raw.IndexOf(ripplesOpen, StringComparison.Ordinal);
            int rClose = raw.IndexOf(ripplesClose, StringComparison.Ordinal);

            if (pOpen < 0 || pClose < 0 || rOpen < 0 || rClose < 0)
            {
                split.Verdict = FleetCandidateSplitVerdict.Malformed;
                return split;
            }
            if (pClose <= pOpen || rClose <= rOpen)
            {
                split.Verdict = FleetCandidateSplitVerdict.Malformed;
                return split;
            }

            split.PlaybookContent = raw.Substring(pOpen + playbookOpen.Length, pClose - pOpen - playbookOpen.Length).Trim();
            split.RipplesJson = raw.Substring(rOpen + ripplesOpen.Length, rClose - rOpen - ripplesOpen.Length).Trim();
            split.Verdict = String.IsNullOrWhiteSpace(split.PlaybookContent)
                ? FleetCandidateSplitVerdict.Malformed
                : FleetCandidateSplitVerdict.Success;
            return split;
        }

        private static List<FleetRippleDisable> ParseRippleDisables(string ripplesJson)
        {
            List<FleetRippleDisable> result = new List<FleetRippleDisable>();
            if (String.IsNullOrWhiteSpace(ripplesJson)) return result;
            using JsonDocument doc = JsonDocument.Parse(ripplesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("expected object");
            if (!doc.RootElement.TryGetProperty("disableFromVessels", out JsonElement arrEl)) return result;
            if (arrEl.ValueKind != JsonValueKind.Array) throw new JsonException("disableFromVessels must be array");
            foreach (JsonElement el in arrEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) throw new JsonException("disableFromVessels entries must be objects");
                FleetRippleDisable r = new FleetRippleDisable();
                if (el.TryGetProperty("vesselId", out JsonElement vEl) && vEl.ValueKind == JsonValueKind.String)
                    r.VesselId = vEl.GetString() ?? "";
                if (el.TryGetProperty("noteRef", out JsonElement nEl) && nEl.ValueKind == JsonValueKind.String)
                    r.NoteRef = nEl.GetString() ?? "";
                if (el.TryGetProperty("reason", out JsonElement rEl) && rEl.ValueKind == JsonValueKind.String)
                    r.Reason = rEl.GetString();
                if (String.IsNullOrEmpty(r.VesselId) || String.IsNullOrEmpty(r.NoteRef))
                    throw new JsonException("ripple entry missing vesselId or noteRef");
                result.Add(r);
            }
            return result;
        }

        private static FleetPromotionGateError? ValidateFleetPromotionGates(string? diffText)
        {
            if (String.IsNullOrEmpty(diffText)) return null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(diffText.Trim());
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                if (!doc.RootElement.TryGetProperty("added", out JsonElement addedEl)) return null;
                if (addedEl.ValueKind != JsonValueKind.Array) return null;
                int idx = 0;
                foreach (JsonElement entry in addedEl.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) { idx++; continue; }
                    int vc = 0;
                    int ms = 0;
                    if (entry.TryGetProperty("vesselsContributing", out JsonElement vEl) && vEl.ValueKind == JsonValueKind.Number)
                        vc = vEl.GetInt32();
                    if (entry.TryGetProperty("missionsSupporting", out JsonElement mEl) && mEl.ValueKind == JsonValueKind.Number)
                        ms = mEl.GetInt32();
                    if (vc < 2)
                    {
                        return new FleetPromotionGateError
                        {
                            Error = "fleet_promotion_insufficient_vessels",
                            Details = new { entryIndex = idx, vesselsContributing = vc }
                        };
                    }
                    if (ms < 3)
                    {
                        return new FleetPromotionGateError
                        {
                            Error = "fleet_promotion_insufficient_missions",
                            Details = new { entryIndex = idx, missionsSupporting = ms }
                        };
                    }
                    idx++;
                }
            }
            catch (JsonException) { /* malformed diff is caught upstream */ }
            return null;
        }

        private async Task<FleetConflictResult> DetectFleetVesselConflictAsync(
            string fleetCandidateContent,
            List<Vessel> activeVessels,
            CancellationToken token)
        {
            FleetConflictResult result = new FleetConflictResult();
            double threshold = _FleetConflictThreshold;

            // Extract candidate fact lines (each line starting with [high]/[medium]/[low]).
            List<string> candidateNotes = ExtractTaggedNoteLines(fleetCandidateContent);
            if (candidateNotes.Count == 0) return result;

            foreach (Vessel v in activeVessels)
            {
                string vesselContent = await ReadLearnedPlaybookContentAsync(v, token).ConfigureAwait(false);
                if (String.IsNullOrWhiteSpace(vesselContent)) continue;
                List<string> vesselNotes = ExtractTaggedNoteLines(vesselContent);
                foreach (string cand in candidateNotes)
                {
                    foreach (string vn in vesselNotes)
                    {
                        double sim = Armada.Core.Memory.HabitPatternMiner.Jaccard3GramSimilarity(cand, vn);
                        if (sim < threshold) continue;
                        if (!Armada.Core.Memory.HabitPatternMiner.SentimentDisagrees(cand, vn)) continue;
                        result.Conflict = new
                        {
                            vesselId = v.Id,
                            vesselName = v.Name,
                            vesselNote = vn.Length > 240 ? vn.Substring(0, 240) + "..." : vn,
                            candidateNote = cand.Length > 240 ? cand.Substring(0, 240) + "..." : cand,
                            jaccardSimilarity = Math.Round(sim, 4)
                        };
                        return result;
                    }
                }
            }

            return result;
        }

        // Settings hooks for fleet-curate gates. The dispatcher passes live settings in via
        // ReflectionDispatcher; the service uses module-static defaults that mirror the v2-F3
        // ArmadaSettings defaults.
        private static double _FleetConflictThreshold = 0.7;

        private static List<string> ExtractTaggedNoteLines(string content)
        {
            List<string> notes = new List<string>();
            if (String.IsNullOrEmpty(content)) return notes;
            string[] lines = content.Replace("\r\n", "\n").Split('\n');
            int idx = 0;
            while (idx < lines.Length)
            {
                string trimmed = lines[idx].TrimStart();
                if (Regex.IsMatch(trimmed, @"^\[(high|medium|low)\]", RegexOptions.IgnoreCase))
                {
                    StringBuilder body = new StringBuilder();
                    body.Append(trimmed);
                    int next = idx + 1;
                    while (next < lines.Length)
                    {
                        string peek = lines[next].TrimStart();
                        if (peek.Length == 0) break;
                        if (peek.StartsWith("#", StringComparison.Ordinal)) break;
                        if (peek.StartsWith("Source:", StringComparison.OrdinalIgnoreCase)) break;
                        if (Regex.IsMatch(peek, @"^\[(high|medium|low)\]", RegexOptions.IgnoreCase)) break;
                        body.Append(' ').Append(peek);
                        next++;
                    }
                    notes.Add(body.ToString());
                    idx = next;
                    continue;
                }
                idx++;
            }
            return notes;
        }

        private static bool HasTaggedNotes(string content)
        {
            return !String.IsNullOrEmpty(content)
                && Regex.IsMatch(content, @"^\s*\[(high|medium|low)\]", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }

        private static int CountTaggedNotes(string content)
        {
            if (String.IsNullOrEmpty(content)) return 0;
            return Regex.Matches(content, @"^\s*\[(high|medium|low)\]", RegexOptions.IgnoreCase | RegexOptions.Multiline).Count;
        }

        private static int CountDiffModifiedAndDisabled(string? diffText)
        {
            if (String.IsNullOrEmpty(diffText)) return 0;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(diffText.Trim());
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return 0;
                int n = 0;
                if (doc.RootElement.TryGetProperty("modified", out JsonElement mEl) && mEl.ValueKind == JsonValueKind.Array)
                    n += mEl.GetArrayLength();
                if (doc.RootElement.TryGetProperty("disabled", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.Array)
                    n += dEl.GetArrayLength();
                return n;
            }
            catch (JsonException) { return 0; }
        }

        private static bool HasNoteAt(string content, string noteRef)
        {
            // noteRef shape: "<Section>:<index>" -- locate the section heading then count
            // confidence-tagged note lines until reaching the index.
            if (String.IsNullOrEmpty(content) || String.IsNullOrEmpty(noteRef)) return false;
            int colon = noteRef.LastIndexOf(':');
            if (colon <= 0 || colon >= noteRef.Length - 1) return false;
            string section = noteRef.Substring(0, colon).Trim();
            if (!Int32.TryParse(noteRef.Substring(colon + 1), out int targetIndex) || targetIndex < 0) return false;

            string[] lines = content.Replace("\r\n", "\n").Split('\n');
            bool inSection = false;
            int count = 0;
            foreach (string raw in lines)
            {
                string trimmed = raw.TrimStart();
                if (trimmed.StartsWith("##", StringComparison.Ordinal))
                {
                    string heading = trimmed.TrimStart('#').Trim();
                    inSection = String.Equals(heading, section, StringComparison.OrdinalIgnoreCase);
                    count = 0;
                    continue;
                }
                if (!inSection) continue;
                if (Regex.IsMatch(trimmed, @"^\[(high|medium|low)\]", RegexOptions.IgnoreCase))
                {
                    if (count == targetIndex) return true;
                    count++;
                }
            }
            return false;
        }

        private static string DisableNoteAt(string content, string noteRef, string reason)
        {
            // Best-effort: prepend a `[disabled: <reason>]` marker to the matching note line so
            // the next curate cycle can see the disable trail. When the note can't be located,
            // returns the content unchanged.
            if (String.IsNullOrEmpty(content) || String.IsNullOrEmpty(noteRef)) return content;
            int colon = noteRef.LastIndexOf(':');
            if (colon <= 0) return content;
            string section = noteRef.Substring(0, colon).Trim();
            if (!Int32.TryParse(noteRef.Substring(colon + 1), out int targetIndex) || targetIndex < 0) return content;

            string[] lines = content.Replace("\r\n", "\n").Split('\n');
            bool inSection = false;
            int count = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("##", StringComparison.Ordinal))
                {
                    string heading = trimmed.TrimStart('#').Trim();
                    inSection = String.Equals(heading, section, StringComparison.OrdinalIgnoreCase);
                    count = 0;
                    continue;
                }
                if (!inSection) continue;
                if (Regex.IsMatch(trimmed, @"^\[(high|medium|low)\]", RegexOptions.IgnoreCase))
                {
                    if (count == targetIndex)
                    {
                        if (lines[i].Contains("[disabled:", StringComparison.Ordinal)) return content; // already disabled
                        lines[i] = "[disabled: " + reason + "] " + lines[i].TrimStart();
                        return String.Join("\n", lines);
                    }
                    count++;
                }
            }
            return content;
        }

        private struct FleetCandidateSplit
        {
            public FleetCandidateSplitVerdict Verdict;
            public string PlaybookContent;
            public string RipplesJson;
        }

        private enum FleetCandidateSplitVerdict
        {
            Success = 0,
            Malformed = 1
        }

        private sealed class FleetRippleDisable
        {
            public string VesselId { get; set; } = "";
            public string NoteRef { get; set; } = "";
            public string? Reason { get; set; }
        }

        private struct FleetPromotionGateError
        {
            public string Error;
            public object Details;
        }

        private struct FleetConflictResult
        {
            public object? Conflict;
        }

        private static string? ValidateIdentityCandidateNotes(string content, ReflectionMode mode)
        {
            // Parse note lines: each line beginning with `[high]`, `[medium]`, or `[low]`.
            // For persona-curate: low confidence is rejected; mentioning a captain id
            // (cpt_xxxx pattern) in a note is rejected; <3 supporting missions on a Source:
            // line is rejected.
            // For captain-curate: low is permitted (bias-amplification risk lower).
            string[] lines = content.Replace("\r\n", "\n").Split('\n');
            int idx = 0;
            while (idx < lines.Length)
            {
                string line = lines[idx];
                string trimmed = line.TrimStart();
                Match m = Regex.Match(trimmed, @"^\[(?<c>high|medium|low)\]", RegexOptions.IgnoreCase);
                if (!m.Success) { idx++; continue; }

                string confidence = m.Groups["c"].Value.ToLowerInvariant();
                if (mode == ReflectionMode.PersonaCurate && confidence == "low")
                {
                    return "persona_note_confidence_too_low";
                }

                // Collect this note's body across continuation lines until a blank line, the
                // next note tag, or a section header.
                StringBuilder body = new StringBuilder();
                body.AppendLine(trimmed);
                int next = idx + 1;
                while (next < lines.Length)
                {
                    string peek = lines[next];
                    string peekTrim = peek.TrimStart();
                    if (peekTrim.Length == 0) break;
                    if (peekTrim.StartsWith("#")) break;
                    if (Regex.IsMatch(peekTrim, @"^\[(high|medium|low)\]", RegexOptions.IgnoreCase)) break;
                    body.AppendLine(peek);
                    next++;
                }
                string noteBody = body.ToString();

                if (mode == ReflectionMode.PersonaCurate
                    && Regex.IsMatch(noteBody, @"\bcpt_[A-Za-z0-9_]+", RegexOptions.IgnoreCase))
                {
                    return "persona_note_specifies_captain";
                }

                Match sourceMatch = Regex.Match(noteBody, @"Source:\s*(?<ids>[^\n]+)", RegexOptions.IgnoreCase);
                if (sourceMatch.Success)
                {
                    string idsStr = sourceMatch.Groups["ids"].Value;
                    int idCount = idsStr.Split(',').Count(s => Regex.IsMatch(s.Trim(), @"^msn_[A-Za-z0-9_]+", RegexOptions.IgnoreCase));
                    if (mode == ReflectionMode.PersonaCurate && idCount < 3)
                    {
                        return "persona_note_insufficient_evidence";
                    }
                }

                idx = next;
            }
            return null;
        }

        private async Task<string?> ValidateCounterEvidenceHandlingAsync(
            ReflectionMode mode,
            string targetId,
            Vessel anchorVessel,
            string? diffText,
            string newContent,
            CancellationToken token)
        {
            // Read prior playbook content to identify pre-existing note headings + Source: lines.
            string priorFileName = (mode == ReflectionMode.PersonaCurate ? "persona-" : "captain-")
                + Armada.Core.Memory.ReflectionDispatcher.SanitizeIdentityName(targetId)
                + "-learned.md";
            string tenantId = anchorVessel.TenantId ?? Constants.DefaultTenantId;
            Playbook? prior = await _Database.Playbooks.ReadByFileNameAsync(tenantId, priorFileName, token).ConfigureAwait(false);
            if (prior == null || String.IsNullOrWhiteSpace(prior.Content)) return null;

            // Identify prior note bodies. If a prior note appears verbatim in the new content
            // (substring match), it's preserved unchanged: counter-evidence handling is the
            // consolidator's responsibility but we cannot detect "evidence contradicted N
            // missions" without re-running the miner. Pragmatic gate: if the diff JSON has
            // ZERO modified+disabled entries AND the prior playbook had >=1 confidence-tagged
            // note, require at least one disabled-or-modified entry whenever the prior content
            // is no longer a substring of the new content (i.e. something changed but nothing
            // was explicitly addressed). This catches the obvious "ignored counter-evidence"
            // case the spec calls out.
            string priorContent = prior.Content;
            bool priorHasTaggedNotes = Regex.IsMatch(priorContent, @"^\s*\[(high|medium|low)\]", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!priorHasTaggedNotes) return null;

            int modifiedDisabledCount = 0;
            if (!String.IsNullOrEmpty(diffText))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(diffText.Trim());
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("modified", out JsonElement mEl) && mEl.ValueKind == JsonValueKind.Array)
                            modifiedDisabledCount += mEl.GetArrayLength();
                        if (doc.RootElement.TryGetProperty("disabled", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.Array)
                            modifiedDisabledCount += dEl.GetArrayLength();
                    }
                }
                catch (JsonException) { /* best-effort */ }
            }

            if (modifiedDisabledCount > 0) return null;

            // No modifications/disables proposed. If the new content materially differs from
            // prior (set of note tags differs), we have an unaddressed prior-state delta.
            int priorTagged = Regex.Matches(priorContent, @"^\s*\[(high|medium|low)\]", RegexOptions.IgnoreCase | RegexOptions.Multiline).Count;
            int newTagged = Regex.Matches(newContent, @"^\s*\[(high|medium|low)\]", RegexOptions.IgnoreCase | RegexOptions.Multiline).Count;
            if (priorTagged > newTagged)
            {
                // Notes were dropped silently rather than disabled with a reason.
                return mode == ReflectionMode.PersonaCurate
                    ? "persona_curate_ignored_counter_evidence"
                    : "captain_curate_ignored_counter_evidence";
            }

            return null;
        }

        private async Task<List<object>> DetectVesselIdentityConflictsAsync(
            string targetId,
            ReflectionMode mode,
            string newContent,
            Vessel anchorVessel,
            CancellationToken token)
        {
            List<object> warnings = new List<object>();
            int sampleSize = ReadConflictSampleSize();
            if (sampleSize <= 0) return warnings;

            // Find the top-N vessels this identity has served.
            List<Vessel> candidates = await PickVesselsForIdentityAsync(targetId, mode, sampleSize, token).ConfigureAwait(false);
            if (candidates.Count == 0) return warnings;

            HashSet<string> newShingles = ShingleNGrams(newContent, 3);
            if (newShingles.Count == 0) return warnings;

            double threshold = ReadConflictThreshold();

            foreach (Vessel v in candidates)
            {
                string vesselContent = await ReadLearnedPlaybookContentAsync(v, token).ConfigureAwait(false);
                HashSet<string> vesselShingles = ShingleNGrams(vesselContent, 3);
                if (vesselShingles.Count == 0) continue;

                int intersect = newShingles.Count(s => vesselShingles.Contains(s));
                int union = newShingles.Count + vesselShingles.Count - intersect;
                double jaccard = union <= 0 ? 0.0 : (double)intersect / union;
                if (jaccard >= threshold)
                {
                    warnings.Add(new
                    {
                        warning = "identity_note_conflicts_vessel",
                        vesselId = v.Id,
                        vesselName = v.Name,
                        jaccardSimilarity = Math.Round(jaccard, 4)
                    });
                }
            }

            return warnings;
        }

        private async Task<List<Vessel>> PickVesselsForIdentityAsync(string targetId, ReflectionMode mode, int sampleSize, CancellationToken token)
        {
            // Pick the vessels with the most recent terminal missions for this identity.
            List<Mission> all = await _Database.Missions.EnumerateAsync(token).ConfigureAwait(false);
            IEnumerable<Mission> filtered = all.Where(m => m.Status == MissionStatusEnum.Complete
                || m.Status == MissionStatusEnum.Failed
                || m.Status == MissionStatusEnum.Cancelled);
            if (mode == ReflectionMode.PersonaCurate)
            {
                filtered = filtered.Where(m => String.Equals(m.Persona, targetId, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                filtered = filtered.Where(m => String.Equals(m.CaptainId, targetId, StringComparison.Ordinal));
            }

            Dictionary<string, int> vesselCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Mission m in filtered)
            {
                if (String.IsNullOrEmpty(m.VesselId)) continue;
                if (!vesselCounts.ContainsKey(m.VesselId)) vesselCounts[m.VesselId] = 0;
                vesselCounts[m.VesselId]++;
            }

            List<Vessel> result = new List<Vessel>();
            foreach (KeyValuePair<string, int> kv in vesselCounts.OrderByDescending(p => p.Value).Take(sampleSize))
            {
                Vessel? v = await _Database.Vessels.ReadAsync(kv.Key, token).ConfigureAwait(false);
                if (v != null) result.Add(v);
            }
            return result;
        }

        private int ReadConflictSampleSize()
        {
            // _Database is the only injected dependency; read settings via reflection-free
            // fallback. Default 3 matches ArmadaSettings.IdentityNoteConflictVesselSampleSize.
            return _ConflictSampleSize;
        }

        private double ReadConflictThreshold()
        {
            return _ConflictThreshold;
        }

        // Module-static fallbacks; in practice the dispatch path passes the live settings via
        // ReflectionDispatcher (not threaded into this service yet). These match the v2-F2 spec
        // defaults and are tunable by anyone instantiating ReflectionMemoryService later.
        private static int _ConflictSampleSize = 3;
        private static double _ConflictThreshold = 0.7;

        private static HashSet<string> ShingleNGrams(string text, int n)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrEmpty(text) || n <= 0) return result;
            string normalized = Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9 ]+", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            string[] words = normalized.Split(' ');
            for (int i = 0; i + n <= words.Length; i++)
            {
                result.Add(String.Join(" ", words, i, n));
            }
            return result;
        }

        private async Task EnsureIdentityLearnedPlaybookWiringAsync(ReflectionMode mode, string targetId, string playbookId, CancellationToken token)
        {
            if (mode == ReflectionMode.PersonaCurate)
            {
                Persona? persona = await _Database.Personas.ReadByNameAsync(targetId, token).ConfigureAwait(false);
                if (persona == null) return;
                bool dirty = false;
                if (!String.Equals(persona.LearnedPlaybookId, playbookId, StringComparison.Ordinal))
                {
                    persona.LearnedPlaybookId = playbookId;
                    dirty = true;
                }
                List<SelectedPlaybook> defaults = persona.GetDefaultPlaybooks();
                if (!defaults.Exists(sp => String.Equals(sp.PlaybookId, playbookId, StringComparison.Ordinal)))
                {
                    defaults.Add(new SelectedPlaybook { PlaybookId = playbookId, DeliveryMode = PlaybookDeliveryModeEnum.InstructionWithReference });
                    persona.DefaultPlaybooks = JsonSerializer.Serialize(defaults);
                    dirty = true;
                }
                if (dirty) await _Database.Personas.UpdateAsync(persona, token).ConfigureAwait(false);
                return;
            }

            // Captain side: lazy-create the wiring on first accepted captain-curate.
            Captain? captain = await _Database.Captains.ReadAsync(targetId, token).ConfigureAwait(false);
            if (captain == null) return;
            bool cDirty = false;
            if (!String.Equals(captain.LearnedPlaybookId, playbookId, StringComparison.Ordinal))
            {
                captain.LearnedPlaybookId = playbookId;
                cDirty = true;
            }
            List<SelectedPlaybook> cDefaults = captain.GetDefaultPlaybooks();
            if (!cDefaults.Exists(sp => String.Equals(sp.PlaybookId, playbookId, StringComparison.Ordinal)))
            {
                cDefaults.Add(new SelectedPlaybook { PlaybookId = playbookId, DeliveryMode = PlaybookDeliveryModeEnum.InstructionWithReference });
                captain.DefaultPlaybooks = JsonSerializer.Serialize(cDefaults);
                cDirty = true;
            }
            if (cDirty) await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
        }

        private static ReflectionDiffMetrics ParseIdentityDiffMetrics(string? diffText)
        {
            ReflectionDiffMetrics m = new ReflectionDiffMetrics();
            if (String.IsNullOrEmpty(diffText)) return m;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(diffText.Trim());
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return m;

                if (root.TryGetProperty("added", out JsonElement aEl) && aEl.ValueKind == JsonValueKind.Array)
                    m.NotesAdded = aEl.GetArrayLength();
                if (root.TryGetProperty("modified", out JsonElement modEl) && modEl.ValueKind == JsonValueKind.Array)
                    m.NotesModified = modEl.GetArrayLength();
                if (root.TryGetProperty("disabled", out JsonElement disEl) && disEl.ValueKind == JsonValueKind.Array)
                    m.NotesDisabled = disEl.GetArrayLength();
                if (root.TryGetProperty("missionsExamined", out JsonElement mxEl) && mxEl.ValueKind == JsonValueKind.Number && mxEl.TryGetInt32(out int mx))
                    m.MissionsExamined = mx;
                if (root.TryGetProperty("captainsInScope", out JsonElement csEl) && csEl.ValueKind == JsonValueKind.Number && csEl.TryGetInt32(out int cs))
                    m.CaptainsInScope = cs;
                if (root.TryGetProperty("evidenceConfidence", out JsonElement ecEl) && ecEl.ValueKind == JsonValueKind.String)
                    m.EvidenceConfidence = ecEl.GetString() ?? "low";
            }
            catch (JsonException) { /* best-effort */ }
            return m;
        }

        private sealed class ReflectionDiffMetrics
        {
            public int NotesAdded { get; set; }
            public int NotesModified { get; set; }
            public int NotesDisabled { get; set; }
            public int MissionsExamined { get; set; }
            public int CaptainsInScope { get; set; }
            public string EvidenceConfidence { get; set; } = "low";
        }

        private static string? ValidatePackCurateAntiPatterns(PackCurateCandidate proposed)
        {
            if (proposed.AddHints != null)
            {
                foreach (PackCurateAddHint ah in proposed.AddHints)
                {
                    string pattern = (ah.GoalPattern ?? "").Trim();
                    if (pattern.Length < 3 || pattern == ".*" || String.IsNullOrWhiteSpace(pattern))
                        return "pack_hint_pattern_too_broad";
                    try
                    {
                        _ = new Regex(pattern, RegexOptions.IgnoreCase);
                    }
                    catch (ArgumentException)
                    {
                        return "pack_hint_invalid_regex";
                    }

                    if (ah.MustInclude != null)
                    {
                        foreach (string p in ah.MustInclude)
                        {
                            if (String.IsNullOrWhiteSpace(p)) return "pack_hint_invalid_path";
                        }
                    }
                    if (ah.MustExclude != null)
                    {
                        foreach (string p in ah.MustExclude)
                        {
                            if (String.IsNullOrWhiteSpace(p)) return "pack_hint_invalid_path";
                        }
                    }
                }
            }

            if (proposed.ModifyHints != null)
            {
                foreach (PackCurateModifyHint mh in proposed.ModifyHints)
                {
                    if (mh.Changes?.GoalPattern != null)
                    {
                        string pattern = mh.Changes.GoalPattern.Trim();
                        if (pattern.Length < 3 || pattern == ".*") return "pack_hint_pattern_too_broad";
                        try
                        {
                            _ = new Regex(pattern, RegexOptions.IgnoreCase);
                        }
                        catch (ArgumentException)
                        {
                            return "pack_hint_invalid_regex";
                        }
                    }
                }
            }

            return null;
        }

        private static List<object> RunPackCuratePathValidation(PackCurateCandidate proposed, Vessel vessel)
        {
            List<object> warnings = new List<object>();
            HashSet<string> repoFiles = TryListRepoFiles(vessel);
            if (repoFiles.Count == 0) return warnings; // best-effort: skip when repo unreachable

            void CheckGlobs(string hintRef, IReadOnlyList<string>? globs)
            {
                if (globs == null) return;
                List<string> unmatched = new List<string>();
                foreach (string glob in globs)
                {
                    if (String.IsNullOrWhiteSpace(glob)) continue;
                    if (!GlobMatchesAnyFile(glob, repoFiles))
                        unmatched.Add(glob);
                }
                if (unmatched.Count > 0)
                {
                    warnings.Add(new
                    {
                        warning = "pack_hint_no_matches",
                        hintReference = hintRef,
                        unmatchedPaths = unmatched,
                        branchChecked = vessel.DefaultBranch ?? "main"
                    });
                }
            }

            if (proposed.AddHints != null)
            {
                for (int i = 0; i < proposed.AddHints.Count; i++)
                {
                    PackCurateAddHint ah = proposed.AddHints[i];
                    CheckGlobs("addHints[" + i + "].mustInclude", ah.MustInclude);
                    CheckGlobs("addHints[" + i + "].mustExclude", ah.MustExclude);
                }
            }
            if (proposed.ModifyHints != null)
            {
                for (int i = 0; i < proposed.ModifyHints.Count; i++)
                {
                    PackCurateModifyHint mh = proposed.ModifyHints[i];
                    CheckGlobs("modifyHints[" + i + "].changes.mustInclude", mh.Changes?.MustInclude);
                    CheckGlobs("modifyHints[" + i + "].changes.mustExclude", mh.Changes?.MustExclude);
                }
            }
            return warnings;
        }

        private static List<object> RunPackCurateConflictDetection(PackCurateCandidate proposed, List<VesselPackHint> existingHints)
        {
            List<object> warnings = new List<object>();
            if (proposed.AddHints == null) return warnings;
            for (int i = 0; i < proposed.AddHints.Count; i++)
            {
                PackCurateAddHint ah = proposed.AddHints[i];
                if (ah.MustInclude == null || ah.MustInclude.Count == 0) continue;
                foreach (VesselPackHint existing in existingHints)
                {
                    if (!existing.Active) continue;
                    string existingPattern = existing.GoalPattern ?? "";
                    string newPattern = ah.GoalPattern ?? "";
                    bool patternsOverlap = String.Equals(existingPattern, newPattern, StringComparison.OrdinalIgnoreCase)
                        || existingPattern.IndexOf(newPattern, StringComparison.OrdinalIgnoreCase) >= 0
                        || newPattern.IndexOf(existingPattern, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!patternsOverlap) continue;

                    List<string> existingExcl = existing.GetMustExclude();
                    List<string> overlap = new List<string>();
                    foreach (string inc in ah.MustInclude)
                    {
                        if (existingExcl.Contains(inc, StringComparer.OrdinalIgnoreCase))
                            overlap.Add(inc);
                    }
                    if (overlap.Count == 0) continue;

                    warnings.Add(new
                    {
                        warning = "pack_hint_conflict",
                        hintA = "addHints[" + i + "]",
                        hintB = existing.Id,
                        overlappingPaths = overlap,
                        resolution = "higher priority wins; if equal, exclude wins"
                    });
                }
            }
            return warnings;
        }

        private static HashSet<string> TryListRepoFiles(Vessel vessel)
        {
            HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrEmpty(vessel.LocalPath)) return files;
            string defaultBranch = String.IsNullOrEmpty(vessel.DefaultBranch) ? "main" : vessel.DefaultBranch!;
            try
            {
                if (!System.IO.Directory.Exists(vessel.LocalPath)) return files;
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = vessel.LocalPath!,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("ls-tree");
                psi.ArgumentList.Add("-r");
                psi.ArgumentList.Add("--name-only");
                psi.ArgumentList.Add(defaultBranch);

                using System.Diagnostics.Process? proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return files;
                string stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(10000);
                if (proc.ExitCode != 0) return files;
                foreach (string raw in stdout.Split('\n'))
                {
                    string line = raw.TrimEnd('\r').Trim();
                    if (!String.IsNullOrEmpty(line)) files.Add(line);
                }
            }
            catch (Exception)
            {
                // best-effort
            }
            return files;
        }

        private static bool GlobMatchesAnyFile(string glob, HashSet<string> files)
        {
            Matcher matcher = new Matcher();
            matcher.AddInclude(glob);
            foreach (string f in files)
            {
                PatternMatchingResult result = matcher.Match(f);
                if (result.HasMatches) return true;
            }
            return false;
        }

        private static int ExtractMissionsExaminedFromDiff(string? agentOutput, IReflectionOutputParser parser)
        {
            if (String.IsNullOrEmpty(agentOutput)) return 0;
            try
            {
                ReflectionOutputParseResult parsed = parser.Parse(agentOutput);
                if (parsed.Verdict != ReflectionOutputParseVerdict.Success) return 0;
                if (String.IsNullOrEmpty(parsed.ReflectionsDiffText)) return 0;
                using JsonDocument doc = JsonDocument.Parse(parsed.ReflectionsDiffText.Trim());
                if (doc.RootElement.TryGetProperty("missionsExamined", out JsonElement el)
                    && el.ValueKind == JsonValueKind.Number
                    && el.TryGetInt32(out int v))
                {
                    return v;
                }
            }
            catch (Exception) { }
            return 0;
        }

        private static string ExtractEvidenceConfidenceFromDiff(string? agentOutput, IReflectionOutputParser parser)
        {
            if (String.IsNullOrEmpty(agentOutput)) return "low";
            try
            {
                ReflectionOutputParseResult parsed = parser.Parse(agentOutput);
                if (parsed.Verdict != ReflectionOutputParseVerdict.Success) return "low";
                if (String.IsNullOrEmpty(parsed.ReflectionsDiffText)) return "low";
                using JsonDocument doc = JsonDocument.Parse(parsed.ReflectionsDiffText.Trim());
                if (doc.RootElement.TryGetProperty("evidenceConfidence", out JsonElement el)
                    && el.ValueKind == JsonValueKind.String)
                {
                    return el.GetString() ?? "low";
                }
            }
            catch (Exception) { }
            return "low";
        }

        private static readonly JsonSerializerOptions _PackCurateJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

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
            EnumerationQuery acceptedQuery = new EnumerationQuery
            {
                MissionId = missionId,
                EventType = _ReflectionAcceptedEvent,
                PageNumber = 1,
                PageSize = 1
            };
            EnumerationResult<ArmadaEvent> acceptedPage = await _Database.Events.EnumerateAsync(acceptedQuery, token).ConfigureAwait(false);
            if (acceptedPage.Objects.Count > 0)
                return true;

            EnumerationQuery rejectedQuery = new EnumerationQuery
            {
                MissionId = missionId,
                EventType = _ReflectionRejectedEvent,
                PageNumber = 1,
                PageSize = 1
            };
            EnumerationResult<ArmadaEvent> rejectedPage = await _Database.Events.EnumerateAsync(rejectedQuery, token).ConfigureAwait(false);
            return rejectedPage.Objects.Count > 0;
        }

        private static string SanitizeName(string name)
        {
            string lower = name.ToLowerInvariant();
            string replaced = Regex.Replace(lower, "[^a-z0-9]+", "-");
            return replaced.Trim('-');
        }

        private async Task<ReflectionMode?> ResolveMissionDispatchedModeAsync(string missionId, CancellationToken token)
        {
            EnumerationQuery query = new EnumerationQuery
            {
                MissionId = missionId,
                EventType = _ReflectionDispatchedEvent,
                PageNumber = 1,
                PageSize = 5
            };
            EnumerationResult<ArmadaEvent> page = await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false);
            foreach (ArmadaEvent evt in page.Objects)
            {
                if (String.IsNullOrEmpty(evt.Payload)) continue;
                try
                {
                    DispatchedPayload? payload = JsonSerializer.Deserialize<DispatchedPayload>(evt.Payload);
                    ReflectionMode? mode = ParseModeString(payload?.Mode);
                    if (mode.HasValue) return mode;
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            return null;
        }

        /// <summary>
        /// Parse a wire-string mode value into a <see cref="ReflectionMode"/>.
        /// Returns null when the value is missing or unrecognized.
        /// </summary>
        /// <param name="value">Wire string from MCP input or event payload.</param>
        /// <returns>Parsed mode, or null when not parseable.</returns>
        public static ReflectionMode? ParseModeString(string? value)
        {
            if (String.IsNullOrEmpty(value)) return null;
            string trimmed = value.Trim();
            if (String.Equals(trimmed, "consolidate", StringComparison.OrdinalIgnoreCase))
                return ReflectionMode.Consolidate;
            if (String.Equals(trimmed, "reorganize", StringComparison.OrdinalIgnoreCase))
                return ReflectionMode.Reorganize;
            if (String.Equals(trimmed, "consolidate-and-reorganize", StringComparison.OrdinalIgnoreCase)
                || String.Equals(trimmed, "consolidateandreorganize", StringComparison.OrdinalIgnoreCase))
                return ReflectionMode.ConsolidateAndReorganize;
            if (String.Equals(trimmed, "pack-curate", StringComparison.OrdinalIgnoreCase)
                || String.Equals(trimmed, "packcurate", StringComparison.OrdinalIgnoreCase))
                return ReflectionMode.PackCurate;
            if (String.Equals(trimmed, "persona-curate", StringComparison.OrdinalIgnoreCase)
                || String.Equals(trimmed, "personacurate", StringComparison.OrdinalIgnoreCase))
                return ReflectionMode.PersonaCurate;
            if (String.Equals(trimmed, "captain-curate", StringComparison.OrdinalIgnoreCase)
                || String.Equals(trimmed, "captaincurate", StringComparison.OrdinalIgnoreCase))
                return ReflectionMode.CaptainCurate;
            if (String.Equals(trimmed, "fleet-curate", StringComparison.OrdinalIgnoreCase)
                || String.Equals(trimmed, "fleetcurate", StringComparison.OrdinalIgnoreCase))
                return ReflectionMode.FleetCurate;
            return null;
        }

        /// <summary>
        /// Convert a <see cref="ReflectionMode"/> to its canonical wire-string form.
        /// </summary>
        /// <param name="mode">Mode to format.</param>
        /// <returns>Canonical lowercase form used in event payloads and MCP responses.</returns>
        public static string ModeToWireString(ReflectionMode mode)
        {
            return mode switch
            {
                ReflectionMode.Reorganize => "reorganize",
                ReflectionMode.ConsolidateAndReorganize => "consolidate-and-reorganize",
                ReflectionMode.PackCurate => "pack-curate",
                ReflectionMode.PersonaCurate => "persona-curate",
                ReflectionMode.CaptainCurate => "captain-curate",
                ReflectionMode.FleetCurate => "fleet-curate",
                _ => "consolidate"
            };
        }

        private async Task<DispatchedPayload?> ReadDispatchedPayloadAsync(string missionId, CancellationToken token)
        {
            EnumerationQuery query = new EnumerationQuery
            {
                MissionId = missionId,
                EventType = _ReflectionDispatchedEvent,
                PageNumber = 1,
                PageSize = 5
            };
            EnumerationResult<ArmadaEvent> page = await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false);
            foreach (ArmadaEvent evt in page.Objects)
            {
                if (String.IsNullOrEmpty(evt.Payload)) continue;
                try
                {
                    return JsonSerializer.Deserialize<DispatchedPayload>(evt.Payload);
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            return null;
        }

        private static ReorganizeGateResult ValidateReorganizeDiff(string diffText, string priorPlaybookContent)
        {
            string trimmed = diffText.Trim();
            if (trimmed.Length == 0) return new ReorganizeGateResult();

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(trimmed);
            }
            catch (JsonException)
            {
                return new ReorganizeGateResult();
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return new ReorganizeGateResult();

                List<string> nonStructuralAdded = new List<string>();
                if (doc.RootElement.TryGetProperty("added", out JsonElement addedEl) && addedEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement entry in addedEl.EnumerateArray())
                    {
                        string? text = entry.ValueKind == JsonValueKind.String ? entry.GetString() : entry.ToString();
                        if (String.IsNullOrWhiteSpace(text)) continue;
                        if (!IsStructuralMarker(text!))
                        {
                            nonStructuralAdded.Add(text!);
                        }
                    }

                    if (nonStructuralAdded.Count > 0)
                    {
                        return new ReorganizeGateResult
                        {
                            Error = "reorganize_added_facts",
                            Details = new { offendingEntries = nonStructuralAdded }
                        };
                    }
                }

                List<string> invalidRemovedEntries = new List<string>();
                if (doc.RootElement.TryGetProperty("removed", out JsonElement removedEl) && removedEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement entry in removedEl.EnumerateArray())
                    {
                        string? text = entry.ValueKind == JsonValueKind.String ? entry.GetString() : entry.ToString();
                        if (String.IsNullOrWhiteSpace(text)) continue;
                        if (!PlaybookContainsSummary(priorPlaybookContent, text!))
                        {
                            invalidRemovedEntries.Add(text!);
                        }
                    }

                    if (invalidRemovedEntries.Count > 0)
                    {
                        return new ReorganizeGateResult
                        {
                            Error = "reorganize_invalid_remove",
                            Details = new { offendingEntries = invalidRemovedEntries }
                        };
                    }
                }

                List<string> invalidMergeSources = new List<string>();
                if (doc.RootElement.TryGetProperty("merged", out JsonElement mergedEl) && mergedEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement mergedEntry in mergedEl.EnumerateArray())
                    {
                        if (mergedEntry.ValueKind != JsonValueKind.Object) continue;
                        if (!mergedEntry.TryGetProperty("from", out JsonElement fromEl)) continue;
                        if (fromEl.ValueKind != JsonValueKind.Array) continue;

                        foreach (JsonElement source in fromEl.EnumerateArray())
                        {
                            string? text = source.ValueKind == JsonValueKind.String ? source.GetString() : source.ToString();
                            if (String.IsNullOrWhiteSpace(text)) continue;
                            if (!PlaybookContainsSummary(priorPlaybookContent, text!))
                            {
                                invalidMergeSources.Add(text!);
                            }
                        }
                    }

                    if (invalidMergeSources.Count > 0)
                    {
                        return new ReorganizeGateResult
                        {
                            Error = "reorganize_invalid_merge_source",
                            Details = new { offendingEntries = invalidMergeSources }
                        };
                    }
                }
            }

            return new ReorganizeGateResult();
        }

        private static bool IsStructuralMarker(string entry)
        {
            string trimmed = entry.TrimStart();
            if (trimmed.Length == 0) return true;

            if (trimmed.Length > 1 && trimmed[0] == '#')
            {
                int hashCount = 0;
                while (hashCount < trimmed.Length && trimmed[hashCount] == '#') hashCount++;
                if (hashCount < trimmed.Length && trimmed[hashCount] == ' ') return true;
            }

            foreach (char ch in trimmed)
            {
                if (ch != ' ' && ch != '\t' && ch != '*' && ch != '-' && ch != '=' && ch != '_' && ch != '|') return false;
            }

            return true;
        }

        private static bool PlaybookContainsSummary(string playbook, string summary)
        {
            if (String.IsNullOrEmpty(playbook)) return false;
            string normalizedSummary = summary.Trim();
            if (normalizedSummary.Length == 0) return true;
            return playbook.Contains(normalizedSummary, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<DualJudgeGateResult> EvaluateDualJudgeGateAsync(Mission workerMission, CancellationToken token)
        {
            DualJudgeGateResult result = new DualJudgeGateResult();
            if (String.IsNullOrEmpty(workerMission.VoyageId))
            {
                result.AllPassed = false;
                return result;
            }

            List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(workerMission.VoyageId, token).ConfigureAwait(false);
            List<Mission> judgeSiblings = voyageMissions
                .Where(m => String.Equals(m.Persona, "Judge", StringComparison.OrdinalIgnoreCase))
                .Where(m => String.Equals(m.DependsOnMissionId, workerMission.Id, StringComparison.Ordinal))
                .ToList();

            if (judgeSiblings.Count == 0)
            {
                judgeSiblings = voyageMissions
                    .Where(m => String.Equals(m.Persona, "Judge", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            List<JudgeVerdictRecord> verdicts = new List<JudgeVerdictRecord>();
            int passCount = 0;
            foreach (Mission judge in judgeSiblings)
            {
                string verdict = ParseExplicitVerdictMarker(judge.AgentOutput);
                if (verdict == "PASS" && judge.Status == Enums.MissionStatusEnum.Complete)
                {
                    passCount++;
                }
                else if (verdict == "PENDING" && judge.Status == Enums.MissionStatusEnum.Complete)
                {
                    verdict = "PASS";
                    passCount++;
                }

                verdicts.Add(new JudgeVerdictRecord
                {
                    MissionId = judge.Id,
                    CaptainId = judge.CaptainId,
                    Verdict = verdict
                });
            }

            result.Verdicts = verdicts;
            result.AllPassed = verdicts.Count >= 2 && passCount == verdicts.Count;
            return result;
        }

        private static string ParseExplicitVerdictMarker(string? agentOutput)
        {
            if (String.IsNullOrEmpty(agentOutput)) return "PENDING";

            string[] lines = agentOutput.Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                    line,
                    @"^\[ARMADA:VERDICT\]\s+(?<v>PASS|FAIL|NEEDS_REVISION)\s*$");
                if (m.Success) return m.Groups["v"].Value;
            }

            return "PENDING";
        }

        private static ReflectionMetrics ComputeReflectionMetrics(
            string priorContent,
            string newContent,
            ReflectionMode mode,
            string? diffText)
        {
            ReflectionMetrics metrics = new ReflectionMetrics
            {
                EntriesBefore = CountPlaybookEntries(priorContent),
                EntriesAfter = CountPlaybookEntries(newContent),
                TokensBefore = (priorContent?.Length ?? 0) / 4,
                TokensAfter = (newContent?.Length ?? 0) / 4
            };

            if (mode == ReflectionMode.Consolidate)
            {
                metrics.Removed = 0;
                metrics.Merged = 0;
                metrics.AddedFromReorganize = 0;
                return metrics;
            }

            if (!String.IsNullOrEmpty(diffText))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(diffText.Trim());
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("removed", out JsonElement removedEl) && removedEl.ValueKind == JsonValueKind.Array)
                            metrics.Removed = removedEl.GetArrayLength();
                        if (doc.RootElement.TryGetProperty("merged", out JsonElement mergedEl) && mergedEl.ValueKind == JsonValueKind.Array)
                            metrics.Merged = mergedEl.GetArrayLength();
                        if (mode == ReflectionMode.Reorganize
                            && doc.RootElement.TryGetProperty("added", out JsonElement addedEl)
                            && addedEl.ValueKind == JsonValueKind.Array)
                        {
                            metrics.AddedFromReorganize = addedEl.GetArrayLength();
                        }
                    }
                }
                catch (JsonException)
                {
                    // best-effort metrics; leave defaults
                }
            }

            if (mode == ReflectionMode.ConsolidateAndReorganize)
            {
                metrics.AddedFromReorganize = -1;
            }

            return metrics;
        }

        private static int CountPlaybookEntries(string content)
        {
            if (String.IsNullOrEmpty(content)) return 0;
            int count = 0;
            foreach (string raw in content.Split('\n'))
            {
                string line = raw.TrimStart();
                if (line.Length == 0) continue;
                if (line.StartsWith("- ", StringComparison.Ordinal)
                    || line.StartsWith("* ", StringComparison.Ordinal)
                    || line.StartsWith("+ ", StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private sealed class ReorganizeGateResult
        {
            public string? Error { get; set; }

            public object? Details { get; set; }
        }

        private sealed class DualJudgeGateResult
        {
            public bool AllPassed { get; set; }

            public List<JudgeVerdictRecord> Verdicts { get; set; } = new List<JudgeVerdictRecord>();
        }

        private sealed class ReflectionMetrics
        {
            public int EntriesBefore { get; set; }

            public int EntriesAfter { get; set; }

            public int Removed { get; set; }

            public int Merged { get; set; }

            public int AddedFromReorganize { get; set; }

            public int TokensBefore { get; set; }

            public int TokensAfter { get; set; }
        }

        #endregion

        #region Private-Classes

        private sealed class RejectionPayload
        {
            [System.Text.Json.Serialization.JsonPropertyName("missionId")]
            public string? MissionId { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("reason")]
            public string? Reason { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("mode")]
            public string? Mode { get; set; }
        }

        private sealed class DispatchedPayload
        {
            [System.Text.Json.Serialization.JsonPropertyName("mode")]
            public string? Mode { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("dualJudge")]
            public bool? DualJudge { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("targetType")]
            public string? TargetType { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("targetId")]
            public string? TargetId { get; set; }
        }

        #endregion
    }
}
