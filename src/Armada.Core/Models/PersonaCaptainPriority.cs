namespace Armada.Core.Models
{
    using System.ComponentModel.DataAnnotations;
    using Armada.Core.Enums;

    /// <summary>
    /// Matches a captain class. Non-null fields are combined with AND; list order sets priority.
    /// At least one field is required. Model and endpoint names match exactly, ignoring case.
    /// </summary>
    public class PersonaCaptainPriority
    {
        /// <summary>Optional runtime type. Null matches every runtime.</summary>
        public AgentRuntimeEnum? Runtime { get; set; } = null;

        /// <summary>Optional exact model identifier, up to 1024 characters. Null matches every model.</summary>
        [MaxLength(1024)]
        public string? Model { get; set; } = null;

        /// <summary>Optional named Mux endpoint, up to 1024 characters. Requires Runtime Mux.</summary>
        [MaxLength(1024)]
        public string? Endpoint { get; set; } = null;

        /// <summary>Optional minimum effective capability tier. Null imposes no additional tier floor.</summary>
        public CaptainTierEnum? MinimumTier { get; set; } = null;
    }
}
