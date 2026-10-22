namespace Armada.Server
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// What applying a refinement summary produced: the summary that was applied and the updated objective.
    /// </summary>
    public class ObjectiveRefinementApplyOutcome
    {
        /// <summary>The applied refinement summary.</summary>
        public ObjectiveRefinementSummaryResponse Summary { get; }

        /// <summary>The objective after the summary was applied.</summary>
        public Objective Objective { get; }

        /// <summary>Create an outcome.</summary>
        /// <param name="summary">The applied summary.</param>
        /// <param name="objective">The updated objective.</param>
        /// <exception cref="ArgumentNullException">Thrown when summary or objective is null.</exception>
        public ObjectiveRefinementApplyOutcome(ObjectiveRefinementSummaryResponse summary, Objective objective)
        {
            Summary = summary ?? throw new ArgumentNullException(nameof(summary));
            Objective = objective ?? throw new ArgumentNullException(nameof(objective));
        }
    }
}
