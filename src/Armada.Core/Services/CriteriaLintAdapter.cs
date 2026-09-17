namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The D10 <c>criteria_lint</c> decision adapter. When a refinement session returns a structured
    /// summary, each acceptance criterion is checked against the acceptance-criteria defects the
    /// operator memory names: a presence test over an artifact the change itself commits, a pinned
    /// pass or skip total, something not observable from a dock, a criterion satisfiable by an empty
    /// diff, and a criterion that mixes two behaviours needing separate tests. Every question is a
    /// noul phrased as the defect, so a high answer names a criterion problem.
    ///
    /// The adapter only ever ADDS <c>criteria_review</c> lines to the refinement summary the operator
    /// reads before ReadyForDispatch; it NEVER rewrites, reorders, or removes a criterion, and the
    /// acceptance-criteria list itself is left untouched. The deterministic summary is the fallback in
    /// every non-gate case: Off appends nothing and makes no call; an unavailable model appends nothing
    /// and records one unavailable event; a below-threshold answer appends nothing and records one
    /// shadow event. Only in Gate mode, and only for a criterion whose worst defect answer is at or
    /// above the decision threshold, are review lines appended. The adapter never throws into the
    /// caller. This decision ships in Gate.
    /// </summary>
    public sealed class CriteriaLintAdapter
    {
        #region Public-Members

        /// <summary>
        /// The decision-point name in the <c>typedDecisions.decisions</c> settings map.
        /// </summary>
        public const string DecisionPoint = "criteria_lint";

        /// <summary>
        /// Header line prefixing the appended block, so the operator and later tooling can find the
        /// model-flagged review lines in the refinement summary.
        /// </summary>
        public const string ReviewHeader = "criteria_review (model-flagged; resolve before ReadyForDispatch):";

        /// <summary>
        /// Upper bound on criteria checked per summary. A refinement summary with more criteria than
        /// this is unusual; the tail passes through unchecked rather than spending an unbounded number
        /// of model calls in an interactive path.
        /// </summary>
        public const int MaxCriteria = 30;

        #endregion

        #region Private-Members

        private const string _Header = "[CriteriaLintAdapter] ";

        private readonly TypedDecisionSettings _Settings;
        private readonly ITypedDecisionClient _Client;
        private readonly TypedDecisionRecorder _Recorder;
        private readonly LoggingModule _Logging;

        // The acceptance-criteria defect battery. Each entry is a noul phrased as the defect: a high
        // answer names a criterion problem the deterministic parse cannot settle on its own. The order
        // is fixed so the review lines read the same every time.
        private static readonly IReadOnlyList<CriterionNoul> _Nouls = new List<CriterionNoul>
        {
            new CriterionNoul("presence_test",
                "The criterion is satisfied by a presence test over an artifact the same change commits, so it stays green even if the producing code is deleted; it does not assert what the code must compute.",
                "a presence test over a committed artifact",
                "asserts a computed contract, not mere presence"),
            new CriterionNoul("pins_total",
                "The criterion pins a pass or skip total as a contract; only a failure count is a contract, and pass or skip totals drift between host and dock.",
                "pins a pass or skip total",
                "pins no pass or skip total"),
            new CriterionNoul("not_observable",
                "The criterion names something not observable from a dock: it is gitignored, in an unprovisioned sibling, or behind a tool the container lacks.",
                "not observable from a dock",
                "observable from a dock"),
            new CriterionNoul("empty_diff",
                "The criterion is satisfiable by an empty diff, so a mission that changes nothing could report it met.",
                "satisfiable by an empty diff",
                "requires a real change"),
            new CriterionNoul("mixes_behaviours",
                "The criterion mixes two behaviours that need separate tests, so one green result cannot prove both.",
                "mixes two behaviours",
                "names one behaviour")
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a D10 criteria-lint adapter.
        /// </summary>
        /// <param name="settings">Typed-decision settings section.</param>
        /// <param name="client">Typed-decision client (the null client when the system is off).</param>
        /// <param name="recorder">Recorder for the per-call typed-decision event.</param>
        /// <param name="logging">Logging module.</param>
        public CriteriaLintAdapter(
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
        /// Lint each acceptance criterion in the refinement summary and, in Gate mode at or above the
        /// decision threshold, append <c>criteria_review</c> lines the operator reads before
        /// ReadyForDispatch. The acceptance-criteria list is never modified. The summary passed in is
        /// returned unchanged in every non-gate case. Never throws into the caller.
        /// </summary>
        /// <param name="summary">The refinement summary; its <see cref="ObjectiveRefinementSummaryResponse.Summary"/> text may gain appended review lines.</param>
        /// <param name="kind">The objective Kind, given to the model as state.</param>
        /// <param name="token">Cancellation token, forwarded to the client so its timeout links to the caller.</param>
        /// <returns>A task that completes when the criteria have been linted and any review lines appended.</returns>
        public async Task EvaluateAsync(
            ObjectiveRefinementSummaryResponse summary,
            ObjectiveKindEnum kind,
            CancellationToken token)
        {
            if (summary == null) return;

            ResolvedTypedDecision cfg = _Settings.For(DecisionPoint);
            if (cfg.Mode == TypedDecisionModeEnum.Off) return;

            List<string> criteria = (summary.AcceptanceCriteria ?? new List<string>())
                .Where(item => !String.IsNullOrWhiteSpace(item))
                .Take(MaxCriteria)
                .ToList();
            if (criteria.Count == 0) return;

            // The deliverable sentence the model reads alongside each criterion is the summary's first
            // line; the whole summary would drown the one criterion under review.
            string deliverable = FirstLine(summary.Summary);

            // Criteria are independent, so they are answered together in as few requests as the limits
            // allow; each criterion still gets its own recorded event.
            List<TypedDecisionBatchItem> batch = new List<TypedDecisionBatchItem>(criteria.Count);
            for (int index = 0; index < criteria.Count; index++)
            {
                batch.Add(new TypedDecisionBatchItem(
                    DecisionStateRedactor.RedactState(BuildState(criteria[index], index + 1, kind, deliverable), _Settings.MaxStateChars),
                    BuildQuestions()));
            }

            List<TypedDecisionResult> results = await TypedDecisionBatcher.DecideAllAsync(
                _Client, DecisionPoint, batch, _Settings.MaxStateChars, token).ConfigureAwait(false);

            List<string> reviewLines = new List<string>();
            for (int index = 0; index < criteria.Count; index++)
            {
                CriterionOutcome outcome;
                try
                {
                    outcome = await RecordCriterionAsync(criteria[index], index + 1, cfg, results[index], batch[index].State.Text, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A per-criterion fault must never break the summary; leave it deterministic.
                    _Logging.Warn(_Header + "criterion lint failed, summary stands: " + ex.Message);
                    return;
                }

                // The provider is unavailable for this summary; append nothing. The deterministic
                // summary is the fallback.
                if (!outcome.Available) return;

                if (outcome.ReviewLines.Count > 0) reviewLines.AddRange(outcome.ReviewLines);
            }

            if (reviewLines.Count > 0) AppendReviewLines(summary, reviewLines);
        }

        #endregion

        #region Private-Methods

        private async Task<CriterionOutcome> RecordCriterionAsync(
            string criterion,
            int number,
            ResolvedTypedDecision cfg,
            TypedDecisionResult result,
            string redacted,
            CancellationToken token)
        {
            if (result == null || !result.Available)
            {
                await SafeRecordAsync(() => _Recorder.RecordUnavailableAsync(
                    BuildContext("criteria_clean", null, null, result ?? TypedDecisionResult.Exception(), redacted), token)).ConfigureAwait(false);
                return new CriterionOutcome { Available = false };
            }

            List<CriterionFlag> flags = Interpret(result, cfg.GateThreshold, out double worst);
            bool apply = cfg.Mode == TypedDecisionModeEnum.Gate && flags.Count > 0;
            string modelVerdict = flags.Count == 0 ? "criteria_clean" : String.Join(",", flags.Select(flag => flag.Id));

            List<string> lines = new List<string>();
            if (apply)
            {
                foreach (CriterionFlag flag in flags)
                    lines.Add("- criterion " + number.ToString(CultureInfo.InvariantCulture) + ": " + flag.Defect + " — \"" + OneLine(criterion) + "\"");

                await SafeRecordAsync(() => _Recorder.RecordGatedAsync(
                    BuildContext("criteria_clean", modelVerdict, worst, result, redacted), token)).ConfigureAwait(false);
            }
            else
            {
                string outcome = cfg.Mode == TypedDecisionModeEnum.Shadow ? "shadow_mode" : "below_threshold";
                await SafeRecordAsync(() => _Recorder.RecordShadowAsync(
                    BuildContext("criteria_clean", modelVerdict, worst, result, redacted), outcome, token)).ConfigureAwait(false);
            }

            return new CriterionOutcome { Available = true, ReviewLines = lines };
        }

        private static object BuildState(string criterion, int number, ObjectiveKindEnum kind, string deliverable)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["criterion_number"] = number,
                ["criterion"] = criterion,
                ["kind"] = kind.ToString(),
                ["deliverable"] = deliverable
            };
        }

        private static IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            foreach (CriterionNoul noul in _Nouls)
                questions[noul.Id] = new NoulQuestion(noul.Instructions, noul.TrueMeaning, noul.FalseMeaning);
            return questions;
        }

        private static List<CriterionFlag> Interpret(TypedDecisionResult result, double threshold, out double worst)
        {
            List<CriterionFlag> flags = new List<CriterionFlag>();
            worst = 0.0;
            IReadOnlyDictionary<string, TypedAnswer> answers = result.Answers ?? new Dictionary<string, TypedAnswer>();
            foreach (CriterionNoul noul in _Nouls)
            {
                if (!answers.TryGetValue(noul.Id, out TypedAnswer? answer) || answer == null) continue;
                double value = NoulValue(answer);
                if (value > worst) worst = value;
                if (value >= threshold) flags.Add(new CriterionFlag(noul.Id, noul.Defect, value));
            }
            return flags;
        }

        private static double NoulValue(TypedAnswer answer)
        {
            // A noul answer carries its probability in Noul and no confidence; a confidence is never a
            // stand-in for the probability that the statement is true.
            if (answer.Noul.HasValue) return answer.Noul.Value;
            return 0.0;
        }

        private static void AppendReviewLines(ObjectiveRefinementSummaryResponse summary, List<string> reviewLines)
        {
            StringBuilder sb = new StringBuilder();
            if (!String.IsNullOrWhiteSpace(summary.Summary))
            {
                sb.Append(summary.Summary!.TrimEnd());
                sb.Append("\n\n");
            }
            sb.Append(ReviewHeader);
            foreach (string line in reviewLines)
            {
                sb.Append('\n');
                sb.Append(line);
            }
            summary.Summary = sb.ToString();
        }

        private static string FirstLine(string? text)
        {
            if (String.IsNullOrWhiteSpace(text)) return String.Empty;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }
            return String.Empty;
        }

        private static string OneLine(string text)
        {
            return (text ?? String.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private Task SafeRecordAsync(Func<Task> record)
        {
            return TypedDecisionRecording.SafeRecordAsync(record, _Logging, _Header);
        }

        private static TypedDecisionEventContext BuildContext(
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
                RedactedState = redactedState
            };
        }

        #endregion

        #region Private-Types

        private sealed class CriterionNoul
        {
            public CriterionNoul(string id, string instructions, string trueMeaning, string falseMeaning)
            {
                Id = id;
                Instructions = instructions;
                Defect = trueMeaning;
                TrueMeaning = trueMeaning;
                FalseMeaning = falseMeaning;
            }

            public string Id { get; }
            public string Instructions { get; }
            public string Defect { get; }
            public string TrueMeaning { get; }
            public string FalseMeaning { get; }
        }

        private sealed class CriterionFlag
        {
            public CriterionFlag(string id, string defect, double value)
            {
                Id = id;
                Defect = defect;
                Value = value;
            }

            public string Id { get; }
            public string Defect { get; }
            public double Value { get; }
        }

        private sealed class CriterionOutcome
        {
            public bool Available { get; init; }
            public List<string> ReviewLines { get; init; } = new List<string>();
        }

        #endregion
    }
}
