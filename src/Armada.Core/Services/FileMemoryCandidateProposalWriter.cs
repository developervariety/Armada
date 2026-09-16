namespace Armada.Core.Services
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Writes a durable-lesson proposal as a Markdown file under
    /// <c>&lt;aiMemoryRoot&gt;/corpus/memory-candidates/</c>. The folder is fixed and outside
    /// <c>shared/</c> and <c>repos/</c>, so the model can never write into a loaded memory folder;
    /// a resolved path that would escape the proposals folder is refused. When no AI-Memory root is
    /// configured, writing is skipped and the nomination is recorded as an event only. Writing never
    /// throws.
    /// </summary>
    public sealed class FileMemoryCandidateProposalWriter : IMemoryCandidateProposalWriter
    {
        #region Public-Members

        /// <summary>
        /// The fixed proposals folder relative to the AI-Memory root. Deliberately not under
        /// <c>shared/</c> or <c>repos/</c>: those are loaded by every runtime, and the model never
        /// writes loaded memory.
        /// </summary>
        public const string ProposalsRelativePath = "corpus/memory-candidates";

        #endregion

        #region Private-Members

        private const string _Header = "[FileMemoryCandidateProposalWriter] ";
        private static readonly Regex _SlugPattern = new Regex(@"[^a-z0-9]+", RegexOptions.Compiled);

        private readonly string? _AiMemoryRoot;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a file proposal writer.
        /// </summary>
        /// <param name="aiMemoryRoot">The AI-Memory root path on this host, or null when unset.</param>
        /// <param name="logging">Logging module.</param>
        public FileMemoryCandidateProposalWriter(string? aiMemoryRoot, LoggingModule logging)
        {
            _AiMemoryRoot = String.IsNullOrWhiteSpace(aiMemoryRoot) ? null : aiMemoryRoot.Trim();
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Write the proposal under the proposals folder. Returns the path, or null when no root is
        /// configured or the write failed. Never throws.
        /// </summary>
        /// <param name="proposal">The proposal to write; its text is already redacted.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The written path, or null.</returns>
        public async Task<string?> WriteAsync(MemoryCandidateProposal proposal, CancellationToken token)
        {
            if (proposal == null) return null;
            if (_AiMemoryRoot == null) return null;

            try
            {
                string folder = Path.GetFullPath(Path.Combine(_AiMemoryRoot, ProposalsRelativePath));
                string fileName = BuildFileName(proposal);
                string fullPath = Path.GetFullPath(Path.Combine(folder, fileName));

                // Defence in depth: the resolved file must stay inside the proposals folder, and the
                // proposals folder must never resolve under a loaded memory folder.
                if (!IsInside(folder, fullPath) || IsUnderLoadedMemory(folder))
                {
                    _Logging.Warn(_Header + "refusing proposal path outside the proposals folder: " + fullPath);
                    return null;
                }

                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(fullPath, BuildMarkdown(proposal), token).ConfigureAwait(false);
                return fullPath;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to write proposal: " + ex.Message);
                return null;
            }
        }

        #endregion

        #region Private-Methods

        private static string BuildFileName(MemoryCandidateProposal proposal)
        {
            string slug = _SlugPattern.Replace((proposal.Title ?? "").ToLowerInvariant(), "-").Trim('-');
            if (slug.Length > 48) slug = slug.Substring(0, 48).Trim('-');
            if (String.IsNullOrEmpty(slug)) slug = "candidate";

            // The group key gives a stable identity so a recurring group re-writes the same file
            // rather than accumulating duplicates. The key is hashed, so no id reaches the filename.
            string hash = ShortHash(proposal.GroupKey);
            return slug + "-" + hash + ".md";
        }

        private static string ShortHash(string? value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? String.Empty);
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 8);
        }

        private static bool IsInside(string folder, string candidate)
        {
            string normalizedFolder = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
            return candidate.StartsWith(normalizedFolder, StringComparison.Ordinal);
        }

        private static bool IsUnderLoadedMemory(string folder)
        {
            string normalized = folder.Replace('\\', '/');
            return normalized.Contains("/shared/", StringComparison.Ordinal)
                || normalized.Contains("/repos/", StringComparison.Ordinal)
                || normalized.EndsWith("/shared", StringComparison.Ordinal)
                || normalized.EndsWith("/repos", StringComparison.Ordinal);
        }

        private static string BuildMarkdown(MemoryCandidateProposal proposal)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("# Memory candidate: ").Append(Sanitize(proposal.Title)).Append('\n').Append('\n');
            sb.Append("> Nominated by the D18 `memory_candidate` decision from a papercut group. A ")
              .Append("candidate for the owner to promote or discard; it is not memory until the owner ")
              .Append("writes it. All text below is redacted.").Append('\n').Append('\n');
            sb.Append("- Scope: `").Append(Sanitize(proposal.Scope)).Append("`\n");
            sb.Append("- Category: `").Append(Sanitize(proposal.Category)).Append("`\n");
            sb.Append("- Reports: ").Append(proposal.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("- Distinct captains: ").Append(proposal.DistinctCaptainCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("- Vessel context: ").Append(Sanitize(proposal.Vessels)).Append('\n');
            sb.Append("- Durable-lesson score: ").Append(proposal.DurableLesson.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("- Created (UTC): ").Append(proposal.CreatedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append('\n').Append("## Detail\n\n").Append(Sanitize(proposal.Detail)).Append('\n');
            return sb.ToString();
        }

        private static string Sanitize(string? text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "(none)";
            return text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        #endregion
    }
}
