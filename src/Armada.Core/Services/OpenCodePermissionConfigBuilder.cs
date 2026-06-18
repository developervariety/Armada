namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Pure, filesystem-free builder that produces the OpenCode
    /// <c>opencode.json</c> permission document granting the captain
    /// external-directory access. The emitted document sets
    /// <c>permission.external_directory</c> to the bare string <c>"allow"</c>.
    /// </summary>
    /// <remarks>
    /// OpenCode (1.17.x non-interactive <c>opencode run</c>) auto-loads an
    /// <c>opencode.json</c> from the project root. When an agent touches a path
    /// outside the project directory it logs
    /// <c>! permission requested: external_directory (&lt;path&gt;); auto-rejecting</c>
    /// and, with no TTY, refuses the access.
    ///
    /// The earlier shape emitted a PATH-KEYED glob map
    /// (<c>{"&lt;root&gt;":"allow","&lt;root&gt;/**":"allow"}</c>). That shape is
    /// broken on Windows: opencode's glob/path matcher for
    /// <c>external_directory</c> mismatches backslash vs forward-slash,
    /// drive-letter, and absolute-vs-relative forms (sst/opencode issues #11042,
    /// #7279, #20045), so the glob never matched the dock's Windows path,
    /// <c>external_directory</c> stayed at its <c>"ask"</c> default, and the
    /// captain was auto-rejected.
    ///
    /// The fix emits the BARE-STRING form. In opencode's permission schema a rule
    /// value is <c>Union[Action, Record&lt;string, Action&gt;]</c>; a bare action
    /// string is normalized internally to <c>{"*": action}</c>
    /// (packages/core/src/v1/config/permission.ts <c>normalizeInput</c>). The bare
    /// string has NO per-path glob to mis-match, so it is robust on Windows.
    ///
    /// Per-path scoping is NOT achievable on opencode 1.17.x Windows given the
    /// upstream glob bug. This REPLACES the prior 'reasonable-trust path scoping'
    /// intent (unachievable) with a deliberate, documented trade-off: confinement
    /// rests on opencode's CWD-confinement (the dock worktree is the captain's
    /// cwd) plus these being Armada-provisioned docks on our own trusted host.
    /// <c>grantedRoots</c> is retained on the signature for the call site in
    /// <c>DockService</c> but no longer drives a path map.
    /// </remarks>
    public sealed class OpenCodePermissionConfigBuilder
    {
        #region Private-Members

        private const string _SchemaUrl = "https://opencode.ai/config.json";
        private const string _AllowValue = "allow";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Builds the serialized <c>opencode.json</c> permission document granting
        /// external-directory access via the bare-string <c>"allow"</c> form
        /// (normalized by opencode to <c>{"*":"allow"}</c>). The method is pure: it
        /// performs no filesystem or process access. The output is valid JSON with
        /// LF line endings and always carries the <c>$schema</c> reference.
        /// </summary>
        /// <param name="grantedRoots">Retained for call-site compatibility; no
        /// longer drives a path map (see remarks). May be null.</param>
        /// <returns>The serialized <c>opencode.json</c> content as a JSON string.</returns>
        public static string Build(IReadOnlyList<string> grantedRoots)
        {
            OpenCodeConfigDocument document = new OpenCodeConfigDocument();

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

        #region Private-Types

        /// <summary>
        /// Strongly-typed root model for the OpenCode <c>opencode.json</c> document.
        /// </summary>
        private sealed class OpenCodeConfigDocument
        {
            /// <summary>JSON schema reference understood by OpenCode tooling.</summary>
            [JsonPropertyName("$schema")]
            public string Schema { get; set; } = _SchemaUrl;

            /// <summary>Permission section carrying the external-directory grant.</summary>
            [JsonPropertyName("permission")]
            public OpenCodePermissionSection Permission { get; set; } = new OpenCodePermissionSection();
        }

        /// <summary>
        /// Permission section of the OpenCode document. Carries the bare-string
        /// external-directory grant.
        /// </summary>
        private sealed class OpenCodePermissionSection
        {
            /// <summary>
            /// External-directory permission as a bare action string. <c>"allow"</c>
            /// is normalized by opencode to <c>{"*":"allow"}</c>, dodging the broken
            /// Windows path-glob matcher.
            /// </summary>
            [JsonPropertyName("external_directory")]
            public string ExternalDirectory { get; set; } = _AllowValue;
        }

        #endregion
    }
}
