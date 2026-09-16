namespace Armada.Core.Models
{
    /// <summary>API key submitted once for an API-key account login.</summary>
    public sealed class AccountLoginKeyRequest
    {
        #region Public-Members

        /// <summary>The key. Written only to the account's own credential file; never logged, returned, or stored in settings.</summary>
        public string? ApiKey { get; set; }

        #endregion
    }
}
