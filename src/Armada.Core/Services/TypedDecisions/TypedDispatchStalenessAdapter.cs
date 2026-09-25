namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>The dispatch_staleness input: the configured policy, the relevance reading, and a head of the work text.</summary>
    public sealed class DispatchStalenessInput
    {
        /// <summary>The mission being dispatched, for the event owner scope. Null when dispatch has no mission yet.</summary>
        public Mission? Mission { get; init; }

        /// <summary>The vessel being dispatched to, for the egress vessel rule.</summary>
        public string? VesselId { get; init; }

        /// <summary>The objective being dispatched, when known, recorded on the decision event; never part of the state.</summary>
        public string? ObjectiveId { get; init; }

        /// <summary>The configured codeIndex.dispatchStalenessPolicy.</summary>
        public CodeIndexDispatchStalenessPolicyEnum Policy { get; init; } = CodeIndexDispatchStalenessPolicyEnum.Proceed;

        /// <summary>Whether a refresh is already running.</summary>
        public bool UpdateInProgress { get; init; }

        /// <summary>The deterministic relevance reading. Never null after construction.</summary>
        public CodeIndexStalenessRelevance Relevance { get; init; } = new CodeIndexStalenessRelevance();

        /// <summary>Voyage or objective title.</summary>
        public string Title { get; init; } = String.Empty;

        /// <summary>Voyage or objective description; the adapter sends only its head.</summary>
        public string Description { get; init; } = String.Empty;
    }

    /// <summary>The dispatch_staleness reading: the chosen reaction, or null when the answer named no option.</summary>
    public sealed class DispatchStalenessReading : TypedModelReading
    {
        private readonly double _Confidence;
        private readonly string _Label;

        /// <summary>Create a reading.</summary>
        /// <param name="choice">The chosen reaction, or null when the answer named no option.</param>
        /// <param name="confidence">The confidence of the choice; zero when no option was named.</param>
        public DispatchStalenessReading(CodeIndexDispatchStalenessPolicyEnum? choice, double confidence)
        {
            Choice = choice;
            _Confidence = choice.HasValue ? confidence : 0.0;
            _Label = choice.HasValue ? TypedDispatchStalenessAdapter.OptionName(choice.Value) : "no_option";
        }

        /// <summary>The chosen reaction, or null.</summary>
        public CodeIndexDispatchStalenessPolicyEnum? Choice { get; }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => _Label;
    }

    /// <summary>
    /// <c>dispatch_staleness</c> adapter. At voyage dispatch, when a vessel's code index is stale on
    /// indexable source, it asks one closed choice: proceed against the current index, refresh the
    /// changed source now, or wait until the index is fresh. The deterministic policy is the rule
    /// verdict. Combine never introduces a wait the rule would not, and a Block rule always wins.
    /// Off, unavailable, and below threshold keep the rule. Ships in Gate at 0.90.
    /// </summary>
    public sealed class TypedDispatchStalenessAdapter : TypedDecisionAdapterBase<DispatchStalenessInput, CodeIndexDispatchStalenessPolicyEnum, DispatchStalenessReading>
    {
        #region Public-Members

        /// <summary>The decision name: its settings key and event area.</summary>
        public const string DecisionName = "dispatch_staleness";

        /// <summary>The question id.</summary>
        public const string QuestionId = "reaction";

        #endregion

        #region Private-Members

        private const int _DescriptionHeadChars = 2000;

        private static readonly IReadOnlyDictionary<string, string> _Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["proceed"] = "Dispatch now against the current index. The changed source is not load-bearing for this work, or a background refresh is enough.",
            ["refresh_inline"] = "Refresh the changed source now, then dispatch. The work will search or quote that source in this voyage.",
            ["block"] = "Wait until the index is fresh. Context packs and search must match the newest commit before any captain starts."
        };

        private readonly TypedDecisionSettings _Settings;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedDispatchStalenessAdapter(
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            TypedDecisionSettings settings,
            LoggingModule logging)
            : base(client, recorder, settings, logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        #endregion

        #region Public-Methods

        /// <summary>The option name the question uses for a policy.</summary>
        /// <param name="policy">The policy.</param>
        /// <returns><c>proceed</c>, <c>refresh_inline</c>, or <c>block</c>.</returns>
        public static string OptionName(CodeIndexDispatchStalenessPolicyEnum policy)
        {
            return policy == CodeIndexDispatchStalenessPolicyEnum.Block ? "block"
                : policy == CodeIndexDispatchStalenessPolicyEnum.RefreshInline ? "refresh_inline"
                : "proceed";
        }

        /// <summary>Parse an option name into a policy, or null when the name is unknown.</summary>
        /// <param name="option">The option name.</param>
        /// <returns>The policy, or null.</returns>
        public static CodeIndexDispatchStalenessPolicyEnum? ParseOption(string? option)
        {
            if (String.IsNullOrWhiteSpace(option)) return null;
            string value = option.Trim().ToLowerInvariant();
            if (value == "block") return CodeIndexDispatchStalenessPolicyEnum.Block;
            if (value == "refresh_inline") return CodeIndexDispatchStalenessPolicyEnum.RefreshInline;
            if (value == "proceed") return CodeIndexDispatchStalenessPolicyEnum.Proceed;
            return null;
        }

        #endregion

        #region Protected-Overrides

        /// <inheritdoc />
        protected override string DecisionPoint => DecisionName;

        /// <inheritdoc />
        protected override string _Header => "[TypedDispatchStalenessAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(DispatchStalenessInput input)
        {
            int headChars = Math.Min(_DescriptionHeadChars, Math.Max(200, _Settings.MaxStateChars / 2));
            string description = input.Description ?? String.Empty;
            if (description.Length > headChars) description = description.Substring(0, headChars);
            CodeIndexStalenessRelevance relevance = input.Relevance ?? new CodeIndexStalenessRelevance();
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["policy"] = OptionName(input.Policy),
                ["update_in_progress"] = input.UpdateInProgress,
                ["is_stale"] = relevance.IsStale,
                ["is_relevant"] = relevance.IsRelevant,
                ["diff_unavailable"] = relevance.DiffUnavailable,
                ["changed_file_count"] = relevance.ChangedFileCount,
                ["changed_source_file_count"] = relevance.ChangedSourceFileCount,
                ["title"] = input.Title ?? String.Empty,
                ["description_head"] = description
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                [QuestionId] = new ChoiceQuestion(
                    "The vessel's code index is behind the current commit on indexable source. Which dispatch reaction keeps search and context packs honest for this work without waiting more than needed? Choose proceed to dispatch against the current index, refresh_inline to refresh the changed source now then dispatch, or block to wait until the index is fresh.",
                    _Criteria)
            };
        }

        /// <inheritdoc />
        protected override DispatchStalenessReading Interpret(TypedDecisionResult result)
        {
            if (!result.Answers.TryGetValue(QuestionId, out TypedAnswer? answer) || answer == null)
                return new DispatchStalenessReading(null, 0.0);
            CodeIndexDispatchStalenessPolicyEnum? choice = ParseOption(answer.Choice);
            double confidence = answer.Confidence
                ?? (answer.Probabilities != null && answer.Choice != null && answer.Probabilities.TryGetValue(answer.Choice, out double probability) ? probability : 0.0);
            return new DispatchStalenessReading(choice, confidence);
        }

        /// <inheritdoc />
        protected override CodeIndexDispatchStalenessPolicyEnum Combine(CodeIndexDispatchStalenessPolicyEnum ruleVerdict, DispatchStalenessReading model)
        {
            return DispatchStalenessRules.Combine(ruleVerdict, model.Choice);
        }

        /// <inheritdoc />
        protected override string RuleLabel(CodeIndexDispatchStalenessPolicyEnum ruleVerdict) => OptionName(ruleVerdict);

        /// <inheritdoc />
        protected override Mission? MissionOf(DispatchStalenessInput input) => input.Mission;

        /// <inheritdoc />
        protected override string? ObjectiveIdOf(DispatchStalenessInput input) => input.ObjectiveId;

        /// <inheritdoc />
        protected override IEnumerable<string?> VesselIdsOf(DispatchStalenessInput input)
            => new[] { input.VesselId, input.Mission?.VesselId };

        #endregion
    }
}
