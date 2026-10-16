namespace Armada.Core.Models
{
    /// <summary>
    /// A Harbor's answer to where it has a vessel checked out.
    /// </summary>
    public class HarborCheckoutLookup
    {
        #region Public-Members

        /// <summary>
        /// True when the Harbor answered (with or without a checkout); false when it was not connected, does not serve
        /// checkout operations, did not answer in time, or the request failed.
        /// </summary>
        public bool Answered { get; set; } = false;

        /// <summary>
        /// The checkout folder on the Harbor's machine, or null when it has none (only its own clone, or nothing).
        /// </summary>
        public string? Path { get; set; } = null;

        #endregion
    }
}
