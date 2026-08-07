namespace Armada.Core.Services
{
    /// <summary>
    /// Result of resolving a captain's model against the provider registry: the
    /// endpoint and credential the captain process must use, or null when the captain
    /// stays on its runtime's native endpoint.
    /// </summary>
    public class ResolvedModelProvider
    {
        #region Public-Members

        /// <summary>
        /// Registered provider namespace prefix, for example "zyloo". Null when the
        /// captain is a custom-endpoint captain whose model carries no prefix.
        /// </summary>
        public string? ProviderId { get; set; }

        /// <summary>
        /// Base URL the captain process must be pointed at.
        /// </summary>
        public string BaseUrl { get; set; } = String.Empty;

        /// <summary>
        /// Credential the captain process must authenticate with.
        /// </summary>
        public string ApiKey { get; set; } = String.Empty;

        #endregion
    }
}
