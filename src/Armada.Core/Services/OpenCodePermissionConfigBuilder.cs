namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Pure, filesystem-free builder that turns a set of granted directory roots
    /// into the OpenCode <c>opencode.json</c> permission document. The emitted
    /// document expresses reasonable-trust access (read plus build/execute) to
    /// exactly the supplied roots and no broader scope.
    /// </summary>
    /// <remarks>
    /// OpenCode (1.17.x non-interactive <c>opencode run</c>) auto-loads an
    /// <c>opencode.json</c> from the project root. When an agent touches a path
    /// outside the project directory it logs
    /// <c>! permission requested: external_directory (&lt;path&gt;); auto-rejecting</c>
    /// and refuses the access. The permission document grants that access ahead of
    /// time: under the <c>permission.external_directory</c> map, a directory entry
    /// set to <c>allow</c> tells OpenCode the agent may read and operate within that
    /// subtree without prompting. The exact key name could not be confirmed against
    /// a bundled OpenCode binary or docs in this worktree, so this encodes the
    /// best-supported schema observed from the rejection message
    /// (<c>external_directory</c>) and the standard <c>ask|allow|deny</c> value set.
    /// Each granted root is emitted both as the literal directory and as a
    /// <c>&lt;root&gt;/**</c> subtree glob so OpenCode matches both the directory
    /// itself and any path beneath it. A blanket whole-filesystem grant (a bare
    /// <c>*</c>, <c>**</c>, the filesystem root, or a drive root such as
    /// <c>C:</c>) is deliberately never emitted -- access is limited to the
    /// supplied roots.
    /// </remarks>
    public sealed class OpenCodePermissionConfigBuilder
    {
        #region Private-Members

        private const string _SchemaUrl = "https://opencode.ai/config.json";
        private const string _AllowValue = "allow";
        private const string _SubtreeGlobSuffix = "/**";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Builds the serialized <c>opencode.json</c> permission document granting
        /// reasonable-trust (read plus build/execute) access to the supplied roots
        /// and no broader scope. The method is pure: it performs no filesystem or
        /// process access. Null, empty, and whitespace roots are skipped; roots are
        /// normalized (separators unified, trailing separators trimmed) and
        /// deduplicated; blanket whole-filesystem or drive-root entries are dropped.
        /// Output ordering is deterministic and the result is valid JSON with LF
        /// line endings.
        /// </summary>
        /// <param name="grantedRoots">Directory roots to grant access to. A null
        /// list (or one with no usable entries) yields a valid document with no
        /// extra grants rather than throwing.</param>
        /// <returns>The serialized <c>opencode.json</c> content as a JSON string.</returns>
        public static string Build(IReadOnlyList<string> grantedRoots)
        {
            OpenCodeConfigDocument document = new OpenCodeConfigDocument();

            if (grantedRoots is not null)
            {
                SortedSet<string> normalizedRoots = new SortedSet<string>(StringComparer.Ordinal);
                foreach (string rawRoot in grantedRoots)
                {
                    string? normalized = NormalizeRoot(rawRoot);
                    if (normalized is not null) normalizedRoots.Add(normalized);
                }

                foreach (string root in normalizedRoots)
                {
                    document.Permission.ExternalDirectory[root] = _AllowValue;
                    document.Permission.ExternalDirectory[root + _SubtreeGlobSuffix] = _AllowValue;
                }
            }

            JsonSerializerOptions options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            string serialized = JsonSerializer.Serialize(document, options);

            // Force LF line endings regardless of the serializer's platform default
            // so the emitted opencode.json is byte-stable across operating systems.
            return serialized.Replace("\r\n", "\n");
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Normalizes a raw root into a canonical comparable form, or null when the
        /// root is empty or would amount to a blanket whole-filesystem grant.
        /// </summary>
        private static string? NormalizeRoot(string rawRoot)
        {
            if (String.IsNullOrWhiteSpace(rawRoot)) return null;

            string trimmed = rawRoot.Trim().Replace('\\', '/');

            // Strip trailing separators so the subtree glob appends cleanly and so
            // "C:/foo/" and "C:/foo" collapse to a single deduplicated root.
            while (trimmed.Length > 1 && trimmed.EndsWith("/", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            if (IsBlanketRoot(trimmed)) return null;

            return trimmed;
        }

        /// <summary>
        /// Returns true when the normalized root would grant access to the entire
        /// filesystem or a whole drive, which must never be emitted.
        /// </summary>
        private static bool IsBlanketRoot(string root)
        {
            if (root.Length == 0) return true;
            if (root == "*" || root == "**" || root == "/" || root == "/**") return true;

            // Drive root only (e.g. "C:") -- after trailing-separator stripping a
            // bare drive specifier grants the whole drive, which is too broad.
            if (root.Length == 2 && root[1] == ':' && Char.IsLetter(root[0])) return true;

            return false;
        }

        #endregion

        #region Private-Types

        /// <summary>
        /// Strongly-typed root model for the OpenCode <c>opencode.json</c> document.
        /// </summary>
        private sealed class OpenCodeConfigDocument
        {
            /// <summary>JSON schema reference understood by OpenCode tooling.</summary>
            [JsonPropertyName("$schema")]
            public string Schema { get; set; } = _SchemaUrl;

            /// <summary>Permission section carrying the external-directory grants.</summary>
            [JsonPropertyName("permission")]
            public OpenCodePermissionSection Permission { get; set; } = new OpenCodePermissionSection();
        }

        /// <summary>
        /// Permission section of the OpenCode document. Holds the external-directory
        /// allow map keyed by directory or subtree glob.
        /// </summary>
        private sealed class OpenCodePermissionSection
        {
            /// <summary>
            /// Map of granted directory (or subtree glob) to permission value. A
            /// value of <c>allow</c> grants reasonable-trust access to that path.
            /// </summary>
            [JsonPropertyName("external_directory")]
            public Dictionary<string, string> ExternalDirectory { get; set; } = new Dictionary<string, string>();
        }

        #endregion
    }
}
