namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Boundary enforcement configuration materialized into a dock's .armada directory
    /// as <c>.armada/boundary.json</c>. The pre-commit and pre-push hooks installed by
    /// <see cref="DockService"/> read this file so they enforce the same rules as the
    /// server-side <see cref="DockBoundaryScanner"/>.
    /// </summary>
    public sealed class DockBoundaryConfig
    {
        #region Public-Members

        /// <summary>
        /// Protected glob patterns (built-in Armada paths plus vessel-specific additions).
        /// A commit or push touching any of these paths is blocked.
        /// </summary>
        [JsonPropertyName("protectedPaths")]
        public List<string> ProtectedPaths
        {
            get => _ProtectedPaths;
            set => _ProtectedPaths = value ?? new List<string>();
        }

        /// <summary>
        /// CORE_RULE_5 secret regex patterns matched against added lines in staged diffs.
        /// Matched pattern label names appear in block messages; matched bytes are never printed.
        /// </summary>
        [JsonPropertyName("secretPatterns")]
        public List<string> SecretPatterns
        {
            get => _SecretPatterns;
            set => _SecretPatterns = value ?? new List<string>();
        }

        /// <summary>
        /// Private-identifier regex patterns for repositories classified as public.
        /// Empty for non-public repositories. The hook checks each added diff line against
        /// these patterns and blocks the commit or push on any match.
        /// </summary>
        [JsonPropertyName("privateIdentifiers")]
        public List<string> PrivateIdentifiers
        {
            get => _PrivateIdentifiers;
            set => _PrivateIdentifiers = value ?? new List<string>();
        }

        #endregion

        #region Private-Members

        private List<string> _ProtectedPaths = new List<string>();
        private List<string> _SecretPatterns = new List<string>();
        private List<string> _PrivateIdentifiers = new List<string>();

        #endregion
    }
}
