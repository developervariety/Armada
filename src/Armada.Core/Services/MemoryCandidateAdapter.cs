namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The D18 <c>memory_candidate</c> decision adapter. After the weekly papercut grouping (and the
    /// D6 merges) it asks the typed decision client whether a papercut group is a durable lesson
    /// worth remembering, and — only in Gate mode, only at or above the decision threshold — stores a
    /// memory proposal in the database for the owner to promote or an operator to dismiss.
    ///
    /// The model never writes memory: it only nominates a candidate into the proposal store, never into
    /// the AI-Memory folder, and it never dismisses a proposal. The deterministic behaviour (no nomination) is always
    /// the fallback: an Off decision, an unavailable model, a below-threshold answer, and a
    /// <c>not_memory</c> scope all leave the memory untouched. This decision ships in Gate.
    /// </summary>
    public sealed class MemoryCandidateAdapter
    {
        #region Public-Members

        /// <summary>
        /// The decision-point name in the <c>typedDecisions.decisions</c> settings map.
        /// </summary>
        public const string DecisionPoint = "memory_candidate";

        #endregion

        #region Private-Members

        private const string _DurableQuestionId = "durable_lesson";
        private const string _ScopeQuestionId = "scope";

        private readonly TypedDecisionSettings _Settings;
        private readonly ITypedDecisionClient _Client;
        private readonly TypedDecisionRecorder _Recorder;
        private readonly IMemoryCandidateProposalWriter _Writer;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a memory-candidate adapter.
        /// </summary>
        /// <param name="settings">Typed-decision settings section.</param>
        /// <param name="client">Typed-decision client (null client when the system is off).</param>
        /// <param name="recorder">Recorder for the per-call typed-decision event.</param>
        /// <param name="writer">Writer for the proposal store (never writes memory itself).</param>
        /// <param name="logging">Logging module.</param>
        public MemoryCandidateAdapter(
            TypedDecisionSettings settings,
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            IMemoryCandidateProposalWriter writer,
            LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            _Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _Writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Consider each group as a durable-lesson candidate. Nominations (stored proposals) are
        /// returned; the deterministic behaviour is that nothing is nominated. The pass stops the
        /// first time the model is unavailable. Never throws into the caller.
        /// </summary>
        /// <param name="groups">The grouped papercuts to consider. Null is treated as empty.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The candidates nominated (written) this pass; never null.</returns>
        public async Task<List<MemoryCandidateProposal>> NominateAsync(IReadOnlyList<PapercutGroup>? groups, CancellationToken token)
        {
            List<MemoryCandidateProposal> nominated = new List<MemoryCandidateProposal>();
            if (groups == null || groups.Count == 0) return nominated;

            ResolvedTypedDecision cfg = _Settings.For(DecisionPoint);
            if (cfg.Mode == TypedDecisionModeEnum.Off) return nominated;

            // Groups are independent, so they are decided together in as few requests as the limits allow;
            // each group still gets its own recorded event.
            List<PapercutGroup> considered = groups.Where(group => group != null).ToList();
            List<TypedDecisionBatchItem> batch = considered
                .Select(group => new TypedDecisionBatchItem(DecisionStateRedactor.RedactState(BuildState(group), _Settings.MaxStateChars), Questions()))
                .ToList();
            List<TypedDecisionResult> results = await TypedDecisionBatcher.DecideAllAsync(
                _Client, DecisionPoint, batch, _Settings.MaxStateChars, token).ConfigureAwait(false);

            for (int index = 0; index < results.Count; index++)
            {
                MemoryCandidateOutcome outcome = await RecordAsync(considered[index], cfg, results[index], batch[index].State.Text, token).ConfigureAwait(false);
                if (!outcome.Available) return nominated; // provider down for this pass; fall back to no nomination
                if (outcome.Nominated && outcome.Proposal != null) nominated.Add(outcome.Proposal);
            }

            return nominated;
        }

        #endregion

        #region Private-Methods

        private async Task<MemoryCandidateOutcome> RecordAsync(PapercutGroup group, ResolvedTypedDecision cfg, TypedDecisionResult result, string redacted, CancellationToken token)
        {
            if (!result.Available)
            {
                await _Recorder.RecordUnavailableAsync(
                    BuildContext(group, "no_memory", null, null, result, redacted),
                    token).ConfigureAwait(false);
                return new MemoryCandidateOutcome { Available = false };
            }

            double durable = InterpretDurable(result);
            string scope = InterpretScope(result);
            bool durableEnough = durable >= cfg.GateThreshold;
            bool isMemory = !String.Equals(scope, "not_memory", StringComparison.Ordinal);
            bool proposeNominate = durableEnough && isMemory;
            bool applyNominate = cfg.Mode == TypedDecisionModeEnum.Gate && proposeNominate;
            string modelVerdict = proposeNominate ? scope : "not_durable";

            if (applyNominate)
            {
                MemoryCandidateProposal proposal = BuildProposal(group, durable, scope);
                proposal.ProposalId = await _Writer.WriteAsync(proposal, token).ConfigureAwait(false);

                await _Recorder.RecordGatedAsync(
                    BuildContext(group, "no_memory", modelVerdict, durable, result, redacted),
                    token).ConfigureAwait(false);

                return new MemoryCandidateOutcome
                {
                    Available = true,
                    Nominated = true,
                    Proposal = proposal,
                    Scope = scope,
                    DurableLesson = durable
                };
            }

            string outcomeLabel = cfg.Mode == TypedDecisionModeEnum.Shadow ? "shadow_mode" : "below_threshold";
            await _Recorder.RecordShadowAsync(
                BuildContext(group, "no_memory", modelVerdict, durable, result, redacted),
                outcomeLabel,
                token).ConfigureAwait(false);

            return new MemoryCandidateOutcome { Available = true, Nominated = false, Scope = scope, DurableLesson = durable };
        }

        private static object BuildState(PapercutGroup group)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = group.SampleTitle,
                ["detail"] = group.SampleDetail,
                ["count"] = group.Count,
                ["distinct_captains"] = group.DistinctCaptainCount,
                ["vessel"] = group.VesselId,
                ["category"] = group.Category.ToString()
            };
        }

        private static IReadOnlyDictionary<string, TypedQuestion> Questions()
        {
            return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                [_DurableQuestionId] = new NoulQuestion(
                    "Is this a durable lesson that would recur in a different session and a different tool, rather than current work state or a one-off?",
                    "a durable cross-session lesson",
                    "a one-off or current work state"),
                [_ScopeQuestionId] = new ChoiceQuestion(
                    "Where would this lesson belong in durable memory?",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["shared"] = "a rule that holds for every repository and every tool",
                        ["repo"] = "a rule specific to one vessel's repository",
                        ["machine"] = "a rule specific to one host",
                        ["not_memory"] = "not durable memory: current work state, a count, or a one-off"
                    })
            };
        }

        private static double InterpretDurable(TypedDecisionResult result)
        {
            if (result.Answers == null) return 0.0;
            if (!result.Answers.TryGetValue(_DurableQuestionId, out TypedAnswer? answer) || answer == null) return 0.0;
            // A noul answer carries its probability in Noul and no confidence; a confidence is never a
            // stand-in for the probability that the statement is true.
            if (answer.Noul.HasValue) return answer.Noul.Value;
            return 0.0;
        }

        private static string InterpretScope(TypedDecisionResult result)
        {
            if (result.Answers == null) return "not_memory";
            if (!result.Answers.TryGetValue(_ScopeQuestionId, out TypedAnswer? answer) || answer == null) return "not_memory";
            return String.IsNullOrWhiteSpace(answer.Choice) ? "not_memory" : answer.Choice!.Trim();
        }

        private MemoryCandidateProposal BuildProposal(PapercutGroup group, double durable, string scope)
        {
            // The proposal text is what the owner may copy into the AI-Memory repository, so it is
            // redacted the same way the transmitted state is: no Armada ids, paths, hosts, or hashes.
            // The related record ids stay in the admiral database for the operator to follow.
            int cap = _Settings.MaxStateChars;
            return new MemoryCandidateProposal
            {
                GroupKey = group.Key,
                Title = DecisionStateRedactor.Redact(group.SampleTitle, cap),
                Detail = DecisionStateRedactor.Redact(group.SampleDetail, cap),
                Category = group.Category.ToString(),
                Count = group.Count,
                DistinctCaptainCount = group.DistinctCaptainCount,
                Vessels = DecisionStateRedactor.Redact(group.VesselId, cap),
                DurableLesson = durable,
                Scope = ScopeDisplay(scope),
                RelatedRecordIds = new List<string>(group.SampleMissionIds),
                CreatedUtc = DateTime.UtcNow
            };
        }

        private static string ScopeDisplay(string scope)
        {
            // The vessel is redacted out of the proposal, so the repo scope is named generically; the
            // owner sets the real repos/<vessel> folder when promoting.
            if (String.Equals(scope, "repo", StringComparison.Ordinal))
                return "repos/<vessel>";
            if (String.Equals(scope, "machine", StringComparison.Ordinal))
                return "machine-notes";
            return scope;
        }

        private static TypedDecisionEventContext BuildContext(
            PapercutGroup group,
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
    }

    /// <summary>
    /// The outcome of considering one group as a durable-lesson candidate. <see cref="Nominated"/> is
    /// true only in Gate mode with a durable-lesson answer at or above the threshold and a scope that
    /// is not <c>not_memory</c>. <see cref="Available"/> is false when the model could not answer,
    /// which stops the pass.
    /// </summary>
    public sealed class MemoryCandidateOutcome
    {
        /// <summary>Whether the model produced a usable answer.</summary>
        public bool Available { get; init; }

        /// <summary>Whether a proposal was stored for this group.</summary>
        public bool Nominated { get; init; }

        /// <summary>The proposal, when nominated.</summary>
        public MemoryCandidateProposal? Proposal { get; init; }

        /// <summary>The scope the model chose.</summary>
        public string? Scope { get; init; }

        /// <summary>The durable-lesson scalar the gate compared to the threshold.</summary>
        public double DurableLesson { get; init; }
    }
}
