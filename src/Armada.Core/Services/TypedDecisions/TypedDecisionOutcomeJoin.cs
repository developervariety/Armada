namespace Armada.Core.Services.TypedDecisions
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Joins each recorded typed-decision call to what later happened to the record it judged: the mission's
    /// landing result, an incident linked to the mission, a rescue mission that names it as its parent, or the
    /// objective's final status. Reading only; it changes no record and sends nothing off the host.
    /// <para>
    /// The subject of a call is the <c>subject_type</c> and <c>subject_id</c> the recorder writes. A call
    /// recorded before those fields existed falls back to its mission, then its objective, so older calls join
    /// too.
    /// </para>
    /// </summary>
    public sealed class TypedDecisionOutcomeJoin
    {
        #region Public-Members

        /// <summary>Default and upper bound of calls one report reads.</summary>
        public const int DefaultMaxEvents = 20000;

        #endregion

        #region Private-Members

        private const int _PageSize = 1000;
        private const int _IncidentPageSize = 200;
        private const int _MaxIncidentPages = 200;

        private static readonly string[] _CallEventTypes =
        {
            TypedDecisionRecorder.EventTypeGated,
            TypedDecisionRecorder.EventTypeRuleKept,
            TypedDecisionRecorder.EventTypeRuleKeptLegacy,
            TypedDecisionRecorder.EventTypeUnavailable,
            TypedDecisionRecorder.EventTypeCaptain
        };

        private readonly DatabaseDriver _Database;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the join over one database.</summary>
        /// <param name="database">The database holding the events, missions, objectives and incidents.</param>
        public TypedDecisionOutcomeJoin(DatabaseDriver database)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the outcome join for the calls recorded since <paramref name="sinceUtc"/>, newest first, up to
        /// <paramref name="maxEvents"/> calls.
        /// </summary>
        /// <param name="sinceUtc">Start of the window, UTC.</param>
        /// <param name="decision">One decision point to report, or null for every decision.</param>
        /// <param name="maxEvents">The most calls to read; clamped to [1, <see cref="DefaultMaxEvents"/>].</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The report.</returns>
        public async Task<TypedDecisionOutcomeReport> BuildAsync(DateTime sinceUtc, string? decision, int maxEvents = DefaultMaxEvents, CancellationToken token = default)
        {
            int limit = Math.Clamp(maxEvents, 1, DefaultMaxEvents);
            TypedDecisionOutcomeReport report = new TypedDecisionOutcomeReport { SinceUtc = sinceUtc, MaxEvents = limit };
            string? onlyDecision = String.IsNullOrWhiteSpace(decision) ? null : decision.Trim();

            List<RecordedCall> calls = await ReadCallsAsync(sinceUtc, onlyDecision, limit, report, token).ConfigureAwait(false);
            report.EventsRead = calls.Count;
            if (calls.Count == 0) return report;

            HashSet<string> incidentMissions = await ReadIncidentMissionIdsAsync(token).ConfigureAwait(false);
            Dictionary<string, SubjectOutcome> outcomes = new Dictionary<string, SubjectOutcome>(StringComparer.Ordinal);
            Dictionary<string, TypedDecisionOutcomeRow> rows = new Dictionary<string, TypedDecisionOutcomeRow>(StringComparer.Ordinal);

            foreach (RecordedCall call in calls)
            {
                if (!rows.TryGetValue(call.Decision, out TypedDecisionOutcomeRow? row))
                {
                    row = new TypedDecisionOutcomeRow { Name = call.Decision };
                    rows[call.Decision] = row;
                }

                SubjectOutcome? outcome = null;
                if (call.SubjectType != null && call.SubjectId != null)
                {
                    string key = call.SubjectType + "|" + call.SubjectId;
                    if (!outcomes.TryGetValue(key, out outcome))
                    {
                        outcome = await ResolveAsync(call.SubjectType, call.SubjectId, incidentMissions, token).ConfigureAwait(false);
                        outcomes[key] = outcome;
                    }
                }

                Count(row, call, outcome);
                TypedDecisionOutcomeRow band = BandRow(row, call.Confidence);
                Count(band, call, outcome);
            }

            report.Decisions = rows.Values.OrderBy(row => row.Name, StringComparer.Ordinal).ToList();
            foreach (TypedDecisionOutcomeRow row in report.Decisions)
                row.ByConfidenceBand = row.ByConfidenceBand.OrderBy(band => BandOrder(band.Name)).ToList();
            return report;
        }

        /// <summary>The confidence band label for a model action confidence.</summary>
        /// <param name="confidence">The confidence, or null when the call recorded none.</param>
        /// <returns>The band label.</returns>
        public static string BandOf(double? confidence)
        {
            if (!confidence.HasValue) return "none";
            double value = confidence.Value;
            if (value >= 0.9) return ">=0.90";
            if (value >= 0.8) return "0.80-0.90";
            if (value >= 0.5) return "0.50-0.80";
            return "<0.50";
        }

        #endregion

        #region Private-Methods

        private async Task<List<RecordedCall>> ReadCallsAsync(DateTime sinceUtc, string? onlyDecision, int limit, TypedDecisionOutcomeReport report, CancellationToken token)
        {
            List<RecordedCall> calls = new List<RecordedCall>();
            foreach (string eventType in _CallEventTypes)
            {
                for (int page = 1; ; page++)
                {
                    EnumerationResult<ArmadaEvent> result = await _Database.Events.EnumerateAsync(new EnumerationQuery
                    {
                        EventType = eventType,
                        CreatedAfter = sinceUtc,
                        PageNumber = page,
                        PageSize = _PageSize
                    }, token).ConfigureAwait(false);

                    foreach (ArmadaEvent evt in result.Objects)
                    {
                        RecordedCall? call = ReadCall(evt);
                        if (call == null) continue;
                        if (onlyDecision != null && !String.Equals(call.Decision, onlyDecision, StringComparison.Ordinal)) continue;
                        if (calls.Count >= limit)
                        {
                            report.Truncated = true;
                            return calls;
                        }
                        calls.Add(call);
                    }

                    if (result.Objects.Count < _PageSize) break;
                }
            }
            return calls;
        }

        private static RecordedCall? ReadCall(ArmadaEvent evt)
        {
            CallPayload? payload = null;
            if (!String.IsNullOrWhiteSpace(evt.Payload))
            {
                try
                {
                    payload = JsonSerializer.Deserialize<CallPayload>(evt.Payload);
                }
                catch (JsonException)
                {
                    payload = null;
                }
            }
            if (payload == null || String.IsNullOrWhiteSpace(payload.Decision)) return null;

            string? subjectType = Clean(payload.SubjectType);
            string? subjectId = Clean(payload.SubjectId);
            if (subjectType == null || subjectId == null)
            {
                string? missionId = Clean(evt.MissionId) ?? Clean(payload.MissionId);
                string? objectiveId = Clean(payload.ObjectiveId);
                if (missionId != null)
                {
                    subjectType = TypedDecisionRecorder.SubjectTypeMission;
                    subjectId = missionId;
                }
                else if (objectiveId != null)
                {
                    subjectType = TypedDecisionRecorder.SubjectTypeObjective;
                    subjectId = objectiveId;
                }
                else
                {
                    subjectType = null;
                    subjectId = null;
                }
            }

            return new RecordedCall
            {
                Decision = payload.Decision!,
                EventType = evt.EventType,
                SubjectType = subjectType,
                SubjectId = subjectId,
                Confidence = payload.Confidence ?? ConfidenceFromMessage(evt.Message)
            };
        }

        // Calls recorded before the payload carried the action confidence still name it on the message line.
        private static double? ConfidenceFromMessage(string? message)
        {
            if (String.IsNullOrEmpty(message)) return null;
            int at = message.IndexOf(" conf=", StringComparison.Ordinal);
            if (at < 0) return null;
            string rest = message.Substring(at + 6);
            int end = rest.IndexOf(' ');
            if (end >= 0) rest = rest.Substring(0, end);
            return Double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
        }

        private async Task<HashSet<string>> ReadIncidentMissionIdsAsync(CancellationToken token)
        {
            HashSet<string> missions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IncidentService incidents = new IncidentService(_Database);
            AuthContext operatorScope = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "TypedDecisionOutcomeJoin");
            for (int page = 1; page <= _MaxIncidentPages; page++)
            {
                EnumerationResult<Incident> result = await incidents.EnumerateAsync(operatorScope, new IncidentQuery
                {
                    PageNumber = page,
                    PageSize = _IncidentPageSize
                }, token).ConfigureAwait(false);
                foreach (Incident incident in result.Objects)
                {
                    if (!String.IsNullOrWhiteSpace(incident.MissionId)) missions.Add(incident.MissionId!.Trim());
                }
                if (result.Objects.Count < _IncidentPageSize) break;
            }
            return missions;
        }

        private async Task<SubjectOutcome> ResolveAsync(string subjectType, string subjectId, HashSet<string> incidentMissions, CancellationToken token)
        {
            if (String.Equals(subjectType, TypedDecisionRecorder.SubjectTypeMission, StringComparison.Ordinal))
            {
                Mission? mission = await _Database.Missions.ReadAsync(subjectId, token).ConfigureAwait(false);
                if (mission == null) return new SubjectOutcome { Missing = true };
                List<MissionSummary> children = await _Database.Missions.EnumerateMissionSummariesByParentAsync(subjectId, token).ConfigureAwait(false);
                return new SubjectOutcome
                {
                    Joined = IsTerminal(mission.Status),
                    Landed = mission.Status == MissionStatusEnum.Complete,
                    Failed = mission.Status == MissionStatusEnum.Failed || mission.Status == MissionStatusEnum.LandingFailed,
                    Cancelled = mission.Status == MissionStatusEnum.Cancelled,
                    Incident = incidentMissions.Contains(subjectId),
                    Rescued = children.Count > 0
                };
            }

            if (String.Equals(subjectType, TypedDecisionRecorder.SubjectTypeObjective, StringComparison.Ordinal))
            {
                Objective? objective = await _Database.Objectives.ReadAsync(subjectId, token).ConfigureAwait(false);
                if (objective == null) return new SubjectOutcome { Missing = true };
                return new SubjectOutcome
                {
                    Joined = objective.Status == ObjectiveStatusEnum.Completed || objective.Status == ObjectiveStatusEnum.Cancelled,
                    ObjectiveCompleted = objective.Status == ObjectiveStatusEnum.Completed,
                    Cancelled = objective.Status == ObjectiveStatusEnum.Cancelled
                };
            }

            // Any other subject (a papercut group, a merge entry) is named for the record but has
            // no landing outcome to join.
            return new SubjectOutcome();
        }

        private static bool IsTerminal(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Complete
                || status == MissionStatusEnum.Failed
                || status == MissionStatusEnum.LandingFailed
                || status == MissionStatusEnum.Cancelled;
        }

        private static void Count(TypedDecisionOutcomeRow row, RecordedCall call, SubjectOutcome? outcome)
        {
            row.Events++;
            row.ByEventType.TryGetValue(call.EventType, out int byType);
            row.ByEventType[call.EventType] = byType + 1;
            if (call.SubjectType == null) return;

            row.WithSubject++;
            row.BySubjectType.TryGetValue(call.SubjectType, out int bySubject);
            row.BySubjectType[call.SubjectType] = bySubject + 1;
            if (outcome == null) return;
            if (outcome.Missing) row.SubjectMissing++;
            if (outcome.Joined) row.Joined++;
            if (outcome.Landed) row.Landed++;
            if (outcome.Failed) row.Failed++;
            if (outcome.Cancelled) row.Cancelled++;
            if (outcome.ObjectiveCompleted) row.ObjectiveCompleted++;
            if (outcome.Incident) row.WithIncident++;
            if (outcome.Rescued) row.Rescued++;
        }

        private static TypedDecisionOutcomeRow BandRow(TypedDecisionOutcomeRow row, double? confidence)
        {
            string label = BandOf(confidence);
            TypedDecisionOutcomeRow? band = row.ByConfidenceBand.FirstOrDefault(item => item.Name == label);
            if (band == null)
            {
                band = new TypedDecisionOutcomeRow { Name = label };
                row.ByConfidenceBand.Add(band);
            }
            return band;
        }

        private static int BandOrder(string label)
        {
            switch (label)
            {
                case "<0.50": return 0;
                case "0.50-0.80": return 1;
                case "0.80-0.90": return 2;
                case ">=0.90": return 3;
                default: return 4;
            }
        }

        private static string? Clean(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        #endregion

        #region Private-Types

        private sealed class RecordedCall
        {
            public string Decision { get; init; } = String.Empty;
            public string EventType { get; init; } = String.Empty;
            public string? SubjectType { get; init; }
            public string? SubjectId { get; init; }
            public double? Confidence { get; init; }
        }

        private sealed class SubjectOutcome
        {
            public bool Missing { get; init; }
            public bool Joined { get; init; }
            public bool Landed { get; init; }
            public bool Failed { get; init; }
            public bool Cancelled { get; init; }
            public bool ObjectiveCompleted { get; init; }
            public bool Incident { get; init; }
            public bool Rescued { get; init; }
        }

        private sealed class CallPayload
        {
            [JsonPropertyName("decision")]
            public string? Decision { get; set; }

            [JsonPropertyName("subject_type")]
            public string? SubjectType { get; set; }

            [JsonPropertyName("subject_id")]
            public string? SubjectId { get; set; }

            [JsonPropertyName("mission_id")]
            public string? MissionId { get; set; }

            [JsonPropertyName("objective_id")]
            public string? ObjectiveId { get; set; }

            [JsonPropertyName("confidence")]
            public double? Confidence { get; set; }
        }

        #endregion
    }
}
