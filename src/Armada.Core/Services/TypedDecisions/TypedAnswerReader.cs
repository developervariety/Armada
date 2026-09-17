namespace Armada.Core.Services
{
    using Armada.Core.Models;

    /// <summary>
    /// Shared reads over a typed-decision result's answers. Every categorical and noul-scored decision
    /// interprets a chosen option's confidence and a keyed noul the same way, so those two reads live
    /// here once instead of being copied into each adapter. A change to how an answer is read then
    /// reaches every decision at the same time.
    /// </summary>
    internal static class TypedAnswerReader
    {
        /// <summary>
        /// The confidence the model assigns the option it chose: its explicit confidence when present,
        /// otherwise the probability it gave that option, otherwise zero.
        /// </summary>
        /// <param name="answer">The answer to a choice question.</param>
        /// <param name="choice">The chosen option key, used to read its probability when the answer carries no explicit confidence.</param>
        /// <returns>The confidence in [0, 1].</returns>
        internal static double ResolveChoiceConfidence(TypedAnswer answer, string choice)
        {
            if (answer.Confidence.HasValue) return answer.Confidence.Value;
            if (answer.Probabilities != null && answer.Probabilities.TryGetValue(choice, out double probability)) return probability;
            return 0.0;
        }

        /// <summary>
        /// The numeric noul score for a keyed answer, or <paramref name="fallback"/> when the result
        /// carries no answer for the key or that answer has no noul.
        /// </summary>
        /// <param name="result">The decision result.</param>
        /// <param name="key">The question id whose noul is read.</param>
        /// <param name="fallback">The value returned when the noul is absent (defaults to zero).</param>
        /// <returns>The noul, or the fallback.</returns>
        internal static double ReadNoul(TypedDecisionResult result, string key, double fallback = 0.0)
        {
            if (result.Answers != null
                && result.Answers.TryGetValue(key, out TypedAnswer? answer)
                && answer != null
                && answer.Noul.HasValue)
            {
                return answer.Noul.Value;
            }
            return fallback;
        }
    }
}
