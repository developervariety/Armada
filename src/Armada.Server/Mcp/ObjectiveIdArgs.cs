namespace Armada.Server.Mcp
{
    /// <summary>
    /// Arguments containing one objective identifier.
    /// </summary>
    public class ObjectiveIdArgs
    {
        /// <summary>
        /// Objective identifier.
        /// </summary>
        public string ObjectiveId { get; set; } = string.Empty;

        /// <summary>
        /// Optional 1-based page number for list operations.
        /// </summary>
        public int? PageNumber { get; set; }

        /// <summary>
        /// Optional page size for list operations.
        /// </summary>
        public int? PageSize { get; set; }
    }
}
