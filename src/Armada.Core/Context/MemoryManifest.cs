namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;

    /// <summary>
    /// The manifest written beside the staged memory files: each file's path, bytes and SHA-256, and,
    /// when the files are the voyage's pinned memory, which voyage and which mission first staged them.
    /// Every stage of a voyage receives the same files, so the manifest lets a later stage verify its
    /// bytes against an earlier stage's report without re-deriving them.
    /// </summary>
    public sealed class MemoryManifest
    {
        #region Public-Members

        /// <summary>File name of the manifest inside the memory folder.</summary>
        public const string FileName = "manifest.json";

        /// <summary>Dock-relative path of the manifest.</summary>
        public const string RelativePath = ContextBriefRenderer.MemoryFolder + "/" + FileName;

        /// <summary>The voyage whose pinned memory the files are, or null when they were not pinned.</summary>
        public string? VoyageId { get; set; }

        /// <summary>The mission whose brief first staged the files, or null when they were not pinned.</summary>
        public string? PinnedByMissionId { get; set; }

        /// <summary>When the files were first staged, or null when they were not pinned.</summary>
        public DateTime? PinnedUtc { get; set; }

        /// <summary>The staged memory files, in reading order.</summary>
        public List<MemoryManifestEntry> Files { get; set; } = new List<MemoryManifestEntry>();

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the manifest for a memory delivery.
        /// </summary>
        /// <param name="delivery">The delivery whose files are staged.</param>
        /// <returns>The manifest.</returns>
        public static MemoryManifest Build(ContextBriefDelivery delivery)
        {
            if (delivery == null) throw new ArgumentNullException(nameof(delivery));
            MemoryManifest manifest = new MemoryManifest
            {
                VoyageId = delivery.PinnedVoyageId,
                PinnedByMissionId = delivery.PinnedByMissionId,
                PinnedUtc = delivery.PinnedUtc
            };
            foreach (ContextBriefFile file in delivery.Files)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(file.Content ?? String.Empty);
                manifest.Files.Add(new MemoryManifestEntry
                {
                    Path = file.RelativePath,
                    Bytes = bytes.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
                });
            }
            return manifest;
        }

        /// <summary>
        /// Render the manifest as the file written beside the memory files. It is not a memory file: it
        /// is not listed as reading and carries no topics.
        /// </summary>
        /// <returns>The manifest file.</returns>
        public ContextBriefFile ToFile()
        {
            return new ContextBriefFile
            {
                RelativePath = RelativePath,
                Content = JsonSerializer.Serialize(this, _JsonOptions) + "\n",
                ReadFirst = false
            };
        }

        /// <summary>
        /// Parse a manifest file's content.
        /// </summary>
        /// <param name="json">Manifest JSON.</param>
        /// <returns>The manifest, or null when the content is empty.</returns>
        public static MemoryManifest? Parse(string json)
        {
            if (String.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<MemoryManifest>(json, _JsonOptions);
        }

        #endregion
    }
}
