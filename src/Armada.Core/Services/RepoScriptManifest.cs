namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The parts of a package.json or composer.json the ecosystem detector and command resolver read: the
    /// declared script names and the corepack package manager. Script values are never inspected.
    /// </summary>
    internal class RepoScriptManifest
    {
        /// <summary>
        /// Declared scripts keyed by name. Null when the manifest declares none.
        /// </summary>
        [JsonPropertyName("scripts")]
        public Dictionary<string, object?>? Scripts { get; set; } = null;

        /// <summary>
        /// The corepack "packageManager" field, for example "pnpm@9.1.0". Null when absent.
        /// </summary>
        [JsonPropertyName("packageManager")]
        public string? PackageManager { get; set; } = null;
    }
}
