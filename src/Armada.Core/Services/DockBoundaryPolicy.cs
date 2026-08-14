namespace Armada.Core.Services
{
    using System.Collections.Generic;

    /// <summary>
    /// Per-vessel policy driving the <see cref="DockBoundaryScanner"/>: whether to scan for secrets, the
    /// protected file-path globs a mission must not touch, and private identifiers that must not leak into
    /// a public repo.
    /// </summary>
    public sealed class DockBoundaryPolicy
    {
        #region Public-Members

        /// <summary>
        /// Whether built-in secret scanning runs for this vessel.
        /// </summary>
        public bool SecretScanEnabled { get; set; } = false;

        /// <summary>
        /// File-path globs that a mission's diff must not touch (e.g. ".github/**", "LICENSE").
        /// </summary>
        public List<string> ProtectedPathGlobs { get; set; } = new List<string>();

        /// <summary>
        /// Private identifiers (company/domain strings) that must not appear in added diff lines.
        /// </summary>
        public List<string> PrivateIdentifiers { get; set; } = new List<string>();

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether this policy enables any check at all.
        /// </summary>
        /// <returns>True if secret scanning is on or any glob/identifier is configured.</returns>
        public bool HasAnyRule()
        {
            return SecretScanEnabled
                || (ProtectedPathGlobs != null && ProtectedPathGlobs.Count > 0)
                || (PrivateIdentifiers != null && PrivateIdentifiers.Count > 0);
        }

        #endregion
    }
}
