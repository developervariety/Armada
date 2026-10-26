namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>A typed reference to work created by an Ask action.</summary>
    public sealed class AskWorkReference
    {
        /// <summary>Work record kind.</summary>
        public AskWorkKindEnum Kind { get; set; }

        /// <summary>Work record identifier.</summary>
        public string Id { get; set; } = string.Empty;
    }
}
