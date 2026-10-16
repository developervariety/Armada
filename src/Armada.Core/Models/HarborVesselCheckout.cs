namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Where a vessel is checked out on a Harbor's machine.
    /// </summary>
    public class HarborVesselCheckout
    {
        #region Public-Members

        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = String.Empty;

        /// <summary>
        /// Vessel name.
        /// </summary>
        public string VesselName { get; set; } = String.Empty;

        /// <summary>
        /// The checkout folder on the Harbor's machine.
        /// </summary>
        public string Path { get; set; } = String.Empty;

        #endregion
    }
}
