namespace Armada.Core.Memory
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Extracts <see cref="MemoryAnchor"/> from accepted reflection memory note text.
    /// Scans for embedded mission IDs and file-path-like references without requiring
    /// graph sidecars; anchors degrade gracefully to path/mission-only.
    /// </summary>
    public static class MemoryAnchorExtractor
    {
        #region Private-Members

        private static readonly Regex _MissionIdPattern =
            new Regex(@"\bmsn_[a-z0-9_]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _FilePathPattern =
            new Regex(@"\b(?:[a-zA-Z0-9_.-]+/)+[a-zA-Z0-9_.-]+\b", RegexOptions.Compiled);

        private static readonly HashSet<string> _CodeExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "cs", "ts", "tsx", "js", "jsx", "py", "go", "rs", "java", "md",
                "json", "yaml", "yml", "xml", "toml", "csproj", "sln", "fs",
                "cpp", "h", "c", "rb", "sh", "bat", "ps1", "sql",
            };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Extracts anchors from accepted note text and optional reflections-diff JSON.
        /// Always includes the MemoryConsolidator mission ID as a source anchor.
        /// </summary>
        /// <param name="acceptedContent">Markdown text written to the learned playbook.</param>
        /// <param name="diffText">Raw reflections-diff JSON body; null for editsMarkdown path.</param>
        /// <param name="isEditsOverride">True when content came from an operator editsMarkdown override.</param>
        /// <param name="sourceMissionId">MemoryConsolidator mission ID; always added as a source anchor.</param>
        /// <param name="modeWireString">Wire-string mode (consolidate, reorganize, pack-curate, etc.).</param>
        /// <returns>Populated <see cref="MemoryAnchor"/> with path/mission-only anchors at minimum.</returns>
        public static MemoryAnchor Extract(
            string acceptedContent,
            string? diffText,
            bool isEditsOverride,
            string sourceMissionId,
            string modeWireString)
        {
            MemoryAnchor anchor = new MemoryAnchor();
            anchor.Confidence = ParseConfidence(diffText, isEditsOverride);
            anchor.EvidenceKind = DeriveEvidenceKind(isEditsOverride, modeWireString);

            HashSet<string> missionIds = new HashSet<string>(StringComparer.Ordinal);
            if (!String.IsNullOrEmpty(sourceMissionId))
                missionIds.Add(sourceMissionId);

            if (!String.IsNullOrWhiteSpace(acceptedContent))
            {
                foreach (Match m in _MissionIdPattern.Matches(acceptedContent))
                    missionIds.Add(m.Value);
            }

            anchor.SourceMissionIds.AddRange(missionIds);

            if (!String.IsNullOrWhiteSpace(acceptedContent))
            {
                HashSet<string> paths = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match m in _FilePathPattern.Matches(acceptedContent))
                {
                    string candidate = m.Value;
                    if (_CodeExtensions.Contains(GetExtension(candidate)))
                        paths.Add(candidate);
                }

                anchor.FilePaths.AddRange(paths);
            }

            return anchor;
        }

        #endregion

        #region Private-Methods

        private static string ParseConfidence(string? diffText, bool isEditsOverride)
        {
            if (isEditsOverride || String.IsNullOrWhiteSpace(diffText))
                return "high";

            try
            {
                JsonDocument doc = JsonDocument.Parse(diffText!);
                if (doc.RootElement.TryGetProperty("evidenceConfidence", out JsonElement val)
                    && val.ValueKind == JsonValueKind.String)
                {
                    string? raw = val.GetString();
                    if (!String.IsNullOrEmpty(raw))
                        return raw!.ToLowerInvariant();
                }
            }
            catch (JsonException)
            {
            }

            return "high";
        }

        private static string DeriveEvidenceKind(bool isEditsOverride, string modeWireString)
        {
            if (isEditsOverride)
                return "edits";

            if (String.Equals(modeWireString, "pack-curate", StringComparison.OrdinalIgnoreCase))
                return "pack_curate";

            if (String.Equals(modeWireString, "persona-curate", StringComparison.OrdinalIgnoreCase)
                || String.Equals(modeWireString, "captain-curate", StringComparison.OrdinalIgnoreCase))
                return "identity_curate";

            if (String.Equals(modeWireString, "fleet-curate", StringComparison.OrdinalIgnoreCase))
                return "fleet_curate";

            return "verbatim";
        }

        private static string GetExtension(string path)
        {
            int dot = path.LastIndexOf('.');
            if (dot < 0 || dot == path.Length - 1)
                return "";

            return path.Substring(dot + 1);
        }

        #endregion
    }
}
