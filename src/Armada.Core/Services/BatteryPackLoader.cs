namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Models;

    /// <summary>
    /// Reads battery packs from disk. A pack is a directory holding a battery-pack.json manifest; malformed packs are
    /// returned with their errors instead of throwing, so the pack list can show what is wrong.
    /// </summary>
    public static class BatteryPackLoader
    {
        #region Public-Members

        /// <summary>
        /// File name of a pack manifest.
        /// </summary>
        public const string ManifestFileName = "battery-pack.json";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _Json = CreateJsonOptions();

        #endregion

        #region Public-Methods

        /// <summary>
        /// Load every pack directly under a battery directory, ordered by identifier.
        /// </summary>
        /// <param name="batteryDirectory">Battery directory (for example the repository's battery folder).</param>
        /// <returns>The packs; empty when the directory does not exist.</returns>
        public static List<BatteryPack> LoadAll(string? batteryDirectory)
        {
            List<BatteryPack> packs = new List<BatteryPack>();
            if (String.IsNullOrWhiteSpace(batteryDirectory) || !Directory.Exists(batteryDirectory)) return packs;
            foreach (string directory in Directory.GetDirectories(batteryDirectory))
            {
                if (!File.Exists(Path.Combine(directory, ManifestFileName))) continue;
                packs.Add(Load(directory));
            }

            packs.Sort((a, b) => String.CompareOrdinal(a.Id, b.Id));
            return packs;
        }

        /// <summary>
        /// Load and validate one pack directory.
        /// </summary>
        /// <param name="packDirectory">Pack directory.</param>
        /// <returns>The pack, with errors when it is malformed.</returns>
        /// <exception cref="ArgumentNullException">Thrown when packDirectory is null or empty.</exception>
        public static BatteryPack Load(string packDirectory)
        {
            if (String.IsNullOrWhiteSpace(packDirectory)) throw new ArgumentNullException(nameof(packDirectory));
            string directory = Path.GetFullPath(packDirectory);
            string fallbackId = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath))
                return new BatteryPack(fallbackId, directory, null, null, new List<string> { "The pack has no " + ManifestFileName + "." });

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(manifestPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new BatteryPack(fallbackId, directory, null, null, new List<string> { "The manifest could not be read: " + ex.Message });
            }

            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            BatteryPackManifest? manifest;
            try
            {
                manifest = Parse(bytes);
            }
            catch (JsonException ex)
            {
                return new BatteryPack(fallbackId, directory, null, hash, new List<string> { "The manifest is not valid JSON: " + ex.Message });
            }

            if (manifest == null)
                return new BatteryPack(fallbackId, directory, null, hash, new List<string> { "The manifest is empty." });

            List<string> errors = BatteryPackValidator.Validate(manifest, directory);
            string id = !String.IsNullOrWhiteSpace(manifest.Id) ? manifest.Id!.Trim() : fallbackId;
            return new BatteryPack(id, directory, manifest, hash, errors);
        }

        /// <summary>
        /// Parse manifest bytes (UTF-8, with or without a byte order mark; comments and trailing commas allowed).
        /// </summary>
        /// <param name="bytes">Manifest bytes.</param>
        /// <returns>The manifest, or null for a JSON null.</returns>
        /// <exception cref="ArgumentNullException">Thrown when bytes is null.</exception>
        /// <exception cref="JsonException">Thrown when the bytes are not a valid manifest document.</exception>
        public static BatteryPackManifest? Parse(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            ReadOnlySpan<byte> span = bytes;
            if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF) span = span.Slice(3);
            return JsonSerializer.Deserialize<BatteryPackManifest>(span, _Json);
        }

        #endregion

        #region Private-Methods

        private static JsonSerializerOptions CreateJsonOptions()
        {
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.PropertyNameCaseInsensitive = true;
            options.ReadCommentHandling = JsonCommentHandling.Skip;
            options.AllowTrailingCommas = true;
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        #endregion
    }
}
