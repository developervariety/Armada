namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Runs typed-decision evaluation cases against a client and scores the answers. A reference case
    /// passes when every stated answer holds for each variant; a consistency case passes when the named
    /// answers agree between the variants within a tolerance. A case the provider does not answer is
    /// reported unavailable, never passed or failed. Never throws for a provider fault.
    /// </summary>
    public static class TypedDecisionEvalRunner
    {
        #region Public-Members

        /// <summary>
        /// The largest noul difference two consistency variants may show and still agree.
        /// </summary>
        public const double NoulTolerance = 0.20;

        /// <summary>
        /// The largest score difference two consistency variants may show and still agree.
        /// </summary>
        public const double ScoreTolerance = 0.50;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run the cases in order and return the report.
        /// </summary>
        /// <param name="client">The typed-decision client.</param>
        /// <param name="cases">The cases to run.</param>
        /// <param name="reason">Why the run happens, recorded on the report.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The report; never null.</returns>
        public static async Task<TypedDecisionEvalReport> RunAsync(
            ITypedDecisionClient client,
            IReadOnlyList<TypedDecisionEvalCase> cases,
            string reason,
            CancellationToken token)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            TypedDecisionEvalReport report = new TypedDecisionEvalReport { Reason = reason ?? String.Empty, StartedUtc = DateTime.UtcNow };
            if (cases == null) return report;

            foreach (TypedDecisionEvalCase evalCase in cases)
            {
                token.ThrowIfCancellationRequested();
                TypedDecisionEvalCaseResult caseResult = new TypedDecisionEvalCaseResult
                {
                    CaseId = evalCase.Id,
                    DecisionPoint = evalCase.DecisionPoint,
                    Kind = evalCase.Kind.ToString()
                };

                TypedDecisionResult a = await AskAsync(client, evalCase, evalCase.VariantA, report, token).ConfigureAwait(false);
                TypedDecisionResult? b = null;
                if (a.Available && evalCase.VariantB != null)
                    b = await AskAsync(client, evalCase, evalCase.VariantB, report, token).ConfigureAwait(false);

                if (!a.Available || (b != null && !b.Available))
                {
                    caseResult.Outcome = "unavailable";
                    caseResult.UnavailableReason = !a.Available ? a.UnavailableReason : b!.UnavailableReason;
                    report.Unavailable++;
                }
                else
                {
                    Describe(caseResult, "A", a);
                    if (b != null) Describe(caseResult, "B", b);

                    if (evalCase.Kind == TypedDecisionEvalCaseKindEnum.Consistency)
                    {
                        if (b == null) caseResult.Failures.Add("consistency case has no variant B");
                        else CheckConsistency(caseResult, evalCase.ConsistentQuestionIds, a, b);
                    }
                    else
                    {
                        CheckExpectations(caseResult, "A", evalCase.ExpectedA, a);
                        if (b != null) CheckExpectations(caseResult, "B", evalCase.ExpectedB, b);
                    }

                    caseResult.Outcome = caseResult.Failures.Count == 0 ? "passed" : "failed";
                    if (caseResult.Failures.Count == 0) report.Passed++;
                    else report.Failed++;
                }

                report.Total++;
                report.Cases.Add(caseResult);
            }

            return report;
        }

        #endregion

        #region Private-Methods

        private static async Task<TypedDecisionResult> AskAsync(
            ITypedDecisionClient client,
            TypedDecisionEvalCase evalCase,
            TypedDecisionBatchItem variant,
            TypedDecisionEvalReport report,
            CancellationToken token)
        {
            TypedDecisionResult result;
            try
            {
                result = await client.DecideAsync(new TypedDecisionRequest
                {
                    DecisionPoint = evalCase.DecisionPoint,
                    State = variant.State.State,
                    Questions = variant.Questions
                }, token).ConfigureAwait(false) ?? new TypedDecisionResult { Available = false, UnavailableReason = "exception" };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                result = new TypedDecisionResult { Available = false, UnavailableReason = "exception" };
            }

            report.InputTokens += result.InputTokens;
            report.OutputTokens += result.OutputTokens;
            if (!String.IsNullOrWhiteSpace(result.Model)) report.Model = result.Model;
            return result;
        }

        private static void CheckExpectations(
            TypedDecisionEvalCaseResult caseResult,
            string variant,
            IReadOnlyDictionary<string, TypedDecisionExpectation> expected,
            TypedDecisionResult result)
        {
            foreach (KeyValuePair<string, TypedDecisionExpectation> entry in expected)
            {
                string label = variant + "." + entry.Key;
                if (!result.Answers.TryGetValue(entry.Key, out TypedAnswer? answer) || answer == null)
                {
                    caseResult.Failures.Add(label + ": no answer");
                    continue;
                }

                TypedDecisionExpectation want = entry.Value;
                if (want.Choice != null && !String.Equals(want.Choice, answer.Choice?.Trim(), StringComparison.Ordinal))
                    caseResult.Failures.Add(label + ": expected choice " + want.Choice + ", got " + (answer.Choice ?? "none"));
                if (want.NoulAtLeast.HasValue && !(answer.Noul >= want.NoulAtLeast.Value))
                    caseResult.Failures.Add(label + ": expected noul >= " + Number(want.NoulAtLeast) + ", got " + Number(answer.Noul));
                if (want.NoulAtMost.HasValue && !(answer.Noul <= want.NoulAtMost.Value))
                    caseResult.Failures.Add(label + ": expected noul <= " + Number(want.NoulAtMost) + ", got " + Number(answer.Noul));
                if (want.ScoreAtLeast.HasValue && !(answer.Score >= want.ScoreAtLeast.Value))
                    caseResult.Failures.Add(label + ": expected score >= " + Number(want.ScoreAtLeast) + ", got " + Number(answer.Score));
                if (want.ScoreAtMost.HasValue && !(answer.Score <= want.ScoreAtMost.Value))
                    caseResult.Failures.Add(label + ": expected score <= " + Number(want.ScoreAtMost) + ", got " + Number(answer.Score));
                if (want.ScoreLevelsUpTo.HasValue && want.ScoreLevelsProbabilityAtLeast.HasValue)
                {
                    double? mass = LevelsProbability(answer, want.ScoreLevelsUpTo.Value);
                    if (!(mass >= want.ScoreLevelsProbabilityAtLeast.Value))
                        caseResult.Failures.Add(label + ": expected P(level <= " + want.ScoreLevelsUpTo.Value.ToString(CultureInfo.InvariantCulture)
                            + ") >= " + Number(want.ScoreLevelsProbabilityAtLeast) + ", got " + Number(mass));
                }
            }
        }

        private static double? LevelsProbability(TypedAnswer answer, int upTo)
        {
            if (answer.Probabilities == null || answer.Probabilities.Count == 0) return null;
            double mass = 0.0;
            foreach (KeyValuePair<string, double> entry in answer.Probabilities)
            {
                if (!Int32.TryParse(entry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)) return null;
                if (level <= upTo) mass += entry.Value;
            }
            return mass;
        }

        private static void CheckConsistency(
            TypedDecisionEvalCaseResult caseResult,
            IReadOnlyList<string> questionIds,
            TypedDecisionResult a,
            TypedDecisionResult b)
        {
            foreach (string questionId in questionIds)
            {
                a.Answers.TryGetValue(questionId, out TypedAnswer? left);
                b.Answers.TryGetValue(questionId, out TypedAnswer? right);
                if (left == null || right == null)
                {
                    caseResult.Failures.Add(questionId + ": no answer on " + (left == null ? "A" : "B"));
                    continue;
                }

                if (left.Choice != null || right.Choice != null)
                {
                    if (!String.Equals(left.Choice?.Trim(), right.Choice?.Trim(), StringComparison.Ordinal))
                        caseResult.Failures.Add(questionId + ": choice changed from " + (left.Choice ?? "none") + " to " + (right.Choice ?? "none"));
                }
                else if (left.Noul.HasValue || right.Noul.HasValue)
                {
                    if (!left.Noul.HasValue || !right.Noul.HasValue || Math.Abs(left.Noul.Value - right.Noul.Value) > NoulTolerance)
                        caseResult.Failures.Add(questionId + ": noul moved from " + Number(left.Noul) + " to " + Number(right.Noul));
                }
                else if (!left.Score.HasValue || !right.Score.HasValue || Math.Abs(left.Score.Value - right.Score.Value) > ScoreTolerance)
                {
                    caseResult.Failures.Add(questionId + ": score moved from " + Number(left.Score) + " to " + Number(right.Score));
                }
            }
        }

        private static void Describe(TypedDecisionEvalCaseResult caseResult, string variant, TypedDecisionResult result)
        {
            foreach (KeyValuePair<string, TypedAnswer> entry in result.Answers)
            {
                TypedAnswer answer = entry.Value;
                string value = answer.Choice ?? (answer.Noul.HasValue ? Number(answer.Noul) : Number(answer.Score));
                string confidence = answer.Confidence.HasValue ? " (conf " + Number(answer.Confidence) + ")" : String.Empty;
                caseResult.Answers.Add(variant + "." + entry.Key + " = " + value + confidence);
            }
        }

        private static string Number(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : "none";
        }

        #endregion
    }
}
