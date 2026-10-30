namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;
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
    /// The adapter only ever ADDS preview issues; it never removes a deterministic issue, never
    /// dispatches, and never lands. Q4 and Q13 model readings are advisory Warnings even in Gate mode.
    /// Other flagged text questions can add blocking Errors in Gate mode. The deterministic preview is the
    /// fallback in every non-gate case: Off adds nothing and makes no call; an unavailable model adds
    /// nothing and records one unavailable event; a below-threshold answer adds nothing and records one
    /// rule-kept event. In Gate mode a blocking question answered at or above the decision threshold becomes
    /// an Error issue <see cref="ModelFlagIssueCode"/> that the autonomous scheduler already skips on. A
    /// Q4 or Q13 model flag never blocks dispatch. When no deterministic
    /// fact fails, a Q1 flag is not raised while the Q1 citation fact passes: the model answers with a number
    /// only and cannot name a disputed claim. A Q9 flag is a Warning while the
    /// brief states two different proof values for the corrected and the defective code
    /// (<see cref="ProofValueChangeDetector"/>). A Q1 flag is also omitted when the Q1 citation fact fails only on
    /// citations outside the target repository and no other fact fails. The adapter never throws into the caller.
    ///
    /// Answers are cached by the redacted state's hash, the question contract's hash, the configured model
    /// and the model version the provider last reported. A repeat preview of an unchanged objective reuses
    /// the stored answers, applies them under the current mode and threshold exactly as a fresh call would,
    /// and records one <see cref="TypedDecisionRecorder.EventTypeCacheHit"/> event instead of a new call. A
    /// changed state, question contract or configured model misses; a newly reported model version empties
    /// the cache. Only available answers are cached, each for <see cref="CacheLifetime"/>, and at most
    /// <see cref="MaxCacheEntries"/> are kept.
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

        /// <summary>
        /// How long a cached answer is reused.
        /// </summary>
        public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);

        /// <summary>
        /// Most cached answers kept; the oldest is dropped first.
        /// </summary>
        public const int MaxCacheEntries = 4096;

        #endregion

        #region Private-Members

        private const string _Header = "[PreflightTextAdapter] ";
        private const string _Area = "preflight";
        private const string _Q13Id = "q13";
        private const string _Q13NeedsOwnerRuling = "needs_owner_ruling";
        private const string _Q13NeedsRepoFact = "needs_repo_fact";
        private const int _ExampleValueQuestion = 9;
        private const int _PremiseQuestion = 1;
        private const int _MaxQuotedLines = 3;
        private const int _MaxQuoteLength = 160;

        // A brief line that states the value proving a fix, and a heading or line that declares values
        // unchanged on purpose. The model answers question 9 with a number only, so these lines are quoted
        // beside its flag for the operator to check.
        private static readonly Regex _ProofLine = new Regex(
            @"\bprov(?:e|es|ed|ing)\b|\bproof\b|\bexpected\b|\bbefore\b.*\bafter\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex _ParityLine = new Regex(
            @"\bparity\b|\bunchanged\s+on\s+purpose\b|\bunchanged\s+by\s+design\b|\bcontrol\s+values?\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly TypedDecisionSettings _Settings;
        private readonly ITypedDecisionClient _Client;
        private readonly TypedDecisionRecorder _Recorder;
        private readonly LoggingModule _Logging;
        private readonly object _CacheLock = new object();
        private readonly Dictionary<string, CachedAnswer> _Cache = new Dictionary<string, CachedAnswer>(StringComparer.Ordinal);
        private string? _LastReportedModel = null;

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
                "A premise or a deliverable is not observable from a dock: it is gitignored, in a sibling checkout the dock context does not list, "
                + "or needs a tool the dock context lists as missing. A sibling checkout, a tool or an evidence root the dock context lists is "
                + "observable, and so is a claim whose evidence link names one of them.",
                "a premise or deliverable is unobservable from a dock", "everything is observable from a dock", blocksDispatch: false),
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
                "Find the value the brief names as the proof of the fix (for example after 'what proves the fix' or 'expected'). The defect "
                + "is present only when that proof value is the same in the defective and the corrected code, so a green test asserting it "
                + "would prove nothing. Ignore every value the brief declares unchanged on purpose, parity or control: those must stay the "
                + "same, so their not changing is correct and is never this defect. Ignore values that guard a different defect. When the "
                + "brief states that its proof value changes (for example from one to zero) between the defective and the corrected code, "
                + "the answer is false. When the brief names no proof value, this question does not apply and the answer is false.",
                "the named proof value is the same before and after the fix", "the named proof value changes with the fix"),
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
        /// <param name="logging">Logging module.</param>
        public PreflightTextAdapter(
            TypedDecisionSettings settings,
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            _Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Consult the model for the text-half preflight battery and, in Gate mode at or above the
        /// decision threshold, add blocking Error issues and advisory Warning issues to the preview.
        /// Q4 and Q13 are always advisory and do not post owner-addressed notes. The deterministic preview passed in is
        /// returned unchanged in every other case. Never throws into the caller.
        /// </summary>
        /// <param name="objective">The objective being previewed.</param>
        /// <param name="vessel">The resolved target vessel.</param>
        /// <param name="pipeline">The resolved pipeline, or null for the Worker-only path.</param>
        /// <param name="result">The dispatch preview built by the deterministic block; mutated in place.</param>
        /// <param name="token">Cancellation token, forwarded to the client so its timeout links to the caller.</param>
        /// <param name="dock">What a captain dock for this objective provides, when the caller measured it.</param>
        /// <returns>A task that completes when the decision has been consulted and applied.</returns>
        public async Task EvaluateAsync(
            Objective objective,
            Vessel vessel,
            Pipeline? pipeline,
            ObjectiveDispatchPreview result,
            CancellationToken token,
            PreflightDockContext? dock = null)
        {
            if (objective == null || vessel == null || result == null) return;

            ResolvedTypedDecision cfg = _Settings.For(DecisionPoint);
            if (cfg.Mode == TypedDecisionModeEnum.Off) return;

            // The shared egress guard: an objective naming an excluded vessel, a target vessel on the list, or a
            // state naming an excluded marker sends nothing. The preview stays deterministic and records why.
            string? refusal = TypedDecisionEgress.Refusal(_Settings, DecisionPoint, TypedDecisionEgress.VesselsOf(objective.VesselIds, vessel.Id),
                () => BuildState(objective, vessel, pipeline, result, dock));
            if (refusal != null)
            {
                await SafeRecordAsync(() => _Recorder.RecordUnavailableAsync(
                    BuildContext(objective, result, TypedDecisionEgress.Refused(refusal), null, null, String.Empty), token)).ConfigureAwait(false);
                return;
            }

            TypedDecisionBatchItem? item = DescribeRequest(objective, vessel, pipeline, result, dock);
            if (item == null) return;
            string redacted = item.State.Text;
            TypedDecisionRequest request = new TypedDecisionRequest
            {
                DecisionPoint = DecisionPoint,
                State = item.State.State,
                Questions = item.Questions
            };

            string questionsSha256 = QuestionContractSha256(request.Questions);
            TypedDecisionResult? cached = ReadCache(redacted, questionsSha256);
            TypedDecisionResult decision;
            if (cached != null)
            {
                decision = cached;
            }
            else
            {
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
                        BuildContext(objective, result, TypedDecisionResult.Exception(), null, null, redacted), token)).ConfigureAwait(false);
                    return;
                }

                if (decision != null && decision.Available) StoreCache(redacted, questionsSha256, decision);
            }

            if (decision == null || !decision.Available)
            {
                await SafeRecordAsync(() => _Recorder.RecordUnavailableAsync(
                    BuildContext(objective, result, decision ?? TypedDecisionResult.Exception(), null, null, redacted), token)).ConfigureAwait(false);
                return;
            }

            PreflightModelReading reading = Interpret(decision, cfg.GateThreshold);
            bool gate = cfg.Mode == TypedDecisionModeEnum.Gate;

            // Keep uncertain dock-observability and owner-question readings visible without letting
            // the classifier block dispatch. Other questions retain the Gate blocking behavior.
            // Q1 asks whether the deterministic facts contradict the premise. When the citation fact passes and no
            // fact fails, there is no contradiction for the model to report. The model answers with a number only,
            // so a q1 flag cannot name a disputed claim and is not raised.
            // Q9 asks whether the proof value is the same before and after the fix. When the brief itself states two
            // different values for the corrected and the defective code and no fact fails, the question's own contract
            // answers false, so its q9 flag stays a warning.
            bool noFactFails = NoFactFails(result);
            // A citation fact that fails only on paths outside the target repository, with no other fact failing,
            // is not a contradiction the target tip shows, so its q1 flag is not raised either.
            bool premiseProven = (noFactFails && PremiseFactPasses(result)) || PremiseFailsOnlyOutsideTarget(result);
            string? statedChange = noFactFails
                ? ProofValueChangeDetector.Find(objective.Description, objective.AcceptanceCriteria)
                : null;
            PreflightDemotion demotion = new PreflightDemotion(premiseProven, statedChange != null);
            foreach (PreflightFlag flag in reading.Flags)
            {
                if (flag.QuestionNumber == _PremiseQuestion && demotion.PremiseProven)
                    continue;
                string evidence = flag.QuestionNumber == _ExampleValueQuestion ? ExampleValueEvidence(objective) : String.Empty;
                if (flag.QuestionNumber == _ExampleValueQuestion && statedChange != null)
                    evidence = " The brief states a proof value that changes with the fix: " + QuoteOne(statedChange) + "." + evidence;
                if (gate && Blocks(flag, demotion))
                {
                    AddIssue(result, ModelFlagIssueCode, ReadinessSeverityEnum.Error,
                        "Dispatch preflight model flag on question " + flag.QuestionNumber + ": " + flag.Defect + evidence,
                        "q" + flag.QuestionNumber.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    AddIssue(result, "preflight_q" + flag.QuestionNumber.ToString(CultureInfo.InvariantCulture) + "_model",
                        ReadinessSeverityEnum.Warning,
                        "Dispatch preflight model note on question " + flag.QuestionNumber + ": " + flag.Defect + evidence,
                        "q" + flag.QuestionNumber.ToString(CultureInfo.InvariantCulture));
                }
            }

            // Q13 is already supported by the deterministic preflight and recorded owner answers.
            // The model may add a visible warning, but it cannot block dispatch or create an owner task.
            if (reading.Q13Flagged)
            {
                AddIssue(result, "preflight_q13_model", ReadinessSeverityEnum.Warning,
                    "Dispatch preflight model note on question 13: an open " + reading.Q13Choice + " may exist.",
                    "q13");
            }

            bool applied = gate && reading.Flags.Any(flag => Blocks(flag, demotion));
            bool advisory = reading.HasAdvisory || reading.Flags.Any(flag => flag.BlocksDispatch && !Blocks(flag, demotion));
            if (cached != null)
            {
                string cachedOutcome = applied
                    ? "applied"
                    : cfg.Mode == TypedDecisionModeEnum.Shadow
                        ? "shadow_mode"
                        : advisory ? "advisory_only" : "below_threshold";
                await SafeRecordAsync(() => _Recorder.RecordCacheHitAsync(
                    BuildContext(objective, result, decision, reading.VerdictLabel, reading.MaxConfidence, redacted), cachedOutcome, token)).ConfigureAwait(false);
            }
            else if (applied)
            {
                await SafeRecordAsync(() => _Recorder.RecordGatedAsync(
                    BuildContext(objective, result, decision, reading.VerdictLabel, reading.MaxConfidence, redacted), token)).ConfigureAwait(false);
            }
            else
            {
                string outcome = cfg.Mode == TypedDecisionModeEnum.Shadow
                    ? "shadow_mode"
                    : advisory ? "advisory_only" : "below_threshold";
                await SafeRecordAsync(() => _Recorder.RecordRuleKeptAsync(
                    BuildContext(objective, result, decision, reading.VerdictLabel, reading.MaxConfidence, redacted), outcome, token)).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Build the exact redacted state and questions this decision sends for an objective, without calling the
        /// provider or recording anything. The evaluation set uses it so a case tests the questions production asks.
        /// Returns null when the state or questions cannot be built; the preview then stays deterministic.
        /// </summary>
        /// <param name="objective">The objective being previewed.</param>
        /// <param name="vessel">The resolved target vessel.</param>
        /// <param name="pipeline">The resolved pipeline, or null for the Worker-only path.</param>
        /// <param name="result">The dispatch preview built by the deterministic block; read only.</param>
        /// <param name="dock">What a captain dock for this objective provides, when the caller measured it.</param>
        /// <returns>The request item, or null.</returns>
        public TypedDecisionBatchItem? DescribeRequest(
            Objective objective,
            Vessel vessel,
            Pipeline? pipeline,
            ObjectiveDispatchPreview result,
            PreflightDockContext? dock = null)
        {
            try
            {
                RedactedDecisionState redactedState = DecisionStateRedactor.RedactState(BuildState(objective, vessel, pipeline, result, dock), _Settings.MaxStateChars);
                return new TypedDecisionBatchItem(redactedState, QuestionFactory());
            }
            catch (Exception ex)
            {
                // Building state or questions must never break the preview; leave it deterministic.
                _Logging.Warn(_Header + "state build failed, deterministic preview stands: " + ex.Message);
                return null;
            }
        }

        #endregion

        #region Internal-Members

        /// <summary>
        /// Builds the question battery sent with each call. Replaced only by tests that change the
        /// question contract.
        /// </summary>
        internal Func<IReadOnlyDictionary<string, TypedQuestion>> QuestionFactory { get; set; } = BuildQuestions;

        #endregion

        #region Private-Methods

        private TypedDecisionResult? ReadCache(string redactedState, string questionsSha256)
        {
            lock (_CacheLock)
            {
                string key = CacheKey(redactedState, questionsSha256);
                if (!_Cache.TryGetValue(key, out CachedAnswer? entry)) return null;
                if (DateTime.UtcNow - entry.StoredUtc > CacheLifetime)
                {
                    _Cache.Remove(key);
                    return null;
                }

                return entry.Result;
            }
        }

        private void StoreCache(string redactedState, string questionsSha256, TypedDecisionResult result)
        {
            lock (_CacheLock)
            {
                // A model version the provider reports for the first time makes every stored answer stale.
                if (!String.IsNullOrWhiteSpace(result.Model)
                    && !String.Equals(result.Model, _LastReportedModel, StringComparison.Ordinal))
                {
                    _Cache.Clear();
                    _LastReportedModel = result.Model;
                }

                if (_Cache.Count >= MaxCacheEntries)
                {
                    string oldest = _Cache.OrderBy(pair => pair.Value.StoredUtc).First().Key;
                    _Cache.Remove(oldest);
                }

                _Cache[CacheKey(redactedState, questionsSha256)] = new CachedAnswer(result, DateTime.UtcNow);
            }
        }

        private string CacheKey(string redactedState, string questionsSha256)
        {
            return Sha256(redactedState) + "|" + questionsSha256 + "|" + (_Settings.Model ?? String.Empty)
                + "|" + (_LastReportedModel ?? String.Empty);
        }

        private static string QuestionContractSha256(IReadOnlyDictionary<string, TypedQuestion> questions)
        {
            StringBuilder contract = new StringBuilder();
            foreach (KeyValuePair<string, TypedQuestion> entry in questions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                contract.Append(entry.Key).Append('=').Append(entry.Value?.GetType().Name ?? "null").Append(':')
                    .Append(entry.Value == null ? "null" : JsonSerializer.Serialize<object>(entry.Value)).Append('\n');
            }

            return Sha256(contract.ToString());
        }

        private static string Sha256(string text)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? String.Empty))).ToLowerInvariant();
        }

        private static object BuildState(Objective objective, Vessel vessel, Pipeline? pipeline, ObjectiveDispatchPreview result, PreflightDockContext? dock)
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
                ["facts"] = facts,
                ["evidence_links"] = (objective.EvidenceLinks ?? new List<string>())
                    .Where(item => !String.IsNullOrWhiteSpace(item)).ToList(),
                ["claims"] = (objective.Preparation?.Claims ?? new List<ObjectivePreparationClaim>())
                    .Where(claim => claim != null && !String.IsNullOrWhiteSpace(claim.Text))
                    .Select(claim => new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["kind"] = claim.Kind.ToString(),
                        ["state"] = claim.State.ToString(),
                        ["text"] = claim.Text,
                        ["evidence_links"] = (claim.EvidenceLinks ?? new List<string>()).Where(item => !String.IsNullOrWhiteSpace(item)).ToList()
                    }).ToList(),
                ["dock"] = DockState(dock)
            };
        }

        // The dock a captain receives. Unmeasured is stated, so the model never reads an absent list as an empty dock.
        private static object DockState(PreflightDockContext? dock)
        {
            if (dock == null)
                return new Dictionary<string, object?>(StringComparer.Ordinal) { ["measured"] = false };
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["measured"] = true,
                ["operating_system"] = dock.OperatingSystem,
                ["container"] = dock.IsContainer,
                ["sibling_checkouts"] = dock.SiblingCheckouts.ToList(),
                ["available_tools"] = dock.AvailableTools.ToList(),
                ["missing_tools"] = dock.MissingTools.ToList(),
                ["evidence_roots"] = dock.EvidenceRoots.ToList(),
                ["missing_evidence_roots"] = dock.MissingEvidenceRoots.ToList()
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
                    reading.Flags.Add(new PreflightFlag(noul.Number, noul.Defect, value, noul.BlocksDispatch));
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

        /// <summary>
        /// The brief lines a question-9 flag may refer to, quoted for the operator: the lines that state a proof or
        /// expected value, and apart from them the values the brief declares unchanged on purpose, which the question
        /// excludes. A declared section runs from its heading to the next blank line or heading.
        /// </summary>
        private static string ExampleValueEvidence(Objective objective)
        {
            List<string> proof = new List<string>();
            List<string> parity = new List<string>();
            List<string> lines = new List<string>();
            lines.AddRange((objective.Description ?? String.Empty).Split('\n'));
            lines.AddRange(objective.AcceptanceCriteria ?? new List<string>());

            bool inParity = false;
            foreach (string raw in lines)
            {
                string line = (raw ?? String.Empty).Trim();
                if (line.Length == 0)
                {
                    inParity = false;
                    continue;
                }
                bool heading = line.StartsWith("#", StringComparison.Ordinal) || line.EndsWith(":", StringComparison.Ordinal);
                bool declaresParity = _ParityLine.IsMatch(line);
                if (heading) inParity = declaresParity;
                if (declaresParity || inParity)
                {
                    Quote(parity, line);
                    continue;
                }
                foreach (string sentence in line.Split(new[] { ". " }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (_ProofLine.IsMatch(sentence)) Quote(proof, sentence);
                }
            }

            StringBuilder evidence = new StringBuilder();
            evidence.Append(" The model answers with a number only.");
            if (proof.Count == 0)
                evidence.Append(" No brief line states a proof or expected value; name the value that proves the fix and how it changes.");
            else
                evidence.Append(" Brief lines that state a proof or expected value: ").Append(String.Join("; ", proof)).Append('.');
            if (parity.Count > 0)
                evidence.Append(" Declared unchanged on purpose, which question 9 excludes: ").Append(String.Join("; ", parity)).Append('.');
            return evidence.ToString();
        }

        private static void Quote(List<string> quotes, string text)
        {
            if (quotes.Count >= _MaxQuotedLines) return;
            string trimmed = text.Trim().TrimEnd('.');
            if (trimmed.Length == 0) return;
            if (trimmed.Length > _MaxQuoteLength) trimmed = trimmed.Substring(0, _MaxQuoteLength) + "...";
            quotes.Add("\"" + trimmed + "\"");
        }

        private static double NoulValue(TypedAnswer answer)
        {
            if (answer.Noul.HasValue) return answer.Noul.Value;
            if (answer.Confidence.HasValue) return answer.Confidence.Value;
            return 0.0;
        }

        private Task SafeRecordAsync(Func<Task> record)
        {
            return TypedDecisionRecording.SafeRecordAsync(record, _Logging, _Header);
        }

        private static TypedDecisionEventContext BuildContext(
            Objective objective,
            ObjectiveDispatchPreview preview,
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
                RedactedState = redactedState,
                ObjectiveId = objective.Id,
                RuleAnswers = RuleAnswers(preview)
            };
        }

        private static bool Blocks(PreflightFlag flag, PreflightDemotion demotion)
        {
            if (!flag.BlocksDispatch) return false;
            if (demotion.PremiseProven && flag.QuestionNumber == _PremiseQuestion) return false;
            if (demotion.ProofValueChangeStated && flag.QuestionNumber == _ExampleValueQuestion) return false;
            return true;
        }

        private static bool NoFactFails(ObjectiveDispatchPreview result)
        {
            List<ObjectiveDispatchPreflightFact> facts = result.Preflight?.Facts ?? new List<ObjectiveDispatchPreflightFact>();
            return !facts.Any(fact => fact != null && fact.Status == PreflightFactStatusEnum.Fail);
        }

        private static bool PremiseFactPasses(ObjectiveDispatchPreview result)
        {
            List<ObjectiveDispatchPreflightFact> facts = result.Preflight?.Facts ?? new List<ObjectiveDispatchPreflightFact>();
            return facts.Any(fact => fact != null && fact.QuestionNumber == _PremiseQuestion && fact.Status == PreflightFactStatusEnum.Pass);
        }

        private static bool PremiseFailsOnlyOutsideTarget(ObjectiveDispatchPreview result)
        {
            List<ObjectiveDispatchPreflightFact> facts = result.Preflight?.Facts ?? new List<ObjectiveDispatchPreflightFact>();
            bool otherFails = facts.Any(fact => fact != null && fact.QuestionNumber != _PremiseQuestion
                && fact.Status == PreflightFactStatusEnum.Fail);
            return !otherFails && facts.Any(fact => fact != null && fact.QuestionNumber == _PremiseQuestion
                && fact.Status == PreflightFactStatusEnum.Fail && fact.FailsOnlyOutsideTarget);
        }

        private static string QuoteOne(string text)
        {
            List<string> quoted = new List<string>();
            Quote(quoted, text);
            return quoted.Count > 0 ? quoted[0] : "\"\"";
        }

        /// <summary>
        /// The deterministic preflight's answer to each text-half question, in the question's defect polarity.
        /// Only Q1 has a deterministic fact (the citations resolve at the target tip): a failed fact answers true
        /// and a passed fact false. Every other question, and an unknown fact, has no deterministic answer.
        /// </summary>
        private static IReadOnlyDictionary<string, object?> RuleAnswers(ObjectiveDispatchPreview preview)
        {
            Dictionary<string, object?> answers = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (PreflightNoul noul in _Nouls) answers[noul.Id] = null;
            answers[_Q13Id] = null;
            ObjectiveDispatchPreflightFact? q1 = (preview?.Preflight?.Facts ?? new List<ObjectiveDispatchPreflightFact>())
                .FirstOrDefault(fact => fact != null && fact.QuestionNumber == 1);
            if (q1 != null && q1.Status != PreflightFactStatusEnum.Unknown)
                answers["q1"] = q1.Status == PreflightFactStatusEnum.Fail;
            return answers;
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

        private sealed class CachedAnswer
        {
            public CachedAnswer(TypedDecisionResult result, DateTime storedUtc)
            {
                Result = result;
                StoredUtc = storedUtc;
            }

            public TypedDecisionResult Result { get; }
            public DateTime StoredUtc { get; }
        }

        private sealed class PreflightNoul
        {
            public PreflightNoul(string id, int number, string instructions, string trueMeaning, string falseMeaning, bool blocksDispatch = true)
            {
                Id = id;
                Number = number;
                Instructions = instructions;
                Defect = instructions;
                TrueMeaning = trueMeaning;
                FalseMeaning = falseMeaning;
                BlocksDispatch = blocksDispatch;
            }

            public string Id { get; }
            public int Number { get; }
            public string Instructions { get; }
            public string Defect { get; }
            public string TrueMeaning { get; }
            public string FalseMeaning { get; }
            public bool BlocksDispatch { get; }
        }

        private sealed class PreflightFlag
        {
            public PreflightFlag(int questionNumber, string defect, double value, bool blocksDispatch)
            {
                QuestionNumber = questionNumber;
                Defect = defect;
                Value = value;
                BlocksDispatch = blocksDispatch;
            }

            public int QuestionNumber { get; }
            public string Defect { get; }
            public double Value { get; }
            public bool BlocksDispatch { get; }
        }

        // The deterministic evidence that omits a Q1 flag, or that keeps a Q9 flag as a warning.
        private sealed class PreflightDemotion
        {
            public PreflightDemotion(bool premiseProven, bool proofValueChangeStated)
            {
                PremiseProven = premiseProven;
                ProofValueChangeStated = proofValueChangeStated;
            }

            public bool PremiseProven { get; }
            public bool ProofValueChangeStated { get; }
        }

        private sealed class PreflightModelReading
        {
            public List<PreflightFlag> Flags { get; } = new List<PreflightFlag>();
            public bool Q13Flagged { get; set; }
            public string? Q13Choice { get; set; }
            public double MaxConfidence { get; set; }
            public bool HasAdvisory => Q13Flagged || Flags.Any(flag => !flag.BlocksDispatch);

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
