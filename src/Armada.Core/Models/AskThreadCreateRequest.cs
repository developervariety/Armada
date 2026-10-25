namespace Armada.Core.Models
{
    /// <summary>Optional fields for creating a saved Ask thread.</summary>
    public sealed class AskThreadCreateRequest
    {
        /// <summary>Initial title, or null for the default title.</summary>
        public string? Title { get; set; } = null;
        /// <summary>Visible captain to assign, or null for no captain.</summary>
        public string? CaptainId { get; set; } = null;
    }
}
