namespace Armada.Core.Models
{
    /// <summary>
    /// Public OAuth2 configuration surfaced to the dashboard so it can show or
    /// hide the single sign-on button. Contains no secrets.
    /// </summary>
    public class OAuthConfigResult
    {
        #region Public-Members

        /// <summary>
        /// Whether OAuth2 single sign-on is enabled and fully configured.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Label to show on the sign-in button.
        /// </summary>
        public string DisplayName { get; set; } = "Single Sign-On";

        #endregion
    }
}
