namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Runs a user-defined custom typed decision. Unlike the built-in adapters, a custom decision has
    /// no deterministic rule and no orchestration seam of its own: it assembles its state from a
    /// supplied context, asks the operator's questions, records one event, and — only when bound and
    /// gated at or above threshold — reports an advisory flag to its caller. It never lands,
    /// dispatches, approves a PASS, holds a rescue, or writes memory. The binding action list is fixed
    /// in <see cref="CustomDecisionSeamEnum"/> and every member is non-approving by construction.
    ///
    /// Two callers run it: the <c>armada_run_custom_decision</c> tool runs one decision by name, and
    /// the mission service runs every MissionDiff decision through <see cref="RunMissionDiffAsync"/>
    /// when a Worker stage hands off its diff. Events use the shared recorder types under the decision
    /// point <c>custom:&lt;name&gt;</c>, so retention, reversal and every event query treat a custom
    /// decision like a built-in one.
    ///
    /// The skeleton mirrors <see cref="TypedDecisionAdapterBase{TInput,TVerdict,TModel}"/>: Off makes
    /// no call, an unavailable provider records nothing acted, Shadow or below-threshold records the
    /// answer without acting, and Gate at or above threshold records and may raise the advisory flag.
    /// The gate reads Noul answers the way the built-in decisions do: the raw probability that the
    /// statement is true, so a custom Noul must be phrased with the finding as its true pole. A Choice
    /// gates only when the model picks one of the question's <c>flagOptions</c>, at that option's
    /// confidence; a Choice with no finding options, and every Score, is recorded and returned but
    /// never gates. It never throws into a caller.
    /// </summary>
    public sealed class CustomTypedDecisionAdapter
    {
        #region Public-Members

        /// <summary>Prefix of every custom decision's decision point, ahead of its name.</summary>
        public const string DecisionPointPrefix = "custom:";

        /// <summary>Characters of a mission's agent output sent as <c>output_tail</c> on the MissionDiff surface.</summary>
        public const int OutputTailChars = 4096;

        #endregion

        #region Private-Members

        private const string _Header = "[CustomTypedDecisionAdapter] ";

        private readonly ITypedDecisionClient _Client;
        private readonly TypedDecisionRecorder _Recorder;
        private readonly TypedDecisionSettings _Settings;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the adapter.</summary>
        /// <param name="client">The typed-decision client; never called when the decision is Off.</param>
        /// <param name="recorder">The recorder that writes one event per consulted call.</param>
        /// <param name="settings">Typed-decision settings; the effective mode and threshold come from here.</param>
        /// <param name="logging">Logging module.</param>
        public CustomTypedDecisionAdapter(ITypedDecisionClient client, TypedDecisionRecorder recorder, TypedDecisionSettings settings, LoggingModule logging)
        {
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            _Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>The decision point a custom decision records under: <c>custom:&lt;name&gt;</c>.</summary>
        /// <param name="name">Custom decision name.</param>
        /// <returns>The decision point key.</returns>
        public static string DecisionPointFor(string name)
        {
            return DecisionPointPrefix + name;
        }

        /// <summary>
        /// The fields a MissionDiff decision can select from a finished mission: <c>title</c>,
        /// <c>persona</c>, <c>diff</c>, <c>output_tail</c> (the last <see cref="OutputTailChars"/>
        /// characters of the agent output), <c>changed_paths</c> (one per line), and
        /// <c>failure_reason</c>. Absent values are left out. The eval store builds its cases from the
        /// same method, so a replay sends what production sends.
        /// </summary>
        /// <param name="mission">The finished mission.</param>
        /// <returns>The context keyed by field name.</returns>
        public static Dictionary<string, object?> MissionContext(Mission mission)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            Dictionary<string, object?> context = new Dictionary<string, object?>(StringComparer.Ordinal);
            AddIfPresent(context, "title", mission.Title);
            AddIfPresent(context, "persona", mission.Persona);
            AddIfPresent(context, "diff", mission.DiffSnapshot);
            if (!String.IsNullOrEmpty(mission.AgentOutput))
            {
                string output = mission.AgentOutput!;
                AddIfPresent(context, "output_tail", output.Length > OutputTailChars ? output.Substring(output.Length - OutputTailChars) : output);
            }
            IReadOnlyList<string> paths = DiffPathExtractor.ExtractChangedPaths(mission.DiffSnapshot);
            if (paths.Count > 0) context["changed_paths"] = String.Join("\n", paths);
            AddIfPresent(context, "failure_reason", mission.FailureReason);
            return context;
        }

        /// <summary>
        /// Run every custom decision on the MissionDiff surface over a finished mission, in name order.
        /// A decision that is Off, or scoped to other vessels, is skipped without a call, and a mission
        /// with no diff runs nothing. Returns one outcome per decision that consulted the provider.
        /// Never throws.
        /// </summary>
        /// <param name="mission">The finished mission whose diff is read.</param>
        /// <param name="vesselName">The mission's vessel name, matched against a decision's vessel scope with its id; may be null.</param>
        /// <param name="token">Cancellation token, forwarded to each call.</param>
        /// <returns>The outcomes; empty when nothing ran.</returns>
        public async Task<List<CustomDecisionOutcome>> RunMissionDiffAsync(Mission mission, string? vesselName, CancellationToken token)
        {
            List<CustomDecisionOutcome> outcomes = new List<CustomDecisionOutcome>();
            if (mission == null || String.IsNullOrWhiteSpace(mission.DiffSnapshot)) return outcomes;

            List<string> names = new List<string>();
            foreach (KeyValuePair<string, CustomTypedDecisionSettings> entry in _Settings.Custom)
            {
                if (entry.Value == null || entry.Value.Surface != CustomDecisionSurfaceEnum.MissionDiff) continue;
                if (_Settings.ForCustom(entry.Key).Mode == TypedDecisionModeEnum.Off) continue;
                if (!AppliesToVessel(entry.Value, mission.VesselId, vesselName)) continue;
                names.Add(entry.Key);
            }
            if (names.Count == 0) return outcomes;
            names.Sort(StringComparer.Ordinal);

            Dictionary<string, object?> context = MissionContext(mission);
            foreach (string name in names)
            {
                try
                {
                    CustomDecisionOutcome outcome = await RunAsync(name, context, mission, mission.CaptainId, token).ConfigureAwait(false);
                    if (outcome.Status != "inactive" && outcome.Status != "not_found") outcomes.Add(outcome);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "MissionDiff decision '" + name + "' failed for mission " + mission.Id + ", no action: " + ex.Message);
                }
            }
            return outcomes;
        }

        /// <summary>
        /// Build the request a custom decision would send for a context, without calling the provider.
        /// Returns null when the decision does not exist or has no questions. The eval store uses this
        /// to replay a custom decision exactly as production would.
        /// </summary>
        /// <param name="name">Custom decision name.</param>
        /// <param name="context">The field values to assemble the state from, keyed by field name.</param>
        /// <returns>The redacted state and questions, or null.</returns>
        public TypedDecisionBatchItem? DescribeRequest(string name, IReadOnlyDictionary<string, object?> context)
        {
            if (!_Settings.Custom.TryGetValue(name, out CustomTypedDecisionSettings? definition) || definition == null) return null;
            return Prepare(definition, context);
        }

        /// <summary>
        /// Run a custom decision over a context. Returns the model answers and whether the decision
        /// flagged. Never throws; an Off decision, a missing decision, or an unavailable provider
        /// returns a result that flagged nothing.
        /// </summary>
        /// <param name="name">Custom decision name.</param>
        /// <param name="context">The field values to assemble the state from.</param>
        /// <param name="mission">The mission the call is about, for event attribution; may be null.</param>
        /// <param name="captainId">The captain that invoked it, for attribution; may be null.</param>
        /// <param name="token">Cancellation token, forwarded to the client.</param>
        /// <returns>The outcome; never null.</returns>
        public async Task<CustomDecisionOutcome> RunAsync(
            string name,
            IReadOnlyDictionary<string, object?> context,
            Mission? mission,
            string? captainId,
            CancellationToken token)
        {
            if (!_Settings.Custom.TryGetValue(name, out CustomTypedDecisionSettings? definition) || definition == null)
                return CustomDecisionOutcome.NotFound(name);

            ResolvedTypedDecision cfg = _Settings.ForCustom(name);
            if (cfg.Mode == TypedDecisionModeEnum.Off)
                return CustomDecisionOutcome.Inactive(name);

            // The same two egress rules the adapter skeleton applies, asked of the same settings: a mission on
            // an excluded vessel, or a state whose UNREDACTED text names an excluded marker, sends nothing.
            string? refusal = null;
            if (!_Settings.AllowsEgress(mission?.VesselId)) refusal = TypedDecisionEgress.ExcludedVesselReason;
            else
            {
                IReadOnlyList<string> markers = _Settings.MarkersForCustom(name);
                if (markers.Count > 0)
                {
                    string raw;
                    try { raw = TypedDecisionEgress.RawText(BuildState(definition, context)); }
                    catch (Exception) { raw = ((char)0).ToString(); }
                    if (TypedDecisionSettings.FirstMarkerIn(markers, raw) != null) refusal = TypedDecisionEgress.ExcludedContentReason;
                }
            }
            if (refusal != null)
            {
                TypedDecisionResult refused = new TypedDecisionResult { Available = false, UnavailableReason = refusal };
                await _Recorder.RecordUnavailableAsync(new TypedDecisionEventContext
                {
                    DecisionPoint = DecisionPointFor(name),
                    RuleVerdict = "none",
                    Result = refused,
                    RedactedState = String.Empty,
                    Mission = mission,
                    CaptainId = captainId
                }, token).ConfigureAwait(false);
                return CustomDecisionOutcome.Unavailable(name, refused);
            }

            TypedDecisionBatchItem? item = Prepare(definition, context);
            if (item == null) return CustomDecisionOutcome.Inactive(name);

            TypedDecisionResult result;
            try
            {
                result = await _Client.DecideAsync(new TypedDecisionRequest
                {
                    DecisionPoint = DecisionPointFor(name),
                    State = item.State.State,
                    Questions = item.Questions
                }, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "custom decision '" + name + "' client threw, no action: " + ex.Message);
                result = TypedDecisionResult.Exception();
            }

            TypedDecisionEventContext eventContext = new TypedDecisionEventContext
            {
                DecisionPoint = DecisionPointFor(name),
                RuleVerdict = "none",
                ModelVerdict = Summarize(result),
                Confidence = Max(GateValues(definition, result)),
                Result = result,
                RedactedState = item.State.Text,
                Mission = mission,
                CaptainId = captainId
            };

            if (!result.Available)
            {
                await _Recorder.RecordUnavailableAsync(eventContext, token).ConfigureAwait(false);
                return CustomDecisionOutcome.Unavailable(name, result);
            }

            Dictionary<string, double> values = GateValues(definition, result);
            double? gateValue = Max(values);
            double confidence = gateValue ?? 0.0;
            bool gated = cfg.Mode == TypedDecisionModeEnum.Gate
                && definition.Binding != CustomDecisionSeamEnum.None
                && gateValue.HasValue
                && confidence >= cfg.GateThreshold
                && cfg.GateThreshold > 0.0;

            if (gated)
            {
                List<string> flaggedQuestions = new List<string>();
                foreach (KeyValuePair<string, double> entry in values)
                    if (entry.Value >= cfg.GateThreshold) flaggedQuestions.Add(entry.Key);
                flaggedQuestions.Sort(StringComparer.Ordinal);
                await _Recorder.RecordGatedAsync(eventContext, token).ConfigureAwait(false);
                _Logging.Info(_Header + "custom decision '" + name + "' flagged at " + confidence.ToString("0.00"));
                return CustomDecisionOutcome.Flagged(name, definition.Description, result, confidence, flaggedQuestions);
            }

            bool shadow = cfg.Mode == TypedDecisionModeEnum.Shadow;
            await _Recorder.RecordShadowAsync(eventContext, shadow ? "shadow_mode" : "below_threshold", token).ConfigureAwait(false);
            return shadow
                ? CustomDecisionOutcome.Shadow(name, result, gateValue)
                : CustomDecisionOutcome.Recorded(name, result, gateValue);
        }

        #endregion

        #region Private-Methods

        private TypedDecisionBatchItem? Prepare(CustomTypedDecisionSettings definition, IReadOnlyDictionary<string, object?> context)
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            foreach (CustomTypedQuestionSettings question in definition.Questions)
            {
                if (String.IsNullOrWhiteSpace(question.Id) || String.IsNullOrWhiteSpace(question.Instructions)) continue;
                questions[question.Id] = ToQuestion(question);
            }
            if (questions.Count == 0) return null;

            object state = BuildState(definition, context);
            RedactedDecisionState redacted = DecisionStateRedactor.RedactState(state, _Settings.MaxStateChars);
            return new TypedDecisionBatchItem(redacted, questions);
        }

        private static object BuildState(CustomTypedDecisionSettings definition, IReadOnlyDictionary<string, object?> context)
        {
            // Assemble only the fields the definition names, in order, from the supplied context. On
            // the MissionDiff surface StateFields selects mission fields; on the captain tool the
            // caller supplies whatever the questions need. Unknown or absent fields are skipped.
            Dictionary<string, object?> state = new Dictionary<string, object?>(StringComparer.Ordinal);
            IReadOnlyList<string> fields = definition.StateFields.Count > 0
                ? definition.StateFields
                : new List<string> { "diff", "output_tail" };
            foreach (string field in fields)
            {
                if (context.TryGetValue(field, out object? value) && value != null) state[field] = value;
            }
            // A captain-tool decision may pass the whole context through when it names no fields it
            // recognises, so the questions still have something to read.
            if (state.Count == 0)
                foreach (KeyValuePair<string, object?> pair in context)
                    if (pair.Value != null) state[pair.Key] = pair.Value;
            return state;
        }

        private static TypedQuestion ToQuestion(CustomTypedQuestionSettings question)
        {
            switch ((question.Type ?? "noul").Trim().ToLowerInvariant())
            {
                case "choice":
                    return new ChoiceQuestion(question.Instructions, question.Options);
                case "score":
                    return new ScoreQuestion(question.Instructions, question.Levels);
                default:
                    return new NoulQuestion(question.Instructions, question.TrueMeaning, question.FalseMeaning);
            }
        }

        private static string Summarize(TypedDecisionResult result)
        {
            if (!result.Available) return result.UnavailableReason ?? "unavailable";
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, TypedAnswer> entry in result.Answers)
            {
                TypedAnswer answer = entry.Value;
                string value = answer.Choice
                    ?? (answer.Noul.HasValue ? answer.Noul.Value.ToString("0.00") : answer.Score.HasValue ? answer.Score.Value.ToString("0.0") : "?");
                parts.Add(entry.Key + "=" + value);
            }
            return String.Join(" ", parts);
        }

        private static Dictionary<string, double> GateValues(CustomTypedDecisionSettings definition, TypedDecisionResult result)
        {
            // The same reading as the built-in decisions: a Noul's value is the probability that its
            // statement is true, and the true pole is the finding, so a confidently FALSE answer never
            // flags. A Choice counts only when the model picked one of the question's finding options,
            // at the confidence it gave that option. A Score has no finding direction and never counts.
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.Ordinal);
            if (!result.Available || result.Answers == null) return values;
            foreach (CustomTypedQuestionSettings question in definition.Questions)
            {
                if (question == null || String.IsNullOrWhiteSpace(question.Id)) continue;
                if (!result.Answers.TryGetValue(question.Id, out TypedAnswer? answer) || answer == null) continue;
                string kind = (question.Type ?? "noul").Trim().ToLowerInvariant();
                if (kind == "noul" && answer.Noul.HasValue)
                {
                    values[question.Id] = answer.Noul.Value;
                }
                else if (kind == "choice"
                    && !String.IsNullOrWhiteSpace(answer.Choice)
                    && question.FlagOptions.Contains(answer.Choice!))
                {
                    values[question.Id] = TypedAnswerReader.ResolveChoiceConfidence(answer, answer.Choice!);
                }
            }
            return values;
        }

        private static double? Max(Dictionary<string, double> values)
        {
            double? best = null;
            foreach (double value in values.Values)
                if (!best.HasValue || value > best.Value) best = value;
            return best;
        }

        /// <summary>
        /// Whether a decision's vessel scope admits a vessel: an empty scope admits every vessel, and
        /// otherwise the vessel's name or id must be listed (case-insensitive).
        /// </summary>
        /// <param name="definition">The custom decision.</param>
        /// <param name="vesselId">The vessel id; may be null.</param>
        /// <param name="vesselName">The vessel name; may be null.</param>
        /// <returns>True when the decision reads this vessel's diffs.</returns>
        public static bool AppliesToVessel(CustomTypedDecisionSettings definition, string? vesselId, string? vesselName)
        {
            if (definition == null) return false;
            if (definition.Vessels.Count == 0) return true;
            foreach (string listed in definition.Vessels)
            {
                if (String.IsNullOrWhiteSpace(listed)) continue;
                if (!String.IsNullOrWhiteSpace(vesselId) && String.Equals(listed.Trim(), vesselId, StringComparison.OrdinalIgnoreCase)) return true;
                if (!String.IsNullOrWhiteSpace(vesselName) && String.Equals(listed.Trim(), vesselName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void AddIfPresent(Dictionary<string, object?> context, string field, string? value)
        {
            if (!String.IsNullOrWhiteSpace(value)) context[field] = value;
        }

        #endregion
    }

    /// <summary>The outcome of running a custom decision.</summary>
    public sealed class CustomDecisionOutcome
    {
        /// <summary>The decision name.</summary>
        public string Name { get; init; } = String.Empty;

        /// <summary>
        /// The status: <c>flagged</c>, <c>recorded</c> (Gate, not flagged), <c>shadow</c> (recorded in
        /// Shadow mode), <c>unavailable</c>, <c>inactive</c>, or <c>not_found</c>.
        /// </summary>
        public string Status { get; init; } = "inactive";

        /// <summary>The model answers, when the provider answered.</summary>
        public TypedDecisionResult? Result { get; init; }

        /// <summary>
        /// The gate value: the highest Noul probability among the answers, when any Noul was answered.
        /// Null when the decision asked no Noul, since Choice and Score answers never gate.
        /// </summary>
        public double? Confidence { get; init; }

        /// <summary>Whether the decision raised its advisory flag.</summary>
        public bool DidFlag => Status == "flagged";

        /// <summary>The decision's description, on a flag.</summary>
        public string Description { get; init; } = String.Empty;

        /// <summary>The question ids at or above the threshold, on a flag.</summary>
        public IReadOnlyList<string> FlaggedQuestions { get; init; } = Array.Empty<string>();

        internal static CustomDecisionOutcome NotFound(string name) => new CustomDecisionOutcome { Name = name, Status = "not_found" };
        internal static CustomDecisionOutcome Inactive(string name) => new CustomDecisionOutcome { Name = name, Status = "inactive" };
        internal static CustomDecisionOutcome Unavailable(string name, TypedDecisionResult r) => new CustomDecisionOutcome { Name = name, Status = "unavailable", Result = r };
        internal static CustomDecisionOutcome Recorded(string name, TypedDecisionResult r, double? c) => new CustomDecisionOutcome { Name = name, Status = "recorded", Result = r, Confidence = c };
        internal static CustomDecisionOutcome Shadow(string name, TypedDecisionResult r, double? c) => new CustomDecisionOutcome { Name = name, Status = "shadow", Result = r, Confidence = c };
        internal static CustomDecisionOutcome Flagged(string name, string description, TypedDecisionResult r, double c, IReadOnlyList<string> questions) => new CustomDecisionOutcome { Name = name, Status = "flagged", Description = description ?? String.Empty, Result = r, Confidence = c, FlaggedQuestions = questions };
    }
}
