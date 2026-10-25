namespace Armada.Core.Models
{
    /// <summary>Presence-aware changes to a saved Ask thread.</summary>
    public sealed class AskThreadUpdateRequest
    {
        /// <summary>Whether to update the title.</summary>
        public bool SetTitle { get; set; }
        /// <summary>New title when <see cref="SetTitle"/> is true.</summary>
        public string? Title { get; set; }
        /// <summary>Whether to update or clear the captain.</summary>
        public bool SetCaptainId { get; set; }
        /// <summary>New captain id, or null to clear when <see cref="SetCaptainId"/> is true.</summary>
        public string? CaptainId { get; set; }
        /// <summary>Whether to update the pinned flag.</summary>
        public bool SetPinned { get; set; }
        /// <summary>New pinned value when <see cref="SetPinned"/> is true.</summary>
        public bool Pinned { get; set; }
        /// <summary>Whether to update the archived flag.</summary>
        public bool SetArchived { get; set; }
        /// <summary>New archived value when <see cref="SetArchived"/> is true.</summary>
        public bool Archived { get; set; }
    }
}
