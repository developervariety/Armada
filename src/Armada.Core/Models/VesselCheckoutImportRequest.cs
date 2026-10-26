namespace Armada.Core.Models
{
    /// <summary>Request to register an existing local checkout as a vessel.</summary>
    public sealed class VesselCheckoutImportRequest
    {
        /// <summary>Candidate checkout path returned by discovery.</summary>
        public string CheckoutPath { get; set; } = string.Empty;

        /// <summary>Vessel name. It must satisfy the regular vessel-name rules.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Optional fleet identifier visible to the caller.</summary>
        public string? FleetId { get; set; }
    }
}
