namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Server-only credential files for model endpoints. Each mapped endpoint reads its API key from one file in
    /// <see cref="Directory"/> when a runtime is built for it, so the key is never stored on the endpoint record,
    /// returned by a tool or written to a log.
    /// </summary>
    /// <remarks>
    /// Lives under the <c>modelEndpointCredentials</c> key in settings.json. Empty by default: no endpoint reads a
    /// credential file until an operator maps it.
    /// </remarks>
    public class ModelEndpointCredentialSettings
    {
        #region Public-Members

        /// <summary>
        /// Absolute directory that holds the credential files. Null or empty disables file credentials.
        /// </summary>
        public string? Directory { get; set; } = null;

        /// <summary>
        /// Credential file name keyed by endpoint id (for example <c>mep_example</c>). A name is a single file
        /// name inside <see cref="Directory"/>, never a path.
        /// </summary>
        public Dictionary<string, string> Files
        {
            get => _Files;
            set => _Files = value ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        #endregion

        #region Private-Members

        private Dictionary<string, string> _Files = new Dictionary<string, string>(StringComparer.Ordinal);

        #endregion
    }
}
