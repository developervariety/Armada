namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Batch rank updates for backlog/objective reordering.
    /// </summary>
    public class ObjectiveReorderRequest
    {
        /// <summary>
        /// Rank updates to apply.
        /// </summary>
        public List<ObjectiveReorderItem> Items { get; set; } = new List<ObjectiveReorderItem>();
    }
}
