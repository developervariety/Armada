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
    /// The D12 <c>followup_routing</c> decision adapter. For each item in a Judge's Suggested
    /// Follow-ups section it asks the typed-decision model for a home — a blocking follow-up voyage, a
    /// Triaged objective, an evidence note, or a duplicate of an existing objective — and, only in Gate
    /// mode at or above the decision threshold, performs the one additive action for that home through
    /// the injected <see cref="IFollowUpRouter"/>.
    ///
    /// The model NEVER creates a voyage: a <c>blocking_followup_voyage</c> home is only flagged for the
    /// operator, who decides. A <c>triaged_objective</c> home creates a Triaged objective with
    /// auto-dispatch OFF; <c>duplicate_of_existing</c> links to an existing open objective instead of
    /// creating one; <c>evidence_note</c> appends a note. The deterministic behaviour (no routing) is
    /// the fallback in every non-gate case: Off, an unavailable model, a Shadow-mode call, and a
    /// below-threshold answer all route nothing. The adapter never throws into the caller. This
    /// decision ships in Gate.
    /// </summary>
    public sealed class FollowUpRoutingAdapter
    {
        #region Public-Members

        /// <summary>
        /// The decision-point name in the <c>typedDecisions.decisions</c> settings map.
        /// </summary>
        public const string DecisionPoint = "followup_routing";

        /// <summary>Home: a blocking follow-up the operator must decide (never auto-created).</summary>
        public const string HomeBlocking = "blocking_followup_voyage";

        /// <summary>Home: a Triaged objective (auto-dispatch off) carrying real work.</summary>
        public const string HomeTriaged = "triaged_objective";

        /// <summary>Home: an evidence note, no new work.</summary>
        public const string HomeEvidence = "evidence_note";

        /// <summary>Home: a duplicate of an existing open objective, linked instead of created.</summary>
        public const string HomeDuplicate = "duplicate_of_existing";

        /// <summary>Upper bound on follow-up items routed from one section.</summary>
        public const int MaxItems = 12;

        /// <summary>Open objectives offered to the model's same_as duplicate question.</summary>
        public const int MaxDuplicateCandidates = 3;

        #endregion

        #region Private-Members

        private const string _Header = "[FollowUpRoutingAdapter] ";
        private const string _HomeQuestionId = "home";
        private const string _SameAsQuestionId = "same_as";

        private readonly TypedDecisionSettings _Settings;
        private readonly ITypedDecisionClient _Client;
        private readonly TypedDecisionRecorder _Recorder;
        private readonly IFollowUpRouter _Router;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a D12 follow-up-routing adapter.
        /// </summary>
        /// <param name="settings">Typed-decision settings section.</param>
        /// <param name="client">Typed-decision client (the null client when the system is off).</param>
        /// <param name="recorder">Recorder for the per-call typed-decision event.</param>
        /// <param name="router">Router performing the one additive action per home (never creates a voyage).</param>
        /// <param name="logging">Logging module.</param>
        public FollowUpRoutingAdapter(
            TypedDecisionSettings settings,
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            IFollowUpRouter router,
            LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            _Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _Router = router ?? throw new ArgumentNullException(nameof(router));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Route each item in the follow-up's Suggested Follow-ups section to a home. In Gate mode at or
        /// above threshold the routing actions are applied through the router; in every non-gate case
        /// nothing is routed. A blocking item is flagged for the operator, never created as a voyage.
        /// Never throws into the caller.
        /// </summary>
        /// <param name="followUp">The captured Judge follow-up whose section is routed.</param>
        /// <param name="objectiveTitle">The reviewed objective title, when known, for context.</param>
        /// <param name="token">Cancellation token, forwarded to the client so its timeout links to the caller.</param>
        /// <returns>A summary of the routes applied; never null.</returns>
        public async Task<FollowUpRoutingResult> RouteAsync(JudgeFollowUp followUp, string? objectiveTitle, CancellationToken token)
        {
            FollowUpRoutingResult result = new FollowUpRoutingResult();
            if (followUp == null) return result;

            ResolvedTypedDecision cfg = _Settings.For(DecisionPoint);
            if (cfg.Mode == TypedDecisionModeEnum.Off) return result;

            List<string> items = SplitItems(followUp.SuggestedFollowUps);
            if (items.Count == 0) return result;

            IReadOnlyList<FollowUpDuplicateCandidate> candidates;
            try
            {
                candidates = await _Router.GetDuplicateCandidatesAsync(followUp.VesselId, MaxDuplicateCandidates, token).ConfigureAwait(false)
                    ?? new List<FollowUpDuplicateCandidate>();
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "duplicate-candidate lookup failed, routing without candidates: " + ex.Message);
                candidates = new List<FollowUpDuplicateCandidate>();
            }

            // Items are classified together in as few requests as the limits allow; each item is still
            // routed and recorded on its own, in order.
            IReadOnlyDictionary<string, TypedQuestion> questions = BuildQuestions(candidates.Count);
            List<TypedDecisionBatchItem> batch = items
                .Select(item => new TypedDecisionBatchItem(
                    DecisionStateRedactor.RedactState(BuildState(followUp, objectiveTitle, item, candidates), _Settings.MaxStateChars),
                    questions))
                .ToList();
            List<TypedDecisionResult> decisions = await TypedDecisionBatcher.DecideAllAsync(
                _Client, DecisionPoint, batch, _Settings.MaxStateChars, token).ConfigureAwait(false);

            for (int index = 0; index < items.Count; index++)
            {
                string item = items[index];
                FollowUpItemOutcome outcome;
                try
                {
                    outcome = await RouteItemAsync(followUp, objectiveTitle, item, candidates, cfg, decisions[index], batch[index].State.Text, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A per-item fault must never break capture; stop routing the rest.
                    _Logging.Warn(_Header + "follow-up item routing failed, capture unaffected: " + ex.Message);
                    return result;
                }

                // The provider is unavailable for this section; route nothing further.
                if (!outcome.Available) return result;
                if (outcome.Applied) result.Routes.Add(outcome.Route!);
            }

            return result;
        }

        #endregion

        #region Private-Methods

        private async Task<FollowUpItemOutcome> RouteItemAsync(
            JudgeFollowUp followUp,
            string? objectiveTitle,
            string item,
            IReadOnlyList<FollowUpDuplicateCandidate> candidates,
            ResolvedTypedDecision cfg,
            TypedDecisionResult result,
            string redacted,
            CancellationToken token)
        {
            if (result == null || !result.Available)
            {
                await SafeRecordAsync(() => _Recorder.RecordUnavailableAsync(
                    BuildContext(followUp, "unrouted", null, null, result ?? TypedDecisionResult.Exception(), redacted), token)).ConfigureAwait(false);
                return new FollowUpItemOutcome { Available = false };
            }

            string home = InterpretHome(result, out double homeConfidence);
            double sameAs = InterpretSameAs(result, candidates.Count);
            bool apply = cfg.Mode == TypedDecisionModeEnum.Gate && homeConfidence >= cfg.GateThreshold;

            if (!apply)
            {
                string outcome = cfg.Mode == TypedDecisionModeEnum.Shadow ? "shadow_mode" : "below_threshold";
                await SafeRecordAsync(() => _Recorder.RecordShadowAsync(
                    BuildContext(followUp, "unrouted", home, homeConfidence, result, redacted), outcome, token)).ConfigureAwait(false);
                return new FollowUpItemOutcome { Available = true, Applied = false };
            }

            FollowUpRoute route = await ApplyHomeAsync(followUp, objectiveTitle, item, home, sameAs, candidates, result, cfg, token).ConfigureAwait(false);

            await SafeRecordAsync(() => _Recorder.RecordGatedAsync(
                BuildContext(followUp, "unrouted", route.Home, homeConfidence, result, redacted), token)).ConfigureAwait(false);

            return new FollowUpItemOutcome { Available = true, Applied = true, Route = route };
        }

        private async Task<FollowUpRoute> ApplyHomeAsync(
            JudgeFollowUp followUp,
            string? objectiveTitle,
            string item,
            string home,
            double sameAs,
            IReadOnlyList<FollowUpDuplicateCandidate> candidates,
            TypedDecisionResult result,
            ResolvedTypedDecision cfg,
            CancellationToken token)
        {
            FollowUpRouteRequest request = new FollowUpRouteRequest
            {
                FollowUp = followUp,
                ItemText = item,
                ObjectiveTitle = objectiveTitle
            };

            // A blocking item is NEVER created as a voyage by the model; it is flagged for the operator.
            if (String.Equals(home, HomeBlocking, StringComparison.Ordinal))
            {
                await _Router.FlagBlockingForOperatorAsync(request, token).ConfigureAwait(false);
                return new FollowUpRoute { Item = item, Home = HomeBlocking };
            }

            // A duplicate links to an existing open objective instead of creating one; without a
            // candidate confident enough there is nothing to link to, so it degrades to an evidence
            // note rather than inventing work.
            if (String.Equals(home, HomeDuplicate, StringComparison.Ordinal))
            {
                FollowUpDuplicateCandidate? best = SelectBestCandidate(item, candidates, result);
                if (best != null && sameAs >= cfg.GateThreshold)
                {
                    await _Router.LinkDuplicateAsync(request, best.ObjectiveId, token).ConfigureAwait(false);
                    return new FollowUpRoute { Item = item, Home = HomeDuplicate, TargetObjectiveId = best.ObjectiveId };
                }

                await _Router.AppendEvidenceNoteAsync(request, token).ConfigureAwait(false);
                return new FollowUpRoute { Item = item, Home = HomeEvidence };
            }

            // A Triaged objective (auto-dispatch off) carries real work.
            if (String.Equals(home, HomeTriaged, StringComparison.Ordinal))
            {
                string? objectiveId = await _Router.CreateTriagedObjectiveAsync(request, token).ConfigureAwait(false);
                return new FollowUpRoute { Item = item, Home = HomeTriaged, TargetObjectiveId = objectiveId };
            }

            // Default and evidence_note: append an evidence line, no new work.
            await _Router.AppendEvidenceNoteAsync(request, token).ConfigureAwait(false);
            return new FollowUpRoute { Item = item, Home = HomeEvidence };
        }

        private static List<string> SplitItems(string? section)
        {
            List<string> items = new List<string>();
            if (String.IsNullOrWhiteSpace(section)) return items;

            string[] lines = section.Replace("\r\n", "\n").Split('\n');
            List<string> markerItems = new List<string>();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (IsListMarker(line)) markerItems.Add(StripMarker(line));
            }

            if (markerItems.Count > 0)
            {
                foreach (string item in markerItems)
                {
                    if (item.Length == 0) continue;
                    items.Add(item);
                    if (items.Count >= MaxItems) break;
                }
                return items;
            }

            // No list markers: the whole section is one follow-up item.
            string whole = section.Replace("\r\n", "\n").Replace('\n', ' ').Trim();
            if (whole.Length > 0) items.Add(whole);
            return items;
        }

        private static bool IsListMarker(string line)
        {
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) return true;
            int dot = line.IndexOf('.');
            if (dot > 0 && dot <= 3 && line.Substring(0, dot).All(char.IsDigit)) return true;
            return false;
        }

        private static string StripMarker(string line)
        {
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                return line.Substring(2).Trim();
            int dot = line.IndexOf('.');
            if (dot > 0 && dot <= 3 && line.Substring(0, dot).All(char.IsDigit))
                return line.Substring(dot + 1).Trim();
            return line.Trim();
        }

        private static FollowUpDuplicateCandidate? SelectBestCandidate(
            string item,
            IReadOnlyList<FollowUpDuplicateCandidate> candidates,
            TypedDecisionResult result)
        {
            if (candidates == null || candidates.Count == 0) return null;

            IReadOnlyDictionary<string, TypedAnswer> answers = result?.Answers ?? new Dictionary<string, TypedAnswer>();
            int bestIndex = -1;
            double bestNoul = -1.0;
            int count = candidates.Count > MaxDuplicateCandidates ? MaxDuplicateCandidates : candidates.Count;
            for (int i = 0; i < count; i++)
            {
                if (!answers.TryGetValue(SameAsQuestionId(i), out TypedAnswer? answer) || answer == null || !answer.Noul.HasValue)
                    continue;
                if (answer.Noul.Value > bestNoul)
                {
                    bestNoul = answer.Noul.Value;
                    bestIndex = i;
                }
            }
            if (bestIndex >= 0) return candidates[bestIndex];

            HashSet<string> itemWords = Words(item);
            FollowUpDuplicateCandidate? best = null;
            double bestScore = -1.0;
            foreach (FollowUpDuplicateCandidate candidate in candidates)
            {
                HashSet<string> titleWords = Words(candidate.Title);
                int shared = titleWords.Count(word => itemWords.Contains(word));
                double score = titleWords.Count == 0 ? 0.0 : (double)shared / titleWords.Count;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            // Fall back to the most-recent candidate (first) when nothing shares a word.
            return best ?? candidates[0];
        }

        private static HashSet<string> Words(string? text)
        {
            HashSet<string> words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrWhiteSpace(text)) return words;
            foreach (string token in text.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ':', ';', '(', ')', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries))
                if (token.Length >= 3) words.Add(token);
            return words;
        }

        private static object BuildState(JudgeFollowUp followUp, string? objectiveTitle, string item, IReadOnlyList<FollowUpDuplicateCandidate> candidates)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["follow_up"] = item,
                ["verdict"] = followUp.JudgeVerdict,
                ["objective_title"] = objectiveTitle,
                ["vessel"] = followUp.VesselId,
                ["open_objectives"] = candidates.Select(candidate => candidate.Title).ToList()
            };
        }

        private static IReadOnlyDictionary<string, TypedQuestion> BuildQuestions(int candidateCount)
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                [_HomeQuestionId] = new ChoiceQuestion(
                    "Where should this Judge follow-up item in `follow_up` go so it is not lost?",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [HomeBlocking] = "it blocks progress and needs a follow-up voyage the operator must decide",
                        [HomeTriaged] = "it is real work to do later: a Triaged objective with auto-dispatch off",
                        [HomeEvidence] = "it is a note, not new work: an evidence line",
                        [HomeDuplicate] = "it duplicates an existing open objective and should link to it",
                        ["unclear"] = "none of the homes fit"
                    })
            };
            int count = candidateCount < 0 ? 0 : (candidateCount > MaxDuplicateCandidates ? MaxDuplicateCandidates : candidateCount);
            for (int i = 0; i < count; i++)
            {
                string path = "`open_objectives[" + i.ToString(CultureInfo.InvariantCulture) + "]`";
                questions[SameAsQuestionId(i)] = new NoulQuestion(
                    path + " describes the same work as this follow-up item.",
                    "the same work as this follow-up item",
                    "different work from this follow-up item");
            }
            return questions;
        }

        private static string InterpretHome(TypedDecisionResult result, out double confidence)
        {
            confidence = 0.0;
            IReadOnlyDictionary<string, TypedAnswer> answers = result.Answers ?? new Dictionary<string, TypedAnswer>();
            if (!answers.TryGetValue(_HomeQuestionId, out TypedAnswer? answer) || answer == null) return HomeEvidence;
            confidence = answer.Confidence ?? 0.0;
            return String.IsNullOrWhiteSpace(answer.Choice) ? HomeEvidence : answer.Choice!.Trim();
        }

        private static double InterpretSameAs(TypedDecisionResult result, int candidateCount)
        {
            IReadOnlyDictionary<string, TypedAnswer> answers = result.Answers ?? new Dictionary<string, TypedAnswer>();
            double max = 0.0;
            bool anySplit = false;
            int count = candidateCount < 0 ? 0 : (candidateCount > MaxDuplicateCandidates ? MaxDuplicateCandidates : candidateCount);
            for (int i = 0; i < count; i++)
            {
                string key = SameAsQuestionId(i);
                if (!answers.TryGetValue(key, out TypedAnswer? split) || split == null || !split.Noul.HasValue) continue;
                anySplit = true;
                if (split.Noul.Value > max) max = split.Noul.Value;
            }
            if (anySplit) return max;

            if (!answers.TryGetValue(_SameAsQuestionId, out TypedAnswer? answer) || answer == null) return 0.0;
            // A noul answer carries its probability in Noul and no confidence; a confidence is never a
            // stand-in for the probability that the statement is true.
            if (answer.Noul.HasValue) return answer.Noul.Value;
            return 0.0;
        }

        private static string SameAsQuestionId(int index)
        {
            return "same_as_" + index.ToString(CultureInfo.InvariantCulture);
        }

        private Task SafeRecordAsync(Func<Task> record)
        {
            return TypedDecisionRecording.SafeRecordAsync(record, _Logging, _Header);
        }

        private static TypedDecisionEventContext BuildContext(
            JudgeFollowUp followUp,
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

        private sealed class FollowUpItemOutcome
        {
            public bool Available { get; init; }
            public bool Applied { get; init; }
            public FollowUpRoute? Route { get; init; }
        }

        #endregion
    }

    /// <summary>
    /// The routes the D12 adapter applied for one follow-up section. Empty in every non-gate case.
    /// </summary>
    public sealed class FollowUpRoutingResult
    {
        /// <summary>The applied routes, one per routed follow-up item.</summary>
        public List<FollowUpRoute> Routes { get; } = new List<FollowUpRoute>();
    }

    /// <summary>
    /// One applied route: the follow-up item, the home the model chose, and the target objective id
    /// when the home created or linked one. A blocking home carries no target: it was only flagged.
    /// </summary>
    public sealed class FollowUpRoute
    {
        /// <summary>The follow-up item text.</summary>
        public required string Item { get; init; }

        /// <summary>The home chosen (one of the adapter's Home constants).</summary>
        public required string Home { get; init; }

        /// <summary>The created or linked objective id, when the home produced one.</summary>
        public string? TargetObjectiveId { get; init; }
    }
}
