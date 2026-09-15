namespace Armada.Server.WebSocket
{
    /// <summary>
    /// Data carried by an <c>objective.deleted</c> WebSocket event: the identity and owner of the deleted
    /// objective, so a client can drop it from any list it holds.
    /// </summary>
    public sealed class ObjectiveDeletedEventData
    {
        #region Public-Members

        /// <summary>
        /// Deleted objective ID (obj_ prefix).
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Tenant that owned the objective, or null for an unowned record.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// User that owned the objective, or null for an unowned record.
        /// </summary>
        public string? UserId { get; set; } = null;

        #endregion
    }
}
