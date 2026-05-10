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
                vesselId = vessel.Id,
                appliedContentLength = contentToApply.Length
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
                appliedContentLength = 0
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

        internal static ReflectionMode? ParseModeString(string? value)
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
                _ => "consolidate"
            };
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
