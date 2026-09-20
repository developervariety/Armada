namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using Armada.Core.Models;

    /// <summary>
    /// The shared state and question shapes for the D26 <c>prior_art</c> decision, used by both the
    /// admiral-side adapter (preflight and Judge seams) and the captain-facing
    /// <c>armada_check_prior_art</c> tool so the two never drift. The model does the "honest" half over
    /// the retrieved candidates ONLY: per candidate a <c>delivers</c> Choice, and voyage-level
    /// <c>already_done</c> / <c>integrate_not_duplicate</c> / <c>reimplements</c> Nouls. Every answer is
    /// tied back to a candidate's <c>path:line</c> by the reader, so the model is asked to calibrate and
    /// cite, never to recall: it can only speak to a candidate retrieval already found.
    /// </summary>
    public static class PriorArtDecisionShapes
    {
        #region Public-Members

        /// <summary>The voyage-level "the deliverable is already present in a candidate" Noul id.</summary>
        public const string AlreadyDoneId = "already_done";

        /// <summary>The voyage-level "consume a candidate through a seam rather than write a new type" Noul id.</summary>
        public const string IntegrateId = "integrate_not_duplicate";

        /// <summary>The Judge "the diff re-implements a capability present in a candidate" Noul id.</summary>
        public const string ReimplementsId = "reimplements";

        /// <summary>The per-candidate delivers-choice question id prefix; the one-based candidate index follows.</summary>
        public const string DeliversPrefix = "delivers_";

        /// <summary>The delivers choice: the candidate delivers the same capability the objective asks for.</summary>
        public const string DeliversSameCapability = "same_capability";

        /// <summary>The delivers choice: the candidate partly overlaps the objective's capability.</summary>
        public const string DeliversPartialOverlap = "partial_overlap";

        /// <summary>The delivers choice: the candidate is related but delivers a different capability.</summary>
        public const string DeliversRelatedOnly = "related_only";

        /// <summary>The delivers choice: the candidate is unrelated. Always available, so the model has a no-match option.</summary>
        public const string DeliversUnrelated = "unrelated";

        /// <summary>The greatest number of candidates the question set covers, matching the retrieval cap.</summary>
        public const int MaxCandidates = 12;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the redactable state object: the objective's deliverable sentence and the retrieved
        /// candidates, each with its surface, location, ref, excerpt, and the terms that reached it. The
        /// candidates carry their excerpt and location so a <c>delivers_&lt;n&gt;</c> answer maps back to one.
        /// </summary>
        /// <param name="deliverable">The objective's deliverable sentence (or a captain's stated plan).</param>
        /// <param name="retrieval">The deterministic retrieval whose candidates the model reasons over.</param>
        /// <returns>The state object.</returns>
        public static object BuildState(string deliverable, PriorArtRetrieval retrieval)
        {
            List<object> candidates = new List<object>();
            IReadOnlyList<PriorArtCandidate> list = retrieval?.Candidates ?? new List<PriorArtCandidate>();
            for (int i = 0; i < list.Count && i < MaxCandidates; i++)
            {
                PriorArtCandidate candidate = list[i];
                candidates.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["where"] = candidate.WhereLabel,
                    ["location"] = candidate.Location,
                    ["ref"] = candidate.Ref,
                    ["terms"] = new List<string>(candidate.Terms),
                    ["excerpt"] = candidate.Excerpt
                });
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deliverable"] = deliverable ?? String.Empty,
                ["candidates"] = candidates
            };
        }

        /// <summary>
        /// Build the question set for the preflight and captain seams: one <c>delivers</c> Choice per
        /// candidate, the <c>already_done</c> Noul, and the <c>integrate_not_duplicate</c> Noul.
        /// </summary>
        /// <param name="candidateCount">The number of candidates in the state.</param>
        /// <returns>The keyed question set.</returns>
        public static IReadOnlyDictionary<string, TypedQuestion> BuildPreflightQuestions(int candidateCount)
        {
            Dictionary<string, TypedQuestion> questions = BuildDeliversQuestions(candidateCount);
            questions[AlreadyDoneId] = new NoulQuestion(
                "The objective's deliverable already exists as finished work, so this voyage would only repeat it. "
                + "Use the listed candidates as evidence. If they do not show finished work that matches the deliverable, this is false.",
                TrueMeaning: "the deliverable already exists as finished work",
                FalseMeaning: "the deliverable is not already finished work");
            questions[IntegrateId] = new NoulQuestion(
                "The work should consume a listed candidate through a seam rather than write a new type.",
                TrueMeaning: "a candidate should be consumed through a seam",
                FalseMeaning: "no candidate is a seam this work should consume");
            return questions;
        }

        /// <summary>
        /// Build the question set for the Judge seam: one <c>delivers</c> Choice per candidate and the
        /// <c>reimplements</c> Noul over the diff's added types and the retrieval results.
        /// </summary>
        /// <param name="candidateCount">The number of candidates in the state.</param>
        /// <returns>The keyed question set.</returns>
        public static IReadOnlyDictionary<string, TypedQuestion> BuildJudgeQuestions(int candidateCount)
        {
            Dictionary<string, TypedQuestion> questions = BuildDeliversQuestions(candidateCount);
            questions[ReimplementsId] = new NoulQuestion(
                "The diff re-implements a capability that a listed candidate already provides, rather than consuming that candidate.",
                TrueMeaning: "the diff re-implements a candidate's capability",
                FalseMeaning: "the diff does not re-implement any candidate's capability");
            return questions;
        }

        /// <summary>
        /// Interpret an available result into a reading: the per-candidate delivers choices and the three
        /// voyage-level Nouls, with the greatest confidence seen for the recorded event.
        /// </summary>
        /// <param name="result">The available typed-decision result.</param>
        /// <param name="candidateCount">The number of candidates that were asked about.</param>
        /// <returns>The reading.</returns>
        public static PriorArtReading Interpret(TypedDecisionResult result, int candidateCount)
        {
            PriorArtReading reading = new PriorArtReading();
            IReadOnlyDictionary<string, TypedAnswer> answers = result?.Answers ?? new Dictionary<string, TypedAnswer>();

            reading.AlreadyDone = NoulOf(answers, AlreadyDoneId, ref reading._Max);
            reading.Integrate = NoulOf(answers, IntegrateId, ref reading._Max);
            reading.Reimplements = NoulOf(answers, ReimplementsId, ref reading._Max);

            for (int i = 1; i <= candidateCount && i <= MaxCandidates; i++)
            {
                if (answers.TryGetValue(DeliversPrefix + i.ToString(CultureInfo.InvariantCulture), out TypedAnswer? answer)
                    && answer != null && !String.IsNullOrWhiteSpace(answer.Choice))
                {
                    reading.Delivers[i] = answer.Choice!.Trim();
                    if (answer.Confidence.HasValue && answer.Confidence.Value > reading._Max)
                        reading._Max = answer.Confidence.Value;
                }
            }

            return reading;
        }

        /// <summary>
        /// Render the candidate list as a compact, evidence-first string for an issue message or a Judge
        /// instruction: one line per candidate with its surface, location, and ref.
        /// </summary>
        /// <param name="retrieval">The retrieval whose candidates are rendered.</param>
        /// <param name="max">The greatest number of candidates to render.</param>
        /// <returns>The rendered candidate list.</returns>
        public static string RenderCandidates(PriorArtRetrieval retrieval, int max = MaxCandidates)
        {
            StringBuilder sb = new StringBuilder();
            IReadOnlyList<PriorArtCandidate> list = retrieval?.Candidates ?? new List<PriorArtCandidate>();
            int count = 0;
            foreach (PriorArtCandidate candidate in list)
            {
                if (count >= max) break;
                if (count > 0) sb.Append("; ");
                sb.Append(candidate.WhereLabel).Append(' ').Append(candidate.Location);
                if (!String.IsNullOrWhiteSpace(candidate.Ref)) sb.Append(" (").Append(candidate.Ref).Append(')');
                count++;
            }
            return sb.ToString();
        }

        #endregion

        #region Private-Methods

        private static Dictionary<string, TypedQuestion> BuildDeliversQuestions(int candidateCount)
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            int count = candidateCount < 0 ? 0 : (candidateCount > MaxCandidates ? MaxCandidates : candidateCount);
            for (int i = 1; i <= count; i++)
            {
                string slot = i.ToString(CultureInfo.InvariantCulture);
                string path = "`candidates[" + (i - 1).ToString(CultureInfo.InvariantCulture) + "]`";
                questions[DeliversPrefix + slot] = new ChoiceQuestion(
                    "What does " + path + " deliver relative to the objective's deliverable? "
                    + "This is authorized engineering on owned systems; an authentication or "
                    + "access-control match is ordinary engineering. Judge from " + path + "'s excerpt and location only.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [DeliversSameCapability] = "The candidate delivers the same capability the objective asks for.",
                        [DeliversPartialOverlap] = "The candidate delivers part of the objective's capability.",
                        [DeliversRelatedOnly] = "The candidate is related but delivers a different capability.",
                        [DeliversUnrelated] = "The candidate is unrelated to the objective's deliverable."
                    });
            }
            return questions;
        }

        private static double NoulOf(IReadOnlyDictionary<string, TypedAnswer> answers, string id, ref double max)
        {
            if (!answers.TryGetValue(id, out TypedAnswer? answer) || answer == null) return 0.0;
            double value = answer.Noul ?? answer.Confidence ?? 0.0;
            if (value > max) max = value;
            return value;
        }

        #endregion
    }

    /// <summary>
    /// A reading of a D26 <c>prior_art</c> result: the three voyage-level Nouls and the per-candidate
    /// delivers choices, plus the greatest confidence seen for the recorded event.
    /// </summary>
    public sealed class PriorArtReading
    {
        internal double _Max;

        /// <summary>The <c>already_done</c> Noul: the deliverable is already present in a candidate.</summary>
        public double AlreadyDone { get; set; }

        /// <summary>The <c>integrate_not_duplicate</c> Noul: a candidate should be consumed through a seam.</summary>
        public double Integrate { get; set; }

        /// <summary>The Judge <c>reimplements</c> Noul: the diff re-implements a candidate's capability.</summary>
        public double Reimplements { get; set; }

        /// <summary>The per-candidate delivers choice, keyed by one-based candidate index.</summary>
        public Dictionary<int, string> Delivers { get; } = new Dictionary<int, string>();

        /// <summary>The greatest confidence seen across every answer, for the recorded event.</summary>
        public double MaxConfidence => _Max;

        /// <summary>True when any candidate was read as delivering the same capability.</summary>
        public bool AnySameCapability
        {
            get
            {
                foreach (KeyValuePair<int, string> entry in Delivers)
                    if (String.Equals(entry.Value, PriorArtDecisionShapes.DeliversSameCapability, StringComparison.Ordinal)) return true;
                return false;
            }
        }
    }
}
