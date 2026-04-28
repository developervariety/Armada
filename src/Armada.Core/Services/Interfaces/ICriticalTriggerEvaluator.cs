namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Decides whether an auto-landed mission diff hits a "critical" pattern
    /// warranting deep review (vs. fast-lane no-review).
    /// </summary>
    public interface ICriticalTriggerEvaluator
    {
        /// <summary>Evaluates the diff and convention result, returning fired criteria.</summary>
        CriticalTriggerResult Evaluate(string unifiedDiff, ConventionCheckResult conventionResult);
    }
}
