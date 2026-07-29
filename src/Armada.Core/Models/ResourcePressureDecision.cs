namespace Armada.Core.Models
{
    /// <summary>
    /// Admission decision returned by the resource-pressure admission policy.
    /// </summary>
    public sealed class ResourcePressureDecision
    {
        /// <summary>
        /// Whether the mission/captain may be launched now.
        /// </summary>
        public bool Admit { get; set; }

        /// <summary>
        /// Human-readable reason explaining a deferral, or empty when admitted.
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// Resource-pressure snapshot captured during evaluation.
        /// </summary>
        public ResourcePressureSnapshot? Snapshot { get; set; }
    }
}