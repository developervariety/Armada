namespace Armada.Core.Models
{
    /// <summary>
    /// What counts as work that already owns an objective.
    /// </summary>
    public class ObjectiveActiveWorkQuery
    {
        /// <summary>
        /// Linked voyages to leave out, such as the voyage of the failure being recovered and its rescues.
        /// </summary>
        public List<string> ExcludeVoyageIds
        {
            get => _ExcludeVoyageIds;
            set => _ExcludeVoyageIds = value ?? new List<string>();
        }

        /// <summary>
        /// Count only voyages created after this time. Null counts every active linked voyage.
        /// </summary>
        public DateTime? CreatedAfterUtc { get; set; } = null;

        /// <summary>
        /// When set, a Challenger still ruling on a failed Judge of the objective, dispatched within
        /// this window, also owns the objective. Null leaves challenges out.
        /// </summary>
        public TimeSpan? JudgeChallengeTimeout { get; set; } = null;

        private List<string> _ExcludeVoyageIds = new List<string>();
    }
}
