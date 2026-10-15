namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// The files in a repository's default branch, as far as Armada needs them to recognize its toolchain: every path
    /// (forward slashes, relative to the root), and the names at the root.
    /// </summary>
    public class RepositoryLayout
    {
        #region Public-Members

        /// <summary>
        /// Every file path, relative to the repository root, with forward slashes. Never null.
        /// </summary>
        public List<string> Paths { get; } = new List<string>();

        /// <summary>
        /// Where the paths came from (for example "the bare repository" or "a shallow clone"), for the profile's description.
        /// </summary>
        public string Source { get; set; } = String.Empty;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate empty.
        /// </summary>
        public RepositoryLayout()
        {
        }

        /// <summary>
        /// Instantiate from paths.
        /// </summary>
        /// <param name="paths">Repository-relative paths (either slash direction).</param>
        /// <param name="source">Where the paths came from.</param>
        public RepositoryLayout(IEnumerable<string> paths, string source)
        {
            foreach (string path in paths ?? Enumerable.Empty<string>())
            {
                string normalized = (path ?? String.Empty).Trim().Replace('\\', '/').TrimStart('/');
                if (normalized.Length > 0) Paths.Add(normalized);
            }

            Source = source ?? String.Empty;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a file exists at the repository root (case-insensitive).
        /// </summary>
        /// <param name="name">File name.</param>
        /// <returns>True when present.</returns>
        public bool HasRootFile(string name)
        {
            return Paths.Any(p => p.IndexOf('/') < 0 && String.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The shallowest file with an extension (for example ".sln"), ties broken by name, or null.
        /// </summary>
        /// <param name="extension">Extension including the dot.</param>
        /// <returns>Path, or null.</returns>
        public string? Shallowest(string extension)
        {
            return Paths
                .Where(p => p.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && !IsIgnored(p))
                .OrderBy(p => p.Count(c => c == '/'))
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        #endregion

        #region Private-Methods

        private static bool IsIgnored(string path)
        {
            // Samples, vendored code, and build output are not the project the repository builds.
            string lower = path.ToLowerInvariant();
            return lower.Contains("/node_modules/") || lower.StartsWith("node_modules/", StringComparison.Ordinal)
                || lower.Contains("/bin/") || lower.Contains("/obj/") || lower.Contains("/archive/");
        }

        #endregion
    }
}
