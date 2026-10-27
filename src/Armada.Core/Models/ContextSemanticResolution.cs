namespace Armada.Core.Models
{
    using Armada.Core.Services;

    /// <summary>The semantic index for one query, or the named reason it is off.</summary>
    public sealed class ContextSemanticResolution
    {
        /// <summary>Index to use; null when semantic ranking is off.</summary>
        public ContextSemanticIndex? Index { get; set; }

        /// <summary>Why the index is off; null when it is available.</summary>
        public string? Reason { get; set; }
    }
}
