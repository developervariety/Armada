namespace Armada.Core.Models
{
    /// <summary>
    /// One explicit rank update in a backlog reorder request.
    /// </summary>
    public class ObjectiveReorderItem
    {
        /// <summary>
        /// Objective identifier to update.
        /// </summary>
        public string ObjectiveId { get; set; } = string.Empty;

        /// <summary>
        /// New deterministic rank for the objective.
        /// </summary>
        public int Rank { get; set; } = 0;
    }
}
