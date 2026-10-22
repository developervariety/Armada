namespace Armada.Core.Models
{
    /// <summary>
    /// One objective a scheduler sweep examined and did not dispatch, with the reason.
    /// </summary>
    public class ObjectiveSchedulerSkippedObjective
    {
        /// <summary>Objective identifier.</summary>
        public string ObjectiveId { get; set; } = String.Empty;

        /// <summary>Objective title.</summary>
        public string Title { get; set; } = String.Empty;

        /// <summary>
        /// Skip reason, the same name the sweep counts in <c>LastSkipReason</c>, for example
        /// <c>dispatch_preflight</c>, <c>vessel_concurrency</c> or <c>dependency_blocked</c>.
        /// </summary>
        public string Reason { get; set; } = String.Empty;

        /// <summary>
        /// The first error code of the dispatch preview when the preview refused the objective, or another
        /// short detail of the skip; null when the reason says everything.
        /// </summary>
        public string? Detail { get; set; } = null;
    }
}
