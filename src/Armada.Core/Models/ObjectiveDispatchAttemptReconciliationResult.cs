namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Outcome counts from one pass over open objective dispatch attempts.
    /// </summary>
    public class ObjectiveDispatchAttemptReconciliationResult
    {
        /// <summary>
        /// Open attempts examined.
        /// </summary>
        public int Examined { get; set; } = 0;

        /// <summary>
        /// Attempts left alone because their owner still holds a live lease.
        /// </summary>
        public int LiveOwners { get; set; } = 0;

        /// <summary>
        /// Attempts left for a later pass because another request holds an admission they need.
        /// </summary>
        public int Busy { get; set; } = 0;

        /// <summary>
        /// Attempts whose voyage was linked to an objective and therefore kept as the winner.
        /// </summary>
        public int Kept { get; set; } = 0;

        /// <summary>
        /// Orphan voyages cancelled because no objective linked them.
        /// </summary>
        public int CancelledOrphans { get; set; } = 0;

        /// <summary>
        /// Attempts closed with no voyage to act on.
        /// </summary>
        public int ClosedWithoutVoyage { get; set; } = 0;

        /// <summary>
        /// Attempts closed without action because the voyage could not be identified safely, with the reason.
        /// </summary>
        public List<string> Unresolved { get; set; } = new List<string>();
    }
}
