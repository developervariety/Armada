namespace Armada.Core.Models
{
    /// <summary>
    /// Result of GET /api/v1/whoami.
    /// </summary>
    public class WhoAmIResult
    {
        #region Public-Members

        /// <summary>
        /// Tenant metadata.
        /// </summary>
        public TenantMetadata? Tenant { get; set; } = null;

        /// <summary>
        /// User information (password redacted).
        /// </summary>
        public UserMaster? User { get; set; } = null;

        /// <summary>
        /// Whether the caller is a seeded admin account that still uses the well-known default password.
        /// </summary>
        public bool PasswordChangeRequired { get; set; } = false;

        #endregion
    }
}
