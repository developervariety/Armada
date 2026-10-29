namespace Armada.Core.Models
{
    using System;

    /// <summary>A saved Ask Armada conversation owned by one user in one tenant.</summary>
    public sealed class AskThread
    {
        /// <summary>Unique thread identifier.</summary>
        public string Id { get; set; } = Constants.IdGenerator.GenerateKSortable(Constants.AskThreadIdPrefix, 24);
        /// <summary>Owning tenant identifier.</summary>
        public string TenantId { get; set; } = String.Empty;
        /// <summary>Owning user identifier.</summary>
        public string UserId { get; set; } = String.Empty;
        /// <summary>Thread title.</summary>
        public string Title { get; set; } = "New conversation";
        /// <summary>Captain assigned to this conversation, or null.</summary>
        public string? CaptainId { get; set; } = null;
        /// <summary>Whether this thread is pinned in the owner's list.</summary>
        public bool Pinned { get; set; } = false;
        /// <summary>Whether this thread is archived.</summary>
        public bool Archived { get; set; } = false;
        /// <summary>Creation time in UTC.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        /// <summary>Time of the latest user or assistant activity in UTC.</summary>
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
        /// <summary>Request id for the durable active turn, or null.</summary>
        public string? ActiveTurnId { get; set; } = null;
        /// <summary>Replies that finished since the owner last opened the thread. Zero when read.</summary>
        public int UnreadCount { get; set; } = 0;
    }
}
