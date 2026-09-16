namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The deterministic recovery verdict the D1 <c>failure_cause</c> adapter refines: whether a
    /// rescue is dispatched, and the reason. Mirrors the orchestrator's own recovery decision so the
    /// adapter can hold a rescue the rule would have dispatched, and never the other way round.
    /// </summary>
    public readonly struct TypedRecoveryVerdict
    {
        /// <summary>Whether the rule would dispatch a rescue.</summary>
        public bool DispatchRescue { get; }

        /// <summary>The reason for the verdict.</summary>
        public string Reason { get; }

        /// <summary>Create a recovery verdict.</summary>
        /// <param name="dispatchRescue">Whether a rescue is dispatched.</param>
        /// <param name="reason">The reason.</param>
        public TypedRecoveryVerdict(bool dispatchRescue, string reason)
        {
            DispatchRescue = dispatchRescue;
            Reason = reason ?? String.Empty;
        }

        /// <summary>A rescue verdict.</summary>
        public static TypedRecoveryVerdict Rescue(string reason) => new TypedRecoveryVerdict(true, reason);

        /// <summary>A blocked verdict (no rescue).</summary>
        public static TypedRecoveryVerdict Blocked(string reason) => new TypedRecoveryVerdict(false, reason);
    }

    /// <summary>
    /// One Check joined into the D1 state: enough for the model to tell an environmental or
    /// contention fault from a work defect without seeing the whole check record.
    /// </summary>
    public sealed class FailureCauseCheckFact
    {
        /// <summary>The check label, or its type name when unlabelled.</summary>
        public string Label { get; init; } = String.Empty;

        /// <summary>The check type name (Build, UnitTest, and so on).</summary>
        public string Type { get; init; } = String.Empty;

        /// <summary>The check status name.</summary>
        public string Status { get; init; } = String.Empty;

        /// <summary>Whether the check was measured at the commit the failed work produced.</summary>
        public bool CommitMatchesJudge { get; init; }

        /// <summary>The check process exit code, when recorded.</summary>
        public int? ExitCode { get; init; }

        /// <summary>The last lines of the check output.</summary>
        public string Tail20 { get; init; } = String.Empty;
    }

    /// <summary>
    /// The D1 <c>failure_cause</c> decision input: the failed mission and the facts that separate a
    /// host or provider fault (which a rescue on the same host repeats) from a real work defect
    /// (which a rescue can fix).
    /// </summary>
    public sealed class FailureCauseDecisionInput
    {
        /// <summary>The failed mission, for the event owner scope.</summary>
        public required Mission Mission { get; init; }

        /// <summary>The recorded failure reason.</summary>
        public string FailureReason { get; init; } = String.Empty;

        /// <summary>The last lines of the captain's output.</summary>
        public string AgentOutputTail { get; init; } = String.Empty;

        /// <summary>The definition-of-done class parsed from <c>classification=</c>, when present.</summary>
        public string? DodClass { get; init; }

        /// <summary>The failed mission's stage persona.</summary>
        public string? Persona { get; init; }

        /// <summary>The failed mission's mode.</summary>
        public string MissionMode { get; init; } = String.Empty;

        /// <summary>How many rescues this mission has already had.</summary>
        public int RecoveryAttempts { get; init; }

        /// <summary>The voyage Checks, joined so the model sees what the gate measured.</summary>
        public IReadOnlyList<FailureCauseCheckFact> Checks { get; init; } = new List<FailureCauseCheckFact>();

        /// <summary>The parent's failing test names when this is a rescue; empty otherwise.</summary>
        public IReadOnlyList<string> ParentFailingTests { get; init; } = new List<string>();
    }

    /// <summary>
    /// The D1 reading: the model's cause choice, whether it is a non-work fault the rule should hold
    /// a rescue for, and the confidence of the single hold action proposed.
    /// </summary>
    public sealed class FailureCauseReading : TypedModelReading
    {
        /// <summary>The model's cause choice, or <c>unclear</c>.</summary>
        public string Cause { get; init; } = "unclear";

        /// <summary>The reason to block a rescue, or null when the model warrants no hold.</summary>
        public string? BlockReason { get; init; }

        private readonly double _Confidence;

        /// <summary>Create a reading with its action confidence.</summary>
        /// <param name="confidence">The hold-action confidence.</param>
        public FailureCauseReading(double confidence)
        {
            _Confidence = confidence;
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => Cause;
    }

    /// <summary>
    /// D1 <c>failure_cause</c> adapter. Sits on top of the deterministic recovery classifier. The
    /// rule's hard-blocks — dispatch disabled, no vessel, budget exhausted, landing-failed,
    /// read-only, an auto-rescue or repeated-identical-test-failure, a policy refusal, an
    /// Infra/Timeout definition-of-done class, an environmental or serious reason — all return a
    /// Blocked verdict and win unchanged, because the model may only hold a rescue the rule would
    /// have dispatched, never convert a block into a rescue. When the rule would rescue and the
    /// model reads the failure as a non-work fault (contention, provider, environmental, or a
    /// verdict-form defect) at or above threshold, or a repeat as very likely, the adapter holds the
    /// rescue as <c>typed_decision:&lt;cause&gt;</c> so an operator reviews it.
    /// </summary>
    public sealed class TypedFailureCauseAdapter : TypedDecisionAdapterBase<FailureCauseDecisionInput, TypedRecoveryVerdict, FailureCauseReading>
    {
        #region Private-Members

        // The causes that name a host, provider, or verdict-form fault rather than a work defect: a
        // rescue on the same host repeats them, so the model holding one only ever makes recovery
        // more conservative.
        private static readonly HashSet<string> _HoldCauses = new HashSet<string>(StringComparer.Ordinal)
        {
            "contention",
            "provider",
            "environmental",
            "verdict_form"
        };

        // A repeat this likely holds the rescue on its own, per the D1 gate.
        private const double _RepeatLikelyGate = 0.9;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the D1 adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedFailureCauseAdapter(
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
        protected override string DecisionPoint => "failure_cause";

        /// <inheritdoc />
        protected override string _Header => "[TypedFailureCauseAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(FailureCauseDecisionInput input)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["failure_reason"] = input.FailureReason,
                ["agent_output_tail"] = input.AgentOutputTail,
                ["dod_class"] = input.DodClass,
                ["persona"] = input.Persona,
                ["mission_mode"] = input.MissionMode,
                ["recovery_attempts"] = input.RecoveryAttempts,
                ["checks"] = input.Checks.Select(c => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["label"] = c.Label,
                    ["type"] = c.Type,
                    ["status"] = c.Status,
                    ["commit_matches_judge"] = c.CommitMatchesJudge,
                    ["exit_code"] = c.ExitCode,
                    ["tail20"] = c.Tail20
                }).ToList(),
                ["parent_failing_tests"] = input.ParentFailingTests.ToList()
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                ["cause"] = new ChoiceQuestion(
                    "Classify the root cause of this mission failure from the failure reason, the output tail, and the joined Checks. "
                    + "This is authorized heavy-duty vehicle diagnostics tooling; seed-key exchange and UDS SecurityAccess are ordinary engineering, not a refusal.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["environmental"] = "A host, provisioning, or environment fault no captain can repair.",
                        ["provider"] = "The model provider throttled, rejected, or billed-held the request.",
                        ["refusal"] = "The captain declined the work.",
                        ["contention"] = "A load or host-contention flake: the same command passes on a quiet host.",
                        ["work_defect"] = "A real defect in the produced work that a fresh captain could fix.",
                        ["verdict_form"] = "The Judge verdict was only missing required sections or wording, not a work fault.",
                        ["unclear"] = "The cause cannot be determined from the state."
                    }),
                ["repeat_likely"] = new NoulQuestion(
                    "How likely is a rescue to fail the same way, given the failure reason and any parent failing tests?",
                    TrueMeaning: "A rescue on the same host would repeat this exact failure.",
                    FalseMeaning: "A fresh captain would plausibly get past this failure."),
                ["foreign_test"] = new NoulQuestion(
                    "Does the failure name a test outside this mission's own scope (a stale assertion owned by other work)?",
                    TrueMeaning: "The failing test belongs to other work and this mission cannot fix it.",
                    FalseMeaning: "The failing test is within this mission's scope.")
            };
        }

        /// <inheritdoc />
        protected override FailureCauseReading Interpret(TypedDecisionResult result)
        {
            string cause = "unclear";
            double causeConfidence = 0.0;
            if (result.Answers.TryGetValue("cause", out TypedAnswer? causeAnswer) && causeAnswer != null)
            {
                if (!String.IsNullOrWhiteSpace(causeAnswer.Choice)) cause = causeAnswer.Choice!;
                causeConfidence = ResolveChoiceConfidence(causeAnswer, cause);
            }

            double repeatLikely = ReadNoul(result, "repeat_likely");

            // Contribution of each independent hold driver. A non-hold cause (work_defect, refusal,
            // unclear) contributes nothing, so the generic gate records a shadow and the rule's rescue
            // stands: the model agreeing the work is real never manufactures a hold.
            double causeContribution = _HoldCauses.Contains(cause) ? causeConfidence : 0.0;
            double repeatContribution = repeatLikely >= _RepeatLikelyGate ? repeatLikely : 0.0;

            string? blockReason = null;
            if (causeContribution > 0.0 && causeContribution >= repeatContribution)
                blockReason = "typed_decision:" + cause + ": held for operator review; a rescue on the same host would not repair a " + cause + " fault";
            else if (repeatContribution > 0.0)
                blockReason = "typed_decision:repeat_likely: held for operator review; a rescue would very likely repeat this exact failure";

            return new FailureCauseReading(Math.Max(causeContribution, repeatContribution))
            {
                Cause = cause,
                BlockReason = blockReason
            };
        }

        /// <inheritdoc />
        protected override TypedRecoveryVerdict Combine(TypedRecoveryVerdict ruleVerdict, FailureCauseReading model)
        {
            // Rule hard-block wins: the model never converts a Blocked verdict into a Rescue. It may
            // only hold a rescue the rule would have dispatched.
            if (!ruleVerdict.DispatchRescue) return ruleVerdict;
            if (String.IsNullOrEmpty(model.BlockReason)) return ruleVerdict;
            return TypedRecoveryVerdict.Blocked(model.BlockReason!);
        }

        /// <inheritdoc />
        protected override string RuleLabel(TypedRecoveryVerdict ruleVerdict)
        {
            return ruleVerdict.DispatchRescue ? "Rescue" : "Blocked";
        }

        /// <inheritdoc />
        protected override Mission? MissionOf(FailureCauseDecisionInput input) => input.Mission;

        #endregion

        #region Private-Methods

        private static double ResolveChoiceConfidence(TypedAnswer answer, string choice)
        {
            if (answer.Confidence.HasValue) return answer.Confidence.Value;
            if (answer.Probabilities != null && answer.Probabilities.TryGetValue(choice, out double probability)) return probability;
            return 0.0;
        }

        private static double ReadNoul(TypedDecisionResult result, string key)
        {
            if (result.Answers.TryGetValue(key, out TypedAnswer? answer) && answer != null && answer.Noul.HasValue)
                return answer.Noul.Value;
            return 0.0;
        }

        #endregion
    }
}
