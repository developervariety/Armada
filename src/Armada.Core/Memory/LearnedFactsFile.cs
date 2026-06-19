namespace Armada.Core.Memory
{
    using System;
    using System.IO;
    using System.Text;
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
        /// Marker captains must emit to propose a change to the protected learned-facts file
        /// instead of editing it directly.
        /// </summary>
        public const string ProposalMarker = "[LEARNED-FACT-PROPOSAL]";

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

        /// <summary>
        /// Appends or merges a learned fact into the canonical learned-facts file.
        /// The file is written with LF line endings and UTF-8 without a BOM.
        /// </summary>
        /// <param name="repoRoot">Repository root directory.</param>
        /// <param name="contentToApply">Learned-fact markdown to append or merge.</param>
        /// <returns>Result indicating success or the reason the land failed.</returns>
        public static async Task<LearnedFactsFileApplyResult> ApplyAsync(string repoRoot, string contentToApply)
        {
            if (String.IsNullOrWhiteSpace(repoRoot))
            {
                return new LearnedFactsFileApplyResult
                {
                    Success = false,
                    Error = "repo_root_missing"
                };
            }

            if (String.IsNullOrWhiteSpace(contentToApply))
            {
                return new LearnedFactsFileApplyResult
                {
                    Success = false,
                    Error = "content_missing"
                };
            }

            try
            {
                if (!Directory.Exists(repoRoot))
                {
                    return new LearnedFactsFileApplyResult
                    {
                        Success = false,
                        Error = "repo_root_not_found"
                    };
                }

                string dir = Path.Combine(repoRoot, ".armada");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "LEARNED.md");

                string? existing = await ReadAsync(repoRoot).ConfigureAwait(false);
                string normalizedNew = NormalizeLineEndings(contentToApply).Trim();

                string finalContent;
                if (String.IsNullOrEmpty(existing))
                {
                    finalContent = normalizedNew;
                }
                else
                {
                    string normalizedExisting = NormalizeLineEndings(existing).Trim();
                    if (normalizedExisting.Contains(normalizedNew, StringComparison.Ordinal))
                    {
                        return new LearnedFactsFileApplyResult { Success = true };
                    }

                    finalContent = normalizedExisting + "\n\n" + normalizedNew;
                }

                finalContent = NormalizeLineEndings(finalContent);
                await File.WriteAllTextAsync(path, finalContent, new UTF8Encoding(false)).ConfigureAwait(false);
                return new LearnedFactsFileApplyResult { Success = true };
            }
            catch (Exception ex)
            {
                return new LearnedFactsFileApplyResult
                {
                    Success = false,
                    Error = "apply_failed: " + ex.Message
                };
            }
        }

        #endregion

        #region Private-Methods

        private static string NormalizeLineEndings(string content)
        {
            if (String.IsNullOrEmpty(content))
                return content;

            return content.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        #endregion
    }

    /// <summary>
    /// Result of applying a learned fact to the canonical per-vessel learned-facts file.
    /// </summary>
    public sealed class LearnedFactsFileApplyResult
    {
        /// <summary>
        /// Gets or sets a value indicating whether the file land succeeded.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets an error code or message when the land failed.
        /// </summary>
        public string? Error { get; set; }
    }
}
