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
        }

        #endregion
    }
}
