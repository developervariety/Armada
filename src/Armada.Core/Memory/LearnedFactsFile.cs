namespace Armada.Core.Memory
{
    using System;
    using System.IO;
    using System.Threading.Tasks;

    /// <summary>
    /// Helpers for the canonical per-vessel learned-facts file stored in the repository.
    /// </summary>
    public static class LearnedFactsFile
    {
        #region Public-Members

        /// <summary>
        /// Relative path from the repository root to the canonical learned-facts file.
        /// </summary>
        public const string RelativePath = ".armada/LEARNED.md";

        /// <summary>
        /// Legacy empty-state content used before the in-dock discovery pointer was introduced.
        /// </summary>
        public const string LegacyTemplateContent = "# Vessel Learned Facts\n\nNo accepted reflection facts yet.";

        /// <summary>
        /// Current empty-state content that includes the canonical path and propose-not-edit pointer.
        /// </summary>
        public const string DefaultTemplateContent = "# Vessel Learned Facts\n\nNo accepted reflection facts yet.\n\nThe canonical source of truth for this vessel is `.armada/LEARNED.md` in the repository root.\nCaptains must PROPOSE changes via `[LEARNED-FACT-PROPOSAL]` and never edit that file directly.";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Reads the canonical learned-facts file from the repository root.
        /// Returns null when the file is missing or contains only the empty-state template.
        /// </summary>
        /// <param name="repoRoot">Repository root directory.</param>
        /// <returns>File content, or null when the file is absent or template-only.</returns>
        public static async Task<string?> ReadAsync(string repoRoot)
        {
            if (String.IsNullOrWhiteSpace(repoRoot))
                return null;

            string path = Path.Combine(repoRoot, RelativePath);
            if (!File.Exists(path))
                return null;

            string content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            string normalized = content.Trim();

            if (String.Equals(normalized, LegacyTemplateContent, StringComparison.Ordinal)
                || String.Equals(normalized, DefaultTemplateContent, StringComparison.Ordinal))
            {
                return null;
            }

            return content;
        }

        #endregion
    }
}
