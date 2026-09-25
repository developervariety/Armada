namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Context;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// <c>memory_relevance</c> adapter. Retrieval ranks memory leaves by word overlap with the mission, so
    /// a brief carries leaves that share vocabulary with the task but not its subject. This adapter asks,
    /// one leaf at a time, whether the leaf applies to the work the mission describes, and moves the
    /// leaves the model is confident do not apply to the brief's reference files.
    ///
    /// It only SORTS: a moved leaf is still delivered in full and listed, after the read-first files.
    /// Core rules and safety leaves are never asked about. A missing answer reads as "applies", so the
    /// fail-closed direction keeps a leaf read-first. The adapter never throws into the caller.
    /// </summary>
    public sealed class TypedMemoryRelevanceAdapter : TypedDecisionAdapterBase<MemoryRelevanceDecisionInput, MemoryRelevanceVerdict, MemoryRelevanceReading>
    {
        #region Private-Members

        // The most leaves the model is asked about. Excess leaves stay read-first, the conservative reading.
        private const int _MaxLeaves = 40;

        // The leaf excerpt sent with each question, in characters. The summary and read-when line carry
        // the leaf's subject; the excerpt shows what the rule actually says.
        private const int _MaxExcerptChars = 900;

        // The mission description sent as the work to compare against, in characters.
        private const int _MaxDescriptionChars = 6000;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedMemoryRelevanceAdapter(
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            TypedDecisionSettings settings,
            LoggingModule logging)
            : base(client, recorder, settings, logging)
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Sorts a brief's ranked leaves into read-first and reference, for the brief's memory delivery.
        /// Returns the rule's sort (every leaf read-first) whenever the decision is off, unavailable, or
        /// below its threshold.
        /// </summary>
        /// <param name="mission">The mission being briefed.</param>
        /// <param name="leaves">The ranked leaves, in retrieval order.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The sort; never null.</returns>
        public async Task<ContextLeafSort> SortAsync(Mission mission, List<ContextChunk> leaves, CancellationToken token)
        {
            ContextLeafSort sort = new ContextLeafSort();
            if (mission == null || leaves == null || leaves.Count == 0)
            {
                sort.Outcome = "no_leaves";
                return sort;
            }

            List<string> topics = new List<string>();
            foreach (ContextChunk leaf in leaves) topics.Add(leaf?.Topic ?? String.Empty);

            MemoryRelevanceVerdict verdict = await DecideAsync(
                new MemoryRelevanceDecisionInput { Mission = mission, Leaves = leaves },
                MemoryRelevanceVerdict.AllReadFirst(topics),
                token).ConfigureAwait(false);

            foreach (string topic in verdict.ReferenceTopics)
                if (!String.IsNullOrEmpty(topic)) sort.ReferenceTopics.Add(topic);
            sort.Outcome = verdict.OutcomeLabel;
            return sort;
        }

        #endregion

        #region Protected-Overrides

        /// <inheritdoc />
        protected override string DecisionPoint => "memory_relevance";

        /// <inheritdoc />
        protected override string _Header => "[TypedMemoryRelevanceAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(MemoryRelevanceDecisionInput input)
        {
            List<object> leaves = new List<object>();
            int count = 0;
            foreach (ContextChunk leaf in input.Leaves)
            {
                if (count >= _MaxLeaves) break;
                ContextChunk c = leaf ?? new ContextChunk();
                leaves.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["topic"] = c.Topic,
                    ["summary"] = c.Summary,
                    ["read_when"] = c.ReadWhen,
                    ["excerpt"] = Bound(c.Text, _MaxExcerptChars)
                });
                count++;
            }

            Mission mission = input.Mission;
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mission"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["title"] = mission.Title,
                    ["persona"] = mission.Persona,
                    ["description"] = Bound(mission.Description, _MaxDescriptionChars)
                },
                ["leaves"] = leaves
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            return BuildSlotQuestions(_MaxLeaves);
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions(MemoryRelevanceDecisionInput input)
        {
            // One question per listed leaf, named at its JSON path; code tallies.
            int count = Math.Min(_MaxLeaves, input?.Leaves?.Count ?? 0);
            return BuildSlotQuestions(count);
        }

        /// <inheritdoc />
        protected override MemoryRelevanceReading Interpret(TypedDecisionResult result)
        {
            double threshold = Settings.For(DecisionPoint).GateThreshold;
            List<int> referenceIndexes = new List<int>();
            double strongest = 0.0;

            for (int i = 1; i <= _MaxLeaves; i++)
            {
                string id = "applies_" + i.ToString(CultureInfo.InvariantCulture);
                if (!result.Answers.ContainsKey(id)) continue;

                // A missing or unreadable answer reads as "applies": the leaf stays read-first.
                double notApplies = 1.0 - TypedAnswerReader.ReadNoul(result, id, 1.0);
                if (notApplies > strongest) strongest = notApplies;
                if (notApplies >= threshold) referenceIndexes.Add(i - 1);
            }

            string label = referenceIndexes.Count == 0
                ? "all_apply"
                : "reference:" + referenceIndexes.Count.ToString(CultureInfo.InvariantCulture);
            return new MemoryRelevanceReading(strongest, referenceIndexes, label);
        }

        /// <inheritdoc />
        protected override MemoryRelevanceVerdict Combine(MemoryRelevanceVerdict ruleVerdict, MemoryRelevanceReading model)
        {
            // Combine runs only at or above threshold. It moves leaves to reference and never removes one.
            if (model.ReferenceIndexes.Count == 0) return ruleVerdict;

            HashSet<string> reference = new HashSet<string>(StringComparer.Ordinal);
            foreach (int index in model.ReferenceIndexes)
            {
                if (index < 0 || index >= ruleVerdict.Topics.Count) continue;
                string topic = ruleVerdict.Topics[index];
                if (!String.IsNullOrEmpty(topic)) reference.Add(topic);
            }

            return reference.Count == 0 ? ruleVerdict : MemoryRelevanceVerdict.WithReference(ruleVerdict.Topics, reference);
        }

        /// <inheritdoc />
        protected override string RuleLabel(MemoryRelevanceVerdict ruleVerdict) => ruleVerdict.OutcomeLabel;

        /// <inheritdoc />
        protected override Mission? MissionOf(MemoryRelevanceDecisionInput input) => input.Mission;

        #endregion

        #region Private-Methods

        private static IReadOnlyDictionary<string, TypedQuestion> BuildSlotQuestions(int count)
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            for (int i = 1; i <= count; i++)
            {
                string path = "`leaves[" + (i - 1).ToString(CultureInfo.InvariantCulture) + "]`";
                questions["applies_" + i.ToString(CultureInfo.InvariantCulture)] = new NoulQuestion(
                    path + " is a rule, procedure, or lesson that applies to the work `mission` describes, "
                        + "so the captain doing that work should read it before starting.",
                    TrueMeaning: "The memory applies to this mission's work.",
                    FalseMeaning: "The memory is about other work; this mission needs it only if the work turns out to touch its subject.");
            }

            return questions;
        }

        private static string Bound(string? text, int maxChars)
        {
            string value = text ?? String.Empty;
            return value.Length <= maxChars ? value : value.Substring(0, maxChars);
        }

        #endregion
    }
}
