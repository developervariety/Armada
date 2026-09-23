namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// The requested captain and fallback tier a mission takes from its captain override or its persona's
    /// default captain.
    /// </summary>
    public class RequestedCaptainResolution
    {
        #region Public-Members

        /// <summary>
        /// The requested captain identifier, or null when neither the override nor the persona names one.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// The fallback tier the override stores, or null when it stores none.
        /// </summary>
        public CaptainTierEnum? FallbackTier { get; set; } = null;

        #endregion
    }
}
