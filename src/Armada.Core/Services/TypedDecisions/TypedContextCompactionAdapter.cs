namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// One earlier tool result the <c>context_compaction</c> decision may spare: what the captain asked
    /// for, how large the answer was, and a bounded head of that answer. The excerpt is what lets the
    /// model tell a settled lookup from a load-bearing measurement; the byte count alone cannot.
    /// </summary>
    public sealed class ContextCompactionCandidate
    {
        /// <summary>Create a candidate descriptor.</summary>
        /// <param name="toolName">The tool whose result this is.</param>
        /// <param name="requestSummary">A bounded summary of what the captain asked the tool for.</param>
        /// <param name="resultHead">A bounded head of the tool's output.</param>
        /// <param name="resultBytes">The full size of the tool's output, in bytes.</param>
        /// <param name="turnsAgo">How many messages back in the conversation this result sits.</param>
        public ContextCompactionCandidate(string toolName, string requestSummary, string resultHead, int resultBytes, int turnsAgo)
        {
            ToolName = toolName ?? String.Empty;
            RequestSummary = requestSummary ?? String.Empty;
            ResultHead = resultHead ?? String.Empty;
            ResultBytes = resultBytes < 0 ? 0 : resultBytes;
            TurnsAgo = turnsAgo < 0 ? 0 : turnsAgo;
        }

        /// <summary>The tool whose result this is.</summary>
        public string ToolName { get; }

        /// <summary>A bounded summary of what the captain asked the tool for.</summary>
        public string RequestSummary { get; }

        /// <summary>A bounded head of the tool's output.</summary>
        public string ResultHead { get; }

        /// <summary>The full size of the tool's output, in bytes.</summary>
        public int ResultBytes { get; }

        /// <summary>How many messages back in the conversation this result sits.</summary>
        public int TurnsAgo { get; }
    }

    /// <summary>
    /// The <c>context_compaction</c> verdict: which candidates keep their full output this pass. The
    /// rule verdict spares NOTHING (<see cref="SpareNone"/>), which is the deterministic compaction
    /// Armada already performs — it replaces the content of every older tool result. So a gated verdict
    /// can only ever RETAIN more than the rule, never less, and a wrong reading costs context bytes
    /// rather than losing a captain's work.
    /// </summary>
    public readonly struct ContextCompactionVerdict
    {
        /// <summary>
        /// The candidate positions, zero-based in the order supplied, that keep their full output. A caller
        /// resolves them against the candidate list IT supplied and ignores any position outside it: only
        /// the caller knows that list, so only the caller can bound it.
        /// </summary>
        public IReadOnlyList<int> SparedPositions { get; }

        private ContextCompactionVerdict(IReadOnlyList<int>? spared)
        {
            SparedPositions = spared ?? new List<int>();
        }

        /// <summary>Whether the verdict spares any candidate.</summary>
        public bool HasSpared => SparedPositions.Count > 0;

        /// <summary>A short label for the effective outcome.</summary>
        public string OutcomeLabel => HasSpared
            ? "spared:" + SparedPositions.Count.ToString(CultureInfo.InvariantCulture)
            : "compact_all";

        /// <summary>The rule verdict: compact every candidate, sparing none.</summary>
        /// <returns>An empty verdict.</returns>
        public static ContextCompactionVerdict SpareNone()
        {
            return new ContextCompactionVerdict(null);
        }

        /// <summary>Build a verdict sparing the named candidate positions.</summary>
        /// <param name="positions">Zero-based positions, in the order the candidates were supplied.</param>
        /// <returns>A verdict sparing those candidates.</returns>
        public static ContextCompactionVerdict Sparing(IReadOnlyList<int> positions)
        {
            return new ContextCompactionVerdict(positions);
        }
    }

    /// <summary>
    /// The <c>context_compaction</c> decision input: the goal the captain is working towards and the
    /// earlier tool results the deterministic pass is about to replace, oldest first.
    /// </summary>
    public sealed class ContextCompactionDecisionInput
    {
        /// <summary>The mission being run, for the event owner scope. Null for a caller outside a mission.</summary>
        public Mission? Mission { get; init; }

        /// <summary>What the captain is working towards; the mission's own instructions, bounded.</summary>
        public string Goal { get; init; } = String.Empty;

        /// <summary>The candidates the deterministic pass would compact, oldest first.</summary>
        public IReadOnlyList<ContextCompactionCandidate> Candidates { get; init; } = new List<ContextCompactionCandidate>();
    }

    /// <summary>
    /// The <c>context_compaction</c> reading. Per candidate the model answers one Noul: whether that
    /// earlier output is still load-bearing for the remaining work. The single gate confidence is the
    /// STRONGEST such reading, so one clearly load-bearing result is enough to consult the gate; the
    /// per-candidate floor then decides which candidates are actually spared.
    /// </summary>
    public sealed class ContextCompactionReading : TypedModelReading
    {
        private readonly double _Confidence;
        private readonly string _Label;

        /// <summary>The candidate positions the model reads as still load-bearing.</summary>
        public IReadOnlyList<int> SparePositions { get; }

        /// <summary>Create a reading.</summary>
        /// <param name="confidence">The strongest load-bearing reading across the candidates.</param>
        /// <param name="sparePositions">The candidate positions to spare.</param>
        /// <param name="label">The short verdict label for the event message.</param>
        public ContextCompactionReading(double confidence, IReadOnlyList<int> sparePositions, string label)
        {
            _Confidence = confidence;
            SparePositions = sparePositions ?? new List<int>();
            _Label = label ?? String.Empty;
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => _Label;
    }

    /// <summary>
    /// The <c>context_compaction</c> adapter. Armada's deterministic compaction replaces the content of
    /// EVERY older tool result once a conversation passes its threshold, oldest first, without reading
    /// any of them: the bytes are where the bytes are. That is correct and stays the fallback, but it
    /// discards a measurement the captain still needs as readily as a settled directory listing.
    ///
    /// This adapter reads the candidates the deterministic pass is about to replace and spares the ones
    /// whose full output is still load-bearing for the stated goal. The direction is fixed by the rule:
    /// the rule spares nothing, so the gated verdict can only RETAIN more, never less. A wrong reading
    /// therefore costs context bytes, and the caller's own ceiling still holds — a caller that is still
    /// over its limit after sparing compacts the spared candidates in deterministic order.
    ///
    /// The questions are MEANING judgments about one output's remaining usefulness, never a size or
    /// shape test; size and recency are already decided deterministically by the caller. The adapter
    /// never throws into the caller.
    /// </summary>
    public sealed class TypedContextCompactionAdapter : TypedDecisionAdapterBase<ContextCompactionDecisionInput, ContextCompactionVerdict, ContextCompactionReading>
    {
        #region Private-Members

        // The number of candidate slots the model is asked about. Candidates beyond this are compacted
        // by the rule with no question asked, which is the fallback behaviour and so always safe.
        private const int _MaxCandidates = 16;

        // At or above this reading a candidate is spared, once the gate has been consulted. The gate
        // threshold decides whether ANY candidate is spared; this floor decides which ones.
        private const double _SpareFloor = 0.5;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the context-compaction adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedContextCompactionAdapter(
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            TypedDecisionSettings settings,
            LoggingModule logging)
            : base(client, recorder, settings, logging)
        {
        }

        #endregion

        #region Public-Members

        /// <summary>The number of candidates the decision asks about; the caller compacts any beyond it.</summary>
        public static int MaxCandidates => _MaxCandidates;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Decide over the candidates whose content may leave the host. A candidate whose request or output
        /// names one of this decision's excluded markers is removed BEFORE anything is built or sent; it is
        /// never spared, so the caller compacts it by the deterministic rule, and a plugin still keeps it in
        /// the dock's archive, so the captain can read it back. The returned positions index the caller's
        /// ORIGINAL candidate list. Every caller goes through this, never through the skeleton directly.
        /// </summary>
        /// <param name="input">The decision input, with every candidate the caller would compact.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The verdict, in the caller's candidate positions.</returns>
        public async Task<ContextCompactionVerdict> DecideAllowedAsync(ContextCompactionDecisionInput input, CancellationToken token)
        {
            List<int> originalPositions = new List<int>();
            List<ContextCompactionCandidate> allowed = new List<ContextCompactionCandidate>();
            IReadOnlyList<ContextCompactionCandidate> candidates = input?.Candidates ?? new List<ContextCompactionCandidate>();
            for (int position = 0; position < candidates.Count; position++)
            {
                ContextCompactionCandidate candidate = candidates[position];
                if (candidate == null) continue;
                if (Settings.ExcludedMarkerIn(DecisionPoint, candidate.RequestSummary) != null) continue;
                if (Settings.ExcludedMarkerIn(DecisionPoint, candidate.ResultHead) != null) continue;
                originalPositions.Add(position);
                allowed.Add(candidate);
            }

            ContextCompactionVerdict verdict = await DecideAsync(
                new ContextCompactionDecisionInput { Mission = input?.Mission, Goal = input?.Goal ?? String.Empty, Candidates = allowed },
                ContextCompactionVerdict.SpareNone(),
                token).ConfigureAwait(false);

            List<int> spared = new List<int>();
            foreach (int position in verdict.SparedPositions)
                if (position >= 0 && position < originalPositions.Count) spared.Add(originalPositions[position]);
            return spared.Count == 0 ? ContextCompactionVerdict.SpareNone() : ContextCompactionVerdict.Sparing(spared);
        }

        #endregion

        #region Protected-Overrides

        /// <inheritdoc />
        protected override string DecisionPoint => "context_compaction";

        /// <summary>
        /// Never excluded as a whole: excluded content is removed per candidate in
        /// <see cref="DecideAllowedAsync"/>, so the rest of the candidates can still be asked about. The goal is
        /// the captain's brief, which names paths but does not carry their content.
        /// </summary>
        /// <param name="input">The decision input.</param>
        /// <returns>False.</returns>
        protected override bool CarriesExcludedContent(ContextCompactionDecisionInput input) => false;

        /// <inheritdoc />
        protected override string _Header => "[TypedContextCompactionAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(ContextCompactionDecisionInput input)
        {
            List<object> candidates = new List<object>();
            int count = 0;
            foreach (ContextCompactionCandidate candidate in input.Candidates)
            {
                if (candidate == null) continue;
                if (count >= _MaxCandidates) break;
                candidates.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tool"] = candidate.ToolName,
                    ["asked_for"] = candidate.RequestSummary,
                    ["output_head"] = candidate.ResultHead,
                    ["output_bytes"] = candidate.ResultBytes,
                    ["turns_ago"] = candidate.TurnsAgo
                });
                count++;
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["goal"] = input.Goal,
                ["earlier_tool_results"] = candidates
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            return BuildQuestions(_MaxCandidates);
        }

        /// <summary>
        /// One question per candidate actually supplied, so the request never asks about a result that is
        /// not there. Asking a fixed set instead let a provider answer a slot with no candidate behind it,
        /// and the verdict then named a position the caller could not resolve. An empty candidate list asks
        /// nothing at all, which the skeleton reads as "do not consult": the rule stands with no call.
        /// </summary>
        /// <param name="input">The decision input.</param>
        /// <returns>The questions, keyed by question id; empty when there is nothing to ask about.</returns>
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions(ContextCompactionDecisionInput input)
        {
            int supplied = 0;
            foreach (ContextCompactionCandidate candidate in input.Candidates)
            {
                if (candidate == null) continue;
                supplied++;
                if (supplied >= _MaxCandidates) break;
            }

            return BuildQuestions(supplied);
        }

        #endregion

        #region Private-Methods

        private static IReadOnlyDictionary<string, TypedQuestion> BuildQuestions(int slots)
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            for (int i = 1; i <= slots; i++)
            {
                string slot = i.ToString(CultureInfo.InvariantCulture);
                questions["still_load_bearing_" + slot] = new NoulQuestion(
                    "Earlier tool result number " + slot + " (see earlier_tool_results in the state, oldest first). "
                    + "The captain is still working towards the stated goal, and this "
                    + "result's full output is about to be replaced by a one-line note saying the tool can be re-run. Judge whether "
                    + "the remaining work still depends on the CONTENT of this output: a measurement, count, error text, file content, "
                    + "or decision the captain must carry forward is load-bearing, while an output whose conclusion is already settled, "
                    + "superseded by a later result, or recoverable by re-running the tool is not.",
                    TrueMeaning: "The remaining work still depends on this output's content; replacing it would lose something the captain needs.",
                    FalseMeaning: "The output is settled, superseded, or cheap to obtain again; replacing it loses nothing.");
            }

            return questions;
        }

        #endregion

        #region Protected-Overrides-Continued

        /// <inheritdoc />
        protected override ContextCompactionReading Interpret(TypedDecisionResult result)
        {
            List<int> spare = new List<int>();
            double strongest = 0.0;

            for (int i = 1; i <= _MaxCandidates; i++)
            {
                string name = "still_load_bearing_" + i.ToString(CultureInfo.InvariantCulture);
                if (!result.Answers.ContainsKey(name)) continue;

                // An unreadable answer falls back to 0.0: not load-bearing, so the rule's compaction
                // stands for that candidate. The fallback is always the rule, per candidate as well.
                double loadBearing = TypedAnswerReader.ReadNoul(result, name, 0.0);
                if (loadBearing >= _SpareFloor)
                {
                    if (loadBearing > strongest) strongest = loadBearing;
                    spare.Add(i - 1);
                }
            }

            string label = spare.Count == 0
                ? "nothing_load_bearing"
                : "load_bearing:" + spare.Count.ToString(CultureInfo.InvariantCulture);
            return new ContextCompactionReading(strongest, spare, label);
        }

        /// <inheritdoc />
        protected override ContextCompactionVerdict Combine(ContextCompactionVerdict ruleVerdict, ContextCompactionReading model)
        {
            // Combine runs only at or above the gate threshold. The rule spares nothing, so the only
            // change this can make is to RETAIN more of the captain's own history; it can never drop a
            // result the rule would have kept, and it never removes a message.
            if (model.SparePositions.Count == 0) return ruleVerdict;
            return ContextCompactionVerdict.Sparing(model.SparePositions);
        }

        /// <inheritdoc />
        protected override string RuleLabel(ContextCompactionVerdict ruleVerdict) => ruleVerdict.OutcomeLabel;

        /// <inheritdoc />
        protected override Mission? MissionOf(ContextCompactionDecisionInput input) => input.Mission;

        #endregion
    }
}
