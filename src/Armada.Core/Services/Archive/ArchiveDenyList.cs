namespace Armada.Core.Services.Archive
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Files the archive never uploads: provider login files, credential and secret files, the
    /// settings file and its backups, environment files, key material and the admiral's auth header.
    /// A denied file is never uploaded and never freed by the archive, so it stays only on the host.
    /// Matching is by file name, plus any folder on the path named <c>secrets</c> or <c>.ssh</c>.
    /// </summary>
    public static class ArchiveDenyList
    {
        #region Public-Members

        /// <summary>
        /// Built-in file-name patterns. <c>*</c> matches any run of characters; matching ignores case.
        /// </summary>
        public static readonly IReadOnlyList<string> BuiltInPatterns = new[]
        {
            "auth.json",
            "*credential*",
            "*secret*",
            "settings.json",
            "settings.json.*",
            "settings.*.json",
            "settings-*.json",
            ".env",
            ".env.*",
            "*.env",
            "git-credentials",
            ".git-credentials",
            "mcp-auth-header",
            "*.pem",
            "*.key",
            "*.p12",
            "*.pfx",
            "id_rsa*",
            "id_ed25519*",
            "id_ecdsa*"
        };

        /// <summary>
        /// Folder names that deny every file below them.
        /// </summary>
        public static readonly IReadOnlyList<string> DeniedFolderNames = new[] { "secrets", ".ssh" };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a file may never be uploaded.
        /// </summary>
        /// <param name="path">File path, absolute or relative.</param>
        /// <param name="extraPatterns">Configured extra patterns.</param>
        /// <returns>True when the file is denied.</returns>
        public static bool IsDenied(string path, IEnumerable<string>? extraPatterns = null)
        {
            if (String.IsNullOrWhiteSpace(path)) return true;
            string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return true;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (DeniedFolderNames.Any(name => String.Equals(name, parts[i], StringComparison.OrdinalIgnoreCase))) return true;
            }

            string fileName = parts[parts.Length - 1];
            IEnumerable<string> patterns = extraPatterns == null ? BuiltInPatterns : BuiltInPatterns.Concat(extraPatterns.Where(p => !String.IsNullOrWhiteSpace(p)));
            return patterns.Any(pattern => Matches(pattern.Trim(), fileName));
        }

        #endregion

        #region Private-Methods

        private static bool Matches(string pattern, string fileName)
        {
            string regex = "^" + String.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$";
            return Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        #endregion
    }
}
