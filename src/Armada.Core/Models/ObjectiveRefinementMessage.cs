namespace Armada.Core.Models
{
    /// <summary>
    /// One transcript message in an objective refinement session.
    /// </summary>
    public class ObjectiveRefinementMessage
    {
        public string Id { get; set; } = Constants.IdGenerator.GenerateKSortable("orm_", 24);
        public string ObjectiveRefinementSessionId { get; set; } = String.Empty;
        public string ObjectiveId { get; set; } = String.Empty;
        public string? TenantId { get; set; } = null;
        public string? UserId { get; set; } = null;
        public string Role { get; set; } = "User";
        public int Sequence { get; set; } = 1;
        public string Content { get; set; } = String.Empty;
        public bool IsSelected { get; set; } = false;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;
    }
}
