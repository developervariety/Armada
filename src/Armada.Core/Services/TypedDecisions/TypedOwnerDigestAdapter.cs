namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// One owner-decision candidate the D13 <c>owner_digest</c> runner has collected from a hit
    /// source: a question only the owner can answer, the row it blocks, how many rows chain behind
    /// that row, and how long it has waited. The candidate carries a proposed default the owner can
    /// accept; the digest lists it as a suggestion and never acts on it.
    /// </summary>
    public sealed class OwnerDigestCandidate
    {
        /// <summary>The question text put to the owner. Never truncated here; the redactor bounds it before egress.</summary>
        public required string QuestionText { get; init; }

        /// <summary>A short label for the row the question blocks (an id or title), for the owner-addressed note.</summary>
        public string BlockedRow { get; init; } = String.Empty;

        /// <summary>How many rows chain behind the blocked row. A larger fan-out raises the deterministic cost.</summary>
        public int ChainCount { get; init; }

        /// <summary>How long the question has waited, in hours. Older questions carry a higher deterministic cost.</summary>
        public double AgeHours { get; init; }

        /// <summary>The default the owner could accept, when one was stated. Empty means no default is proposed.</summary>
        public string ProposedDefault { get; init; } = String.Empty;

        /// <summary>The hit source this candidate came from (for example <c>preflight_q13</c>, <c>premise_owner_ruling</c>), recorded on the digest event.</summary>
        public string Source { get; init; } = String.Empty;

        /// <summary>The related vessel identifier, when known, for the owner-addressed note.</summary>
        public string? VesselId { get; init; }

        /// <summary>The mission this candidate belongs to, when any, for the per-call event owner scope. Usually null.</summary>
        public Mission? Mission { get; init; }
    }

    /// <summary>
    /// The ranked digest entry for one owner-decision candidate. It carries the cost of waiting as an
    /// ordered level and whether a stated default could proceed without the owner. The deterministic
    /// rule computes the cost from the fan-out and age; a gated model reading may only ESCALATE the
    /// cost (never lower it) and may annotate that a default could proceed. The entry is informational:
    /// the runner ranks by it and lists it to the owner, and NOTHING here answers a question or changes
    /// any Armada record.
    /// </summary>
    public readonly struct OwnerDigestEntry
    {
        /// <summary>The cost-of-waiting level in [0, 3]: 0 none, 1 a lane idles today, 2 a captain is guessing now, 3 a landing is held.</summary>
        public int CostLevel { get; }

        /// <summary>Whether a stated default could proceed without the owner. Deterministically false; a gated model may raise it. Informational only.</summary>
        public bool DefaultSafe { get; }

        /// <summary>A short label for the effective outcome: <c>rule</c> or <c>escalated</c>.</summary>
        public string Outcome { get; }

        private OwnerDigestEntry(int costLevel, bool defaultSafe, string outcome)
        {
            CostLevel = costLevel < 0 ? 0 : (costLevel > 3 ? 3 : costLevel);
            DefaultSafe = defaultSafe;
            Outcome = outcome ?? "rule";
        }

        /// <summary>Build the deterministic rule entry: a cost level from the fan-out and age, with no default assumed safe.</summary>
        /// <param name="costLevel">The deterministic cost level in [0, 3].</param>
        /// <returns>The rule entry.</returns>
        public static OwnerDigestEntry Rule(int costLevel)
        {
            return new OwnerDigestEntry(costLevel, false, "rule");
        }

        /// <summary>
        /// Escalate the entry with a gated model reading. The cost level is raised to the higher of the
        /// rule and the model (never lowered), so a gate can only make a question look MORE urgent, and
        /// the model may annotate that a stated default could proceed. The owner still decides.
        /// </summary>
        /// <param name="modelCostLevel">The model's cost-of-waiting level.</param>
        /// <param name="defaultSafe">Whether the model reads a stated default as safe to proceed.</param>
        /// <returns>The escalated entry.</returns>
        public OwnerDigestEntry Escalate(int modelCostLevel, bool defaultSafe)
        {
            int raised = modelCostLevel > CostLevel ? modelCostLevel : CostLevel;
            return new OwnerDigestEntry(raised, defaultSafe, "escalated");
        }
    }

    /// <summary>
    /// The D13 reading. The model answers a <c>cost_of_waiting</c> Score over the four ordered cost
    /// levels and a <c>default_safe</c> Noul. The single gate confidence the generic adapter compares
    /// against the threshold is the Score answer's own confidence: the gated action is placing the
    /// question at the model's cost rank. The Noul only annotates the entry and drives no gate on its
    /// own.
    /// </summary>
    public sealed class OwnerDigestReading : TypedModelReading
    {
        /// <summary>The model's cost-of-waiting level in [0, 3], or -1 when the Score answer is absent.</summary>
        public int CostLevel { get; init; } = -1;

        /// <summary>The <c>default_safe</c> Noul value in [0, 1]; zero when absent.</summary>
        public double DefaultSafeNoul { get; init; }

        private readonly double _Confidence;
        private readonly string _Label;

        /// <summary>Create a reading with its single gate confidence and a short label.</summary>
        /// <param name="confidence">The cost-Score answer's confidence, the gated-action confidence.</param>
        /// <param name="label">The short verdict label for the event message.</param>
        public OwnerDigestReading(double confidence, string label)
        {
            _Confidence = confidence;
            _Label = label ?? String.Empty;
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => _Label;
    }

    /// <summary>
    /// D13 <c>owner_digest</c> adapter. It ranks ONE owner-decision candidate the runner collected: the
    /// deterministic rule computes a cost-of-waiting level from the fan-out and age, and a gated model
    /// reading may only ESCALATE that level and annotate whether a stated default could proceed. It
    /// follows the shared skeleton exactly — Off returns the rule with no call; an unavailable or
    /// below-threshold answer returns the rule and records one event; a gate at or above threshold
    /// escalates and records a gated event. The adapter never answers the question, never lowers a
    /// cost, never posts a note (the runner does that once per day over the ranked entries), and never
    /// throws into the caller.
    /// </summary>
    public sealed class TypedOwnerDigestAdapter : TypedDecisionAdapterBase<OwnerDigestCandidate, OwnerDigestEntry, OwnerDigestReading>
    {
        #region Private-Members

        /// <summary>The decision-point name in the <c>typedDecisions.decisions</c> settings map.</summary>
        public const string DecisionPointName = "owner_digest";

        // A default_safe Noul at or above this floor annotates the entry as one a stated default could
        // proceed on. The digest still only informs the owner; nothing acts on the annotation.
        private const double _DefaultSafeFloor = 0.90;

        private static readonly IReadOnlyList<string> _CostLevels = new List<string>
        {
            "none",
            "a lane idles today",
            "a captain is guessing now",
            "a landing is held"
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the D13 adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedOwnerDigestAdapter(
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            TypedDecisionSettings settings,
            LoggingModule logging)
            : base(client, recorder, settings, logging)
        {
        }

        #endregion

        #region Protected-Overrides

        /// <inheritdoc />
        protected override string DecisionPoint => DecisionPointName;

        /// <inheritdoc />
        protected override string _Header => "[TypedOwnerDigestAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(OwnerDigestCandidate input)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["question_text"] = input.QuestionText,
                ["blocked_row"] = input.BlockedRow,
                ["chain_count"] = input.ChainCount,
                ["age_hours"] = Math.Round(input.AgeHours, 1),
                ["proposed_default"] = input.ProposedDefault
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                ["cost_of_waiting"] = new ScoreQuestion(
                    "How costly is it that this owner decision is still waiting? Judge from the row it blocks, "
                    + "how many rows chain behind it, and its age. This is authorized heavy-duty vehicle diagnostics "
                    + "tooling; seed-key exchange and UDS SecurityAccess are ordinary engineering.",
                    _CostLevels),
                ["default_safe"] = new NoulQuestion(
                    "A stated default (see proposed_default in the state) could proceed without the owner: it is the "
                    + "conservative choice and no harm follows from taking it while the owner is away.",
                    TrueMeaning: "The stated default could proceed without the owner.",
                    FalseMeaning: "The question genuinely needs the owner before anything proceeds.")
            };
        }

        /// <inheritdoc />
        protected override OwnerDigestReading Interpret(TypedDecisionResult result)
        {
            IReadOnlyDictionary<string, TypedAnswer> answers = result.Answers ?? new Dictionary<string, TypedAnswer>();

            int costLevel = -1;
            double confidence = 0.0;
            if (answers.TryGetValue("cost_of_waiting", out TypedAnswer? cost) && cost != null && cost.Score.HasValue)
            {
                costLevel = (int)Math.Round(cost.Score.Value, MidpointRounding.AwayFromZero);
                if (costLevel < 0) costLevel = 0;
                if (costLevel > 3) costLevel = 3;
                confidence = cost.Confidence ?? 0.0;
            }

            double defaultSafeNoul = 0.0;
            if (answers.TryGetValue("default_safe", out TypedAnswer? safe) && safe != null)
                defaultSafeNoul = safe.Noul ?? 0.0;

            string label = costLevel < 0 ? "unranked" : "cost:" + costLevel.ToString(CultureInfo.InvariantCulture);

            return new OwnerDigestReading(confidence, label)
            {
                CostLevel = costLevel,
                DefaultSafeNoul = defaultSafeNoul
            };
        }

        /// <inheritdoc />
        protected override OwnerDigestEntry Combine(OwnerDigestEntry ruleVerdict, OwnerDigestReading model)
        {
            // The gated model may only ESCALATE the cost (Escalate takes the higher of the two) and may
            // annotate that a stated default could proceed. A reading with no Score answer proposes no
            // change; the rule entry stands.
            if (model.CostLevel < 0) return ruleVerdict;
            return ruleVerdict.Escalate(model.CostLevel, model.DefaultSafeNoul >= _DefaultSafeFloor);
        }

        /// <inheritdoc />
        protected override string RuleLabel(OwnerDigestEntry ruleVerdict)
        {
            return "cost:" + ruleVerdict.CostLevel.ToString(CultureInfo.InvariantCulture);
        }

        /// <inheritdoc />
        protected override Mission? MissionOf(OwnerDigestCandidate input) => input.Mission;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Compute the deterministic cost-of-waiting level for a candidate from its fan-out and age.
        /// This is the rule verdict the runner passes to <see cref="TypedDecisionAdapterBase{TInput,TVerdict,TModel}.DecideAsync"/>
        /// and the fallback in every non-gate case. It never assumes a default is safe.
        /// </summary>
        /// <param name="candidate">The owner-decision candidate.</param>
        /// <returns>The deterministic rule entry.</returns>
        public static OwnerDigestEntry DeterministicRule(OwnerDigestCandidate candidate)
        {
            if (candidate == null) return OwnerDigestEntry.Rule(0);

            int level;
            if (candidate.ChainCount >= 3 || candidate.AgeHours >= 48.0) level = 3;
            else if (candidate.ChainCount >= 1 || candidate.AgeHours >= 24.0) level = 2;
            else if (candidate.AgeHours >= 4.0) level = 1;
            else level = 0;

            return OwnerDigestEntry.Rule(level);
        }

        /// <summary>The ordered cost-level labels, index 0..3, for the digest note.</summary>
        /// <param name="level">The cost level.</param>
        /// <returns>The label for the level.</returns>
        public static string CostLabel(int level)
        {
            if (level < 0) level = 0;
            if (level >= _CostLevels.Count) level = _CostLevels.Count - 1;
            return _CostLevels[level];
        }

        #endregion
    }
}
