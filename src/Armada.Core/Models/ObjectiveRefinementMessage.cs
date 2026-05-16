namespace Armada.Core.Models
{
    /// <summary>
    /// One transcript message in an objective refinement session.
    /// </summary>
    public class ObjectiveRefinementMessage
    {
        /// <summary>
        /// Gets or sets the identifier.
        /// </summary>
        public string Id { get; set; } = Constants.IdGenerator.GenerateKSortable("orm_", 24);
        /// <summary>
        /// Gets or sets the objective refinement session identifier.
        /// </summary>
        public string ObjectiveRefinementSessionId { get; set; } = String.Empty;
        /// <summary>
        /// Gets or sets the objective identifier.
        /// </summary>
        public string ObjectiveId { get; set; } = String.Empty;
        /// <summary>
        /// Gets or sets the tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;
        /// <summary>
        /// Gets or sets the user identifier.
        /// </summary>
        public string? UserId { get; set; } = null;
        /// <summary>
        /// Gets or sets the message role.
        /// </summary>
        public string Role { get; set; } = "User";
        /// <summary>
        /// Gets or sets the message sequence number.
        /// </summary>
        public int Sequence { get; set; } = 1;
        /// <summary>
        /// Gets or sets the content.
        /// </summary>
        public string Content { get; set; } = String.Empty;
        /// <summary>
        /// Gets or sets a value indicating whether the message is selected.
        /// </summary>
        public bool IsSelected { get; set; } = false;
        /// <summary>
        /// Gets or sets the creation UTC timestamp.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        /// <summary>
        /// Gets or sets the last update UTC timestamp.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;
    }
}
