namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>Evaluates a unified diff string against a vessel auto-land predicate.</summary>
    public interface IAutoLandEvaluator
    {
        /// <summary>
        /// Evaluates <paramref name="unifiedDiff"/> against <paramref name="predicate"/> and returns
        /// <see cref="EvaluationResult.Pass"/> when auto-land may proceed or
        /// <see cref="EvaluationResult.Fail"/> with the first violated rule reason.
        /// </summary>
        EvaluationResult Evaluate(string unifiedDiff, AutoLandPredicate predicate);
    }
}
