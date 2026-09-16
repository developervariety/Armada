namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The outcome of one Recorder memory review, for logging and tests.
    /// </summary>
    public sealed class RecorderMemoryReviewResult
    {
        /// <summary>Records the model was asked about.</summary>
        public int Reviewed { get; set; } = 0;

        /// <summary>Records whose salience was lowered.</summary>
        public int SalienceLowered { get; set; } = 0;

        /// <summary>Records linked to an existing duplicate.</summary>
        public int DuplicatesLinked { get; set; } = 0;

        /// <summary>Memory proposals stored for the owner.</summary>
        public int Proposed { get; set; } = 0;

        /// <summary>A short reason for the outcome.</summary>
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// D23 seam B, the <c>memory_review</c> decision. After a Recorder-stage mission completes, it reads
    /// the native memory records that mission wrote and asks the four D23 questions for each one: does
    /// the type fit, does it duplicate an existing record, will it go stale, and does it belong in the
    /// owner's external AI-Memory.
    ///
    /// Its only effects, in Gate mode at or above the threshold, are: lower a record's salience (a
    /// duplicate, a stale fact, or a wrong type), link a duplicate to the record it repeats with a
    /// <c>duplicate-of:</c> tag, and store a D18 memory proposal for a record that belongs in
    /// AI-Memory. It never deletes a record, never changes a record's content, summary, type, topic, or
    /// key, never raises salience, and never writes AI-Memory. The decision ships Off; Off calls
    /// nothing and reads nothing. An unavailable model stops the pass and changes nothing. Never throws.
    /// </summary>
    public sealed class RecorderMemoryReviewAdapter
    {
        #region Public-Members

        /// <summary>The decision-point name in the <c>typedDecisions.decisions</c> settings map.</summary>
        public const string DecisionPoint = "memory_review";

        /// <summary>Tag prefix that links a duplicate to the record it repeats.</summary>
        public const string DuplicateTagPrefix = "duplicate-of:";

        /// <summary>Salience ceiling applied to a record read as a duplicate.</summary>
        public const double DuplicateSalienceCeiling = 0.2;

        /// <summary>Salience ceiling applied to a record read as stale or wrongly typed.</summary>
        public const double StaleSalienceCeiling = 0.3;

        /// <summary>Maximum records reviewed per mission.</summary>
        public const int MaxRecordsPerMission = 10;

        /// <summary>Maximum existing records offered as duplicate candidates per record.</summary>
        public const int MaxDuplicateCandidates = 5;

        #endregion

        #region Private-Members

        private const string _Header = "[RecorderMemoryReviewAdapter] ";
        private const string _TypeOkQuestionId = "type_ok";
        private const string _DuplicateQuestionId = "duplicate_of";
        private const string _StaleQuestionId = "will_go_stale";
        private const string _AiMemoryQuestionId = "belongs_in_ai_memory";
        private const string _NoDuplicate = "none";

        private readonly TypedDecisionSettings _Settings;
        private readonly ITypedDecisionClient _Client;
        private readonly TypedDecisionRecorder _Recorder;
        private readonly IMemoryCandidateProposalWriter _Writer;
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create the Recorder memory review adapter.
        /// </summary>
        /// <param name="settings">Typed-decision settings section.</param>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Recorder for the per-record typed-decision event.</param>
        /// <param name="writer">The D18 proposal writer.</param>
        /// <param name="database">Database driver holding native memory.</param>
        /// <param name="logging">Logging module.</param>
        public RecorderMemoryReviewAdapter(
            TypedDecisionSettings settings,
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            IMemoryCandidateProposalWriter writer,
            DatabaseDriver database,
            LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            _Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _Writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Review the memory records a finished Recorder mission wrote. Never throws.
        /// </summary>
        /// <param name="mission">The finished Recorder mission.</param>
        /// <param name="token">Cancellation token, forwarded to the client so its timeout links to it.</param>
        /// <returns>What the review changed.</returns>
        public async Task<RecorderMemoryReviewResult> ReviewAsync(Mission mission, CancellationToken token)
        {
            RecorderMemoryReviewResult outcome = new RecorderMemoryReviewResult();
            if (mission == null || String.IsNullOrWhiteSpace(mission.Id))
            {
                outcome.Reason = "no_mission";
                return outcome;
            }

            ResolvedTypedDecision cfg = _Settings.For(DecisionPoint);
            if (cfg.Mode == TypedDecisionModeEnum.Off)
            {
                outcome.Reason = "dormant";
                return outcome;
            }

            try
            {
                List<Memory> all = String.IsNullOrWhiteSpace(mission.TenantId)
                    ? await _Database.Memories.EnumerateAsync(token).ConfigureAwait(false)
                    : await _Database.Memories.EnumerateAsync(mission.TenantId!, token).ConfigureAwait(false);

                List<Memory> written = all
                    .Where(memory => String.Equals(memory.SourceMissionId, mission.Id, StringComparison.Ordinal))
                    .OrderBy(memory => memory.CreatedUtc)
                    .ThenBy(memory => memory.Id, StringComparer.Ordinal)
                    .Take(MaxRecordsPerMission)
                    .ToList();
                if (written.Count == 0)
                {
                    outcome.Reason = "no_records";
                    return outcome;
                }

                HashSet<string> writtenIds = new HashSet<string>(written.Select(memory => memory.Id), StringComparer.Ordinal);
                foreach (Memory record in written)
                {
                    List<Memory> candidates = DuplicateCandidates(record, all, writtenIds);
                    bool available = await ReviewRecordAsync(mission, record, candidates, cfg, outcome, token).ConfigureAwait(false);
                    if (!available)
                    {
                        outcome.Reason = "unavailable";
                        return outcome;
                    }
                }

                outcome.Reason = "reviewed";
                return outcome;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                outcome.Reason = "cancelled";
                return outcome;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "Recorder memory review failed for mission " + mission.Id + ": " + ex.Message);
                outcome.Reason = "error";
                return outcome;
            }
        }

        #endregion

        #region Private-Methods

        private async Task<bool> ReviewRecordAsync(
            Mission mission,
            Memory record,
            List<Memory> candidates,
            ResolvedTypedDecision cfg,
            RecorderMemoryReviewResult outcome,
            CancellationToken token)
        {
            object state = DecisionStateRedactor.RedactObject(BuildState(record, candidates), _Settings.MaxStateChars);
            string redacted = state as string ?? String.Empty;

            TypedDecisionResult result;
            try
            {
                result = await _Client.DecideAsync(
                    new TypedDecisionRequest
                    {
                        DecisionPoint = DecisionPoint,
                        State = state,
                        Questions = Questions(candidates.Count)
                    },
                    token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "client threw, records stand: " + ex.Message);
                result = new TypedDecisionResult { Available = false, UnavailableReason = "exception" };
            }

            outcome.Reviewed++;
            if (result == null || !result.Available)
            {
                await _Recorder.RecordUnavailableAsync(Context(mission, "keep", null, null, result ?? new TypedDecisionResult { Available = false, UnavailableReason = "exception" }, redacted), token).ConfigureAwait(false);
                return false;
            }

            double threshold = cfg.GateThreshold;
            double typeOk = NoulOf(result, _TypeOkQuestionId, 1.0);
            double stale = NoulOf(result, _StaleQuestionId, 0.0);
            double aiMemory = NoulOf(result, _AiMemoryQuestionId, 0.0);
            Memory? duplicateOf = DuplicateOf(result, candidates, out double duplicateConfidence);

            bool proposeDuplicate = duplicateOf != null && duplicateConfidence >= threshold;
            bool proposeStale = stale >= threshold;
            bool proposeWrongType = (1.0 - typeOk) >= threshold;
            bool proposeAiMemory = aiMemory >= threshold;
            bool anyProposed = proposeDuplicate || proposeStale || proposeWrongType || proposeAiMemory;
            string modelVerdict = Verdict(proposeDuplicate, proposeStale, proposeWrongType, proposeAiMemory);
            double confidence = new[] { proposeDuplicate ? duplicateConfidence : 0.0, stale, 1.0 - typeOk, aiMemory }.Max();

            if (cfg.Mode != TypedDecisionModeEnum.Gate || !anyProposed)
            {
                string label = cfg.Mode == TypedDecisionModeEnum.Shadow ? "shadow_mode" : "below_threshold";
                await _Recorder.RecordShadowAsync(Context(mission, "keep", modelVerdict, confidence, result, redacted), label, token).ConfigureAwait(false);
                return true;
            }

            bool changed = false;
            if (proposeDuplicate || proposeStale || proposeWrongType)
            {
                double ceiling = proposeDuplicate ? DuplicateSalienceCeiling : StaleSalienceCeiling;
                string? duplicateTag = proposeDuplicate ? DuplicateTagPrefix + duplicateOf!.Id : null;
                changed = await AdjustRecordAsync(record.Id, ceiling, duplicateTag, outcome, token).ConfigureAwait(false);
            }

            if (proposeAiMemory)
            {
                string? proposalId = await _Writer.WriteAsync(BuildProposal(mission, record, aiMemory), token).ConfigureAwait(false);
                if (proposalId != null)
                {
                    outcome.Proposed++;
                    changed = true;
                }
            }

            if (changed)
                await _Recorder.RecordGatedAsync(Context(mission, "keep", modelVerdict, confidence, result, redacted), token).ConfigureAwait(false);
            else
                await _Recorder.RecordShadowAsync(Context(mission, "keep", modelVerdict, confidence, result, redacted), "no_change", token).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> AdjustRecordAsync(string recordId, double ceiling, string? duplicateTag, RecorderMemoryReviewResult outcome, CancellationToken token)
        {
            // Read the stored row again so the conditional write carries the current version. Only
            // salience and tags change; content, summary, type, topic, and key are written back as read.
            Memory? current = await _Database.Memories.ReadAsync(recordId, token).ConfigureAwait(false);
            if (current == null) return false;

            bool lowerSalience = current.Salience > ceiling;
            bool addTag = duplicateTag != null && !current.Tags.Contains(duplicateTag, StringComparer.Ordinal);
            if (!lowerSalience && !addTag) return false;

            int readVersion = current.Version;
            if (lowerSalience) current.Salience = ceiling;
            if (addTag)
            {
                List<string> tags = new List<string>(current.Tags) { duplicateTag! };
                current.Tags = tags;
            }
            current.Version = readVersion + 1;
            current.LastUpdateUtc = DateTime.UtcNow;

            bool applied = await _Database.Memories.UpdateAsync(current, readVersion, token).ConfigureAwait(false);
            if (!applied)
            {
                _Logging.Info(_Header + "memory " + recordId + " changed during review; left as it is");
                return false;
            }

            if (lowerSalience) outcome.SalienceLowered++;
            if (addTag) outcome.DuplicatesLinked++;
            return true;
        }

        private static List<Memory> DuplicateCandidates(Memory record, List<Memory> all, HashSet<string> writtenIds)
        {
            return all
                .Where(other => !writtenIds.Contains(other.Id))
                .Where(other => String.Equals(other.VesselId, record.VesselId, StringComparison.Ordinal))
                .Where(other => other.Type == record.Type
                    || (!String.IsNullOrWhiteSpace(record.Topic) && String.Equals(other.Topic, record.Topic, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(other => other.Salience)
                .ThenByDescending(other => other.LastUpdateUtc)
                .ThenBy(other => other.Id, StringComparer.Ordinal)
                .Take(MaxDuplicateCandidates)
                .ToList();
        }

        private static object BuildState(Memory record, List<Memory> candidates)
        {
            List<object> existing = new List<object>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                existing.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["option"] = OptionName(i),
                    ["type"] = candidates[i].Type.ToString(),
                    ["topic"] = candidates[i].Topic,
                    ["summary"] = candidates[i].Summary,
                    ["content"] = candidates[i].Content
                });
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = record.Type.ToString(),
                ["topic"] = record.Topic,
                ["summary"] = record.Summary,
                ["content"] = record.Content,
                ["existing_records"] = existing
            };
        }

        private static IReadOnlyDictionary<string, TypedQuestion> Questions(int candidateCount)
        {
            Dictionary<string, string> duplicateOptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [_NoDuplicate] = "the record does not repeat any existing record"
            };
            for (int i = 0; i < candidateCount; i++)
                duplicateOptions[OptionName(i)] = "the record repeats the existing record labelled " + OptionName(i);

            return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                [_TypeOkQuestionId] = new NoulQuestion(
                    "The stated memory type (working, episodic, semantic, or procedural) fits this record, and it is not working memory that should not be stored.",
                    "the type fits", "the type is wrong"),
                [_DuplicateQuestionId] = new ChoiceQuestion(
                    "Which existing record, if any, does this record duplicate?",
                    duplicateOptions),
                [_StaleQuestionId] = new NoulQuestion(
                    "The record contains an id, path, count, or date that stops being true, so it will go stale.",
                    "will go stale", "states a durable rule"),
                [_AiMemoryQuestionId] = new NoulQuestion(
                    "The record is a fleet rule that belongs in shared external memory, not a vessel fact for native memory.",
                    "belongs in shared memory", "is a native vessel fact")
            };
        }

        private static string OptionName(int index)
        {
            return "existing_" + (index + 1).ToString(CultureInfo.InvariantCulture);
        }

        private static double NoulOf(TypedDecisionResult result, string questionId, double fallback)
        {
            if (result.Answers == null) return fallback;
            if (!result.Answers.TryGetValue(questionId, out TypedAnswer? answer) || answer == null) return fallback;
            if (answer.Noul.HasValue) return answer.Noul.Value;
            return fallback;
        }

        private static Memory? DuplicateOf(TypedDecisionResult result, List<Memory> candidates, out double confidence)
        {
            confidence = 0.0;
            if (result.Answers == null) return null;
            if (!result.Answers.TryGetValue(_DuplicateQuestionId, out TypedAnswer? answer) || answer == null) return null;
            if (String.IsNullOrWhiteSpace(answer.Choice)) return null;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (String.Equals(answer.Choice!.Trim(), OptionName(i), StringComparison.Ordinal))
                {
                    confidence = answer.Confidence ?? 0.0;
                    return candidates[i];
                }
            }
            return null;
        }

        private static string Verdict(bool duplicate, bool stale, bool wrongType, bool aiMemory)
        {
            List<string> parts = new List<string>();
            if (duplicate) parts.Add("duplicate");
            if (stale) parts.Add("stale");
            if (wrongType) parts.Add("wrong_type");
            if (aiMemory) parts.Add("belongs_in_ai_memory");
            return parts.Count == 0 ? "keep" : String.Join("+", parts);
        }

        private MemoryCandidateProposal BuildProposal(Mission mission, Memory record, double confidence)
        {
            // The proposal text may be copied into the AI-Memory repository, so it is redacted like the
            // transmitted state. The record and mission ids stay in the related-record column.
            int cap = _Settings.MaxStateChars;
            string title = !String.IsNullOrWhiteSpace(record.Summary) ? record.Summary! : FirstLine(record.Content);
            return new MemoryCandidateProposal
            {
                Source = MemoryProposal.SourceRecorderSeam,
                GroupKey = "memory|" + record.Id,
                Title = DecisionStateRedactor.Redact(title, cap),
                Detail = DecisionStateRedactor.Redact(record.Content, cap),
                Category = record.Type.ToString(),
                DurableLesson = confidence,
                Scope = "shared",
                RelatedRecordIds = new List<string> { record.Id, mission.Id },
                CreatedUtc = DateTime.UtcNow
            };
        }

        private static string FirstLine(string? text)
        {
            if (String.IsNullOrWhiteSpace(text)) return String.Empty;
            string trimmed = text.Trim();
            int newline = trimmed.IndexOf('\n');
            return newline < 0 ? trimmed : trimmed.Substring(0, newline).Trim();
        }

        private static TypedDecisionEventContext Context(
            Mission mission,
            string ruleVerdict,
            string? modelVerdict,
            double? confidence,
            TypedDecisionResult result,
            string redactedState)
        {
            return new TypedDecisionEventContext
            {
                DecisionPoint = DecisionPoint,
                RuleVerdict = ruleVerdict,
                ModelVerdict = modelVerdict,
                Confidence = confidence,
                Result = result,
                RedactedState = redactedState,
                Mission = mission
            };
        }

        #endregion
    }
}
