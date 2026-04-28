namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>Checks a unified diff for project-wide CORE RULE violations via regex on '+' lines.</summary>
    public interface IConventionChecker
    {
        /// <summary>Checks the unified diff and returns the result of all rule evaluations.</summary>
        ConventionCheckResult Check(string unifiedDiff);
    }
}
