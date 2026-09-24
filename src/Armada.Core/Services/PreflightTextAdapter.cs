namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The D5 <c>preflight</c> (text half) decision adapter. It runs AFTER the deterministic dispatch
    /// preflight block in <see cref="ObjectiveDispatchPreviewService"/>: the deterministic facts
    /// (Q1/Q2/Q3/Q10/Q11) are already computed and its <c>objective_preflight_incomplete</c> Error
    /// issue, when present, already stands. This adapter asks the typed-decision model the text-half
    /// battery questions the code cannot settle deterministically (Q1 premise-versus-facts, Q4-Q9,
    /// Q12, and the Q13 owner-question choice), each phrased as the DEFECT so a high answer means a
    /// brief problem.
    ///
    /// The adapter only ever ADDS preview issues or posts an owner-addressed note; it never removes a
    /// deterministic issue, never dispatches, and never lands. The deterministic preview is the
    /// fallback in every non-gate case: Off adds nothing and makes no call; an unavailable model adds
    /// nothing and records one unavailable event; a below-threshold answer adds nothing and records one
    /// shadow event. In Gate mode a question answered at or above the decision threshold becomes an
    /// Error issue <see cref="ModelFlagIssueCode"/> that the autonomous scheduler already skips on, and
    /// a Q13 <c>needs_owner_ruling</c> also posts an owner-addressed board note. The adapter never
    /// throws into the caller.
    /// </summary>
    public sealed class PreflightTextAdapter
    {
        #region Public-Members

        /// <summary>
        /// The decision-point name in the <c>typedDecisions.decisions</c> settings map.
        /// </summary>
        public const string DecisionPoint = "preflight";

        /// <summary>
        /// Stable code of the blocking issue a model flag adds to the dispatch preview in Gate mode.
        /// One is added per flagged question; the scheduler skips dispatch on any Error issue and its
        /// skip event lists this code.
        /// </summary>
        public const string ModelFlagIssueCode = "objective_preflight_model_flag";

        #endregion

        #region Private-Members

        private const string _Header = "[PreflightTextAdapter] ";
        private const string _Area = "preflight";
        private const string _Q13Id = "q13";
        private const string _Q13NeedsOwnerRuling = "needs_owner_ruling";
        private const string _Q13NeedsRepoFact = "needs_repo_fact";

        private readonly TypedDecisionSettings _Settings;
        private readonly ITypedDecisionClient _Client;
        private readonly TypedDecisionRecorder _Recorder;
        private readonly IOwnerDecisionNotePoster? _OwnerNotePoster;
        private readonly LoggingModule _Logging;

        // The text-half battery. Each entry is a noul phrased as the defect: a high answer names a
        // brief problem the deterministic block cannot settle on its own. The numbers match the
        // fourteen-question dispatch preflight; Q2/Q3/Q10/Q11 are deterministic facts and Q1 pairs the
        // premise against those facts, so Q1 is a noul here too.
        private static readonly IReadOnlyList<PreflightNoul> _Nouls = new List<PreflightNoul>
        {
            new PreflightNoul("q1", 1,
                "A concrete claim in the objective's premise does not hold at the target tip; the deterministic preflight facts contradict it.",
                "the premise contradicts the facts", "the premise matches the facts"),
            new PreflightNoul("q4", 4,
                "A premise or a deliverable is not observable from a dock: it is gitignored, in an unprovisioned sibling, or behind a tool the container lacks.",
                "a premise or deliverable is unobservable from a dock", "everything is observable from a dock"),
            new PreflightNoul("q5", 5,
                "A stage section permits for one artifact class what another stage section forbids for the same class.",
                "the stages disagree on an artifact class", "the stages agree"),
            new PreflightNoul("q6", 6,
                "An imperative in the description is false at the reviewer, TestEngineer, or Judge stage because the checkout state has changed by then.",
                "a description imperative is false at a later stage", "every imperative holds at its stage"),
            new PreflightNoul("q7", 7,
                "The brief offers 'owner-approved' or other approval wording as a choice a captain may make in source, when only the owner approves and only on the objective.",
                "the brief offers approval as a captain choice", "the brief does not offer approval as a choice"),
            new PreflightNoul("q8", 8,
                "The brief makes success depend on a count that will not hold: a pass or skip total, or a coverage figure, that legitimately varies between host and dock, so a healthy run could read as failing it. Only a failure count measured at a named commit is a stable contract.",
                "success depends on a legitimately varying count, so a healthy run could fail it", "every count the brief depends on is a stable failure count"),
            new PreflightNoul("q9", 9,
                "An example expected value does not change between the defective and the corrected code, so a green test that copies it would prove nothing.",
                "an example value does not distinguish the fix", "every example value distinguishes the fix"),
            new PreflightNoul("q12", 12,
                "A public API reshape does not name every consumer test class it breaks, so their update is not in the same wave.",
                "a reshape omits a broken consumer test class", "every broken consumer test class is named")
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a D5 preflight text-half adapter.
        /// </summary>
        /// <param name="settings">Typed-decision settings section.</param>
        /// <param name="client">Typed-decision client (the null client when the system is off).</param>
        /// <param name="recorder">Recorder for the per-call typed-decision event.</param>
        /// <param name="ownerNotePoster">Owner-addressed board-note poster for a Q13 owner ruling; null skips the note.</param>
        /// <param name="logging">Logging module.</param>
        public PreflightTextAdapter(
            TypedDecisionSettings settings,
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            IOwnerDecisionNotePoster? ownerNotePoster,
            LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            _Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _OwnerNotePoster = ownerNotePoster;
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Consult the model for the text-half preflight battery and, in Gate mode at or above the
        /// decision threshold, add one Error issue per flagged question to the preview and post an
        /// owner-addressed board note for a Q13 owner ruling. The deterministic preview passed in is
        /// returned unchanged in every other case. Never throws into the caller.
        /// </summary>
        /// <param name="objective">The objective being previewed.</param>
        /// <param name="vessel">The resolved target vessel.</param>
        /// <param name="pipeline">The resolved pipeline, or null for the Worker-only path.</param>
        /// <param name="result">The dispatch preview built by the deterministic block; mutated in place.</param>
        /// <param name="token">Cancellation token, forwarded to the client so its timeout links to the caller.</param>
        /// <returns>A task that completes when the decision has been consulted and applied.</returns>
        public async Task EvaluateAsync(
            Objective objective,
            Vessel vessel,
            Pipeline? pipeline,
            ObjectiveDispatchPreview result,
            CancellationToken token)
        {
            if (objective == null || vessel == null || result == null) return;

            ResolvedTypedDecision cfg = _Settings.For(DecisionPoint);
            if (cfg.Mode == TypedDecisionModeEnum.Off) return;

            // The shared egress guard: an objective naming an excluded vessel, a target vessel on the list, or a
            // state naming an excluded marker sends nothing. The preview stays deterministic and records why.
            string? refusal = TypedDecisionEgress.Refusal(_Settings, DecisionPoint, TypedDecisionEgress.VesselsOf(objective.VesselIds, vessel.Id),
                () => BuildState(objective, vessel, pipeline, result));
            if (refusal != null)
            {
                await SafeRecordAsync(() => _Recorder.RecordUnavailableAsync(
                    BuildContext(objective, vessel, TypedDecisionEgress.Refused(refusal), null, null, String.Empty), token)).ConfigureAwait(false);
                return;
            }

            string redacted;
            TypedDecisionRequest request;
            try
            {
                RedactedDecisionState redactedState = DecisionStateRedactor.RedactState(BuildState(objective, vessel, pipeline, result), _Settings.MaxStateChars);
                object state = redactedState.State;
                redacted = redactedState.Text;
                request = new TypedDecisionRequest
                {
                    DecisionPoint = DecisionPoint,
                    State = state,
                    Questions = BuildQuestions()
                };
            }
            catch (Exception ex)
            {
                // Building state or questions must never break the preview; leave it deterministic.
                _Logging.Warn(_Header + "state build failed, deterministic preview stands: " + ex.Message);
                return;
            }

            TypedDecisionResult decision;
            try
            {
                // Forward the caller's token unchanged so the client links its settings timeout to it:
                // a slow decision cancels through this token and is unavailable, never late.
                decision = await _Client.DecideAsync(request, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "client threw, deterministic preview stands: " + ex.Message);
                await SafeRecordAsync(() => _Recorder.RecordUnavailableAsync(
                    BuildContext(objective, vessel, TypedDecisionResult.Exception(), null, null, redacted), token)).ConfigureAwait(false);
                return;
            }

            if (decision == null || !decision.Available)
            {
                await SafeRecordAsync(() => _Recorder.RecordUnavailableAsync(
                    BuildContext(objective, vessel, decision ?? TypedDecisionResult.Exception(), null, null, redacted), token)).ConfigureAwait(false);
                return;
            }

            PreflightModelReading reading = Interpret(decision, cfg.GateThreshold);
            bool gate = cfg.Mode == TypedDecisionModeEnum.Gate;

            // Apply the reading to the preview. In Gate a flagged question is a blocking Error issue
            // the scheduler skips on; in Shadow the same flag is an advisory Warning the operator sees.
            foreach (PreflightFlag flag in reading.Flags)
            {
                if (gate)
                {
                    AddIssue(result, ModelFlagIssueCode, ReadinessSeverityEnum.Error,
                        "Dispatch preflight model flag on question " + flag.QuestionNumber + ": " + flag.Defect,
                        "q" + flag.QuestionNumber.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    AddIssue(result, "preflight_q" + flag.QuestionNumber.ToString(CultureInfo.InvariantCulture) + "_model",
                        ReadinessSeverityEnum.Warning,
                        "Dispatch preflight model note on question " + flag.QuestionNumber + ": " + flag.Defect,
                        "q" + flag.QuestionNumber.ToString(CultureInfo.InvariantCulture));
                }
            }

            // The Q13 owner-question choice is separate from the nouls: needs_owner_ruling and
            // needs_repo_fact are each a blocking flag in Gate, and needs_owner_ruling also posts an
            // owner-addressed board note so the question reaches the owner.
            bool ownerRuling = false;
            if (reading.Q13Flagged)
            {
                if (gate)
                {
                    AddIssue(result, ModelFlagIssueCode, ReadinessSeverityEnum.Error,
                        "Dispatch preflight model flag on question 13: the objective has an open " + reading.Q13Choice
                            + " that must be settled before dispatch.",
                        "q13");
                    ownerRuling = String.Equals(reading.Q13Choice, _Q13NeedsOwnerRuling, StringComparison.Ordinal);
                }
                else
                {
                    AddIssue(result, "preflight_q13_model", ReadinessSeverityEnum.Warning,
                        "Dispatch preflight model note on question 13: an open " + reading.Q13Choice + " may exist.",
                        "q13");
                }
            }

            bool applied = gate && (reading.Flags.Count > 0 || reading.Q13Flagged);
            if (applied)
            {
                await SafeRecordAsync(() => _Recorder.RecordGatedAsync(
                    BuildContext(objective, vessel, decision, reading.VerdictLabel, reading.MaxConfidence, redacted), token)).ConfigureAwait(false);
            }
            else
            {
                string outcome = cfg.Mode == TypedDecisionModeEnum.Shadow ? "shadow_mode" : "below_threshold";
                await SafeRecordAsync(() => _Recorder.RecordShadowAsync(
                    BuildContext(objective, vessel, decision, reading.VerdictLabel, reading.MaxConfidence, redacted), outcome, token)).ConfigureAwait(false);
            }

            if (ownerRuling)
            {
                await PostOwnerRulingNoteAsync(objective, vessel, result, token).ConfigureAwait(false);
            }
        }

        #endregion

        #region Private-Methods

        private static object BuildState(Objective objective, Vessel vessel, Pipeline? pipeline, ObjectiveDispatchPreview result)
        {
            List<object> facts = new List<object>();
            foreach (ObjectiveDispatchPreflightFact fact in result.Preflight?.Facts ?? new List<ObjectiveDispatchPreflightFact>())
            {
                facts.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["question"] = fact.QuestionNumber,
                    ["status"] = fact.Status.ToString(),
                    ["detail"] = fact.Detail,
                    ["recorded_answer"] = fact.RecordedAnswer.ToString()
                });
            }

            List<string> stages = (pipeline?.Stages ?? new List<PipelineStage>())
                .Where(stage => stage != null)
                .OrderBy(stage => stage.Order)
                .Select(stage => stage.PersonaName)
                .ToList();

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = objective.Title,
                ["description"] = objective.Description,
                ["acceptance_criteria"] = (objective.AcceptanceCriteria ?? new List<string>())
                    .Where(item => !String.IsNullOrWhiteSpace(item)).ToList(),
                ["non_goals"] = (objective.NonGoals ?? new List<string>())
                    .Where(item => !String.IsNullOrWhiteSpace(item)).ToList(),
                ["refinement_summary"] = objective.RefinementSummary,
                ["kind"] = objective.Kind.ToString(),
                ["vessel"] = vessel.Name,
                ["pipeline_stages"] = stages,
                ["facts"] = facts
            };
        }

        private static IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            foreach (PreflightNoul noul in _Nouls)
            {
                questions[noul.Id] = new NoulQuestion(noul.Instructions, noul.TrueMeaning, noul.FalseMeaning);
            }
            questions[_Q13Id] = new ChoiceQuestion(
                "Is there an open question that only the owner can answer, or a repository fact the brief still needs, before this objective is dispatched?",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["none"] = "No open owner question and no missing repository fact; the objective is ready to dispatch.",
                    [_Q13NeedsOwnerRuling] = "An open question only the owner can answer blocks dispatch.",
                    [_Q13NeedsRepoFact] = "A repository fact the brief still needs is not yet established."
                });
            return questions;
        }

        private static PreflightModelReading Interpret(TypedDecisionResult result, double threshold)
        {
            PreflightModelReading reading = new PreflightModelReading();
            IReadOnlyDictionary<string, TypedAnswer> answers = result.Answers ?? new Dictionary<string, TypedAnswer>();

            foreach (PreflightNoul noul in _Nouls)
            {
                if (!answers.TryGetValue(noul.Id, out TypedAnswer? answer) || answer == null) continue;
                double value = NoulValue(answer);
                if (value > reading.MaxConfidence) reading.MaxConfidence = value;
                if (value >= threshold)
                    reading.Flags.Add(new PreflightFlag(noul.Number, noul.Defect, value));
            }

            if (answers.TryGetValue(_Q13Id, out TypedAnswer? q13) && q13 != null)
            {
                string? choice = String.IsNullOrWhiteSpace(q13.Choice) ? null : q13.Choice!.Trim();
                double confidence = q13.Confidence ?? 0.0;
                if (confidence > reading.MaxConfidence) reading.MaxConfidence = confidence;
                bool isOpen = String.Equals(choice, _Q13NeedsOwnerRuling, StringComparison.Ordinal)
                    || String.Equals(choice, _Q13NeedsRepoFact, StringComparison.Ordinal);
                if (isOpen && confidence >= threshold)
                {
                    reading.Q13Flagged = true;
                    reading.Q13Choice = choice;
                }
            }

            return reading;
        }

        private static double NoulValue(TypedAnswer answer)
        {
            if (answer.Noul.HasValue) return answer.Noul.Value;
            if (answer.Confidence.HasValue) return answer.Confidence.Value;
            return 0.0;
        }

        private async Task PostOwnerRulingNoteAsync(Objective objective, Vessel vessel, ObjectiveDispatchPreview result, CancellationToken token)
        {
            if (_OwnerNotePoster == null) return;
            try
            {
                string content = "Owner decision needed before dispatching objective " + objective.Id
                    + " (" + (objective.Title ?? "untitled") + ") on vessel " + vessel.Name
                    + ": the dispatch preflight model reports an open question only the owner can answer."
                    + " The objective is held; record the ruling on the row, then re-preview.";
                await _OwnerNotePoster.PostOwnerDecisionAsync(content, result.VesselId, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "owner-ruling board note failed: " + ex.Message);
            }
        }

        private Task SafeRecordAsync(Func<Task> record)
        {
            return TypedDecisionRecording.SafeRecordAsync(record, _Logging, _Header);
        }

        private static TypedDecisionEventContext BuildContext(
            Objective objective,
            Vessel vessel,
            TypedDecisionResult result,
            string? modelVerdict,
            double? confidence,
            string redactedState)
        {
            return new TypedDecisionEventContext
            {
                DecisionPoint = DecisionPoint,
                RuleVerdict = "deterministic_preflight",
                ModelVerdict = modelVerdict,
                Confidence = confidence,
                Result = result,
                RedactedState = redactedState
            };
        }

        private static void AddIssue(
            ObjectiveDispatchPreview result,
            string code,
            ReadinessSeverityEnum severity,
            string message,
            string relatedValue)
        {
            result.Issues.Add(new ObjectiveDispatchPreviewIssue
            {
                Code = code,
                Area = _Area,
                Severity = severity,
                Message = message,
                RelatedValue = relatedValue
            });
        }

        #endregion

        #region Private-Types

        private sealed class PreflightNoul
        {
            public PreflightNoul(string id, int number, string instructions, string trueMeaning, string falseMeaning)
            {
                Id = id;
                Number = number;
                Instructions = instructions;
                Defect = instructions;
                TrueMeaning = trueMeaning;
                FalseMeaning = falseMeaning;
            }

            public string Id { get; }
            public int Number { get; }
            public string Instructions { get; }
            public string Defect { get; }
            public string TrueMeaning { get; }
            public string FalseMeaning { get; }
        }

        private sealed class PreflightFlag
        {
            public PreflightFlag(int questionNumber, string defect, double value)
            {
                QuestionNumber = questionNumber;
                Defect = defect;
                Value = value;
            }

            public int QuestionNumber { get; }
            public string Defect { get; }
            public double Value { get; }
        }

        private sealed class PreflightModelReading
        {
            public List<PreflightFlag> Flags { get; } = new List<PreflightFlag>();
            public bool Q13Flagged { get; set; }
            public string? Q13Choice { get; set; }
            public double MaxConfidence { get; set; }

            public string VerdictLabel
            {
                get
                {
                    List<string> parts = Flags
                        .Select(flag => "q" + flag.QuestionNumber.ToString(CultureInfo.InvariantCulture))
                        .ToList();
                    if (Q13Flagged) parts.Add("q13:" + (Q13Choice ?? "open"));
                    return parts.Count == 0 ? "clear" : String.Join(",", parts);
                }
            }
        }

        #endregion
    }
}
