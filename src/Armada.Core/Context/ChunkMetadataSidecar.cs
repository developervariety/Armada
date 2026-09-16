namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using SyslogLogging;

    /// <summary>
    /// The optional chunk-metadata sidecar: a repository-versioned enrichment file that maps a chunk
    /// id to hand-authored retrieval metadata. It lets the owner give a chunk a clear summary, a
    /// concrete read-when trigger, an applies_to scope, and a must_retrieve safety domain WITHOUT
    /// editing AI-Memory. The memory files stay the sole durable memory source and are never touched
    /// by the index layer; the enrichment lives in the Armada repository beside the docs, so it is
    /// versioned with the code that reads it.
    ///
    /// The generator MERGES this over the auto-derived metadata: a sidecar field wins where it is
    /// present, and the auto-derived value fills every gap the sidecar leaves. A chunk with no
    /// sidecar entry keeps its auto-derived metadata unchanged.
    ///
    /// The file is JSON: a top-level object with a <c>chunks</c> map from chunk id (the manifest
    /// <c>id</c>, which equals the chunk topic) to an override object. It never carries the chunk
    /// body: it is metadata ABOUT a chunk, never a second copy of the rule. The sidecar carries no
    /// <c>tier</c>: the tier is the owner allowlist in <see cref="ContextTierConfig"/>, and metadata
    /// never promotes or demotes a chunk.
    ///
    /// Loading never throws: a missing or malformed file yields an empty sidecar and a logged
    /// warning, so the index still generates. A missing sidecar is the normal state, not an error.
    /// </summary>
    public sealed class ChunkMetadataSidecar
    {
        #region Public-Members

        /// <summary>The default sidecar file name.</summary>
        public const string DefaultFileName = "chunk-metadata.json";

        /// <summary>The default sidecar directory, relative to the docs root.</summary>
        public const string DefaultRelativeDirectory = "context-index";

        /// <summary>The number of chunk ids the sidecar carries an override for.</summary>
        public int Count => _ById.Count;

        #endregion

        #region Private-Members

        private const string _Header = "[ChunkMetadataSidecar] ";

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private readonly Dictionary<string, ChunkMetadataOverride> _ById;

        #endregion

        #region Constructors-and-Factories

        private ChunkMetadataSidecar(Dictionary<string, ChunkMetadataOverride> byId)
        {
            _ById = byId;
        }

        /// <summary>An empty sidecar: it overrides nothing, so every chunk keeps its auto-derived metadata.</summary>
        public static ChunkMetadataSidecar Empty =>
            new ChunkMetadataSidecar(new Dictionary<string, ChunkMetadataOverride>(StringComparer.Ordinal));

        /// <summary>
        /// Resolve the sidecar path. When <paramref name="explicitPath"/> is given it wins; otherwise
        /// the sidecar is the default file under <paramref name="docsRoot"/>, which is where it lives
        /// in the repository. Returns null when neither yields a path.
        /// </summary>
        public static string? ResolvePath(string? explicitPath, string? docsRoot)
        {
            if (!String.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
            if (!String.IsNullOrWhiteSpace(docsRoot))
                return Path.Combine(docsRoot!, DefaultRelativeDirectory, DefaultFileName);
            return null;
        }

        /// <summary>
        /// Load the sidecar from a file path. Never throws: a null or missing path, or a malformed
        /// file, yields <see cref="Empty"/> (with a logged warning for a malformed file).
        /// </summary>
        public static ChunkMetadataSidecar Load(string? path, LoggingModule? logging = null)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Empty;

            try
            {
                string raw = File.ReadAllText(path);
                return Parse(raw);
            }
            catch (Exception ex)
            {
                logging?.Warn(_Header + "load failed, ignoring sidecar: " + ex.Message);
                return Empty;
            }
        }

        /// <summary>
        /// Parse the sidecar from its JSON text. Never throws: malformed JSON yields <see cref="Empty"/>.
        /// A null override entry, or an entry whose id is blank, is skipped.
        /// </summary>
        public static ChunkMetadataSidecar Parse(string? json)
        {
            Dictionary<string, ChunkMetadataOverride> byId =
                new Dictionary<string, ChunkMetadataOverride>(StringComparer.Ordinal);

            if (String.IsNullOrWhiteSpace(json)) return new ChunkMetadataSidecar(byId);

            try
            {
                ChunkMetadataSidecarDocument? doc =
                    JsonSerializer.Deserialize<ChunkMetadataSidecarDocument>(json!, _JsonOptions);

                if (doc?.Chunks != null)
                {
                    foreach (KeyValuePair<string, ChunkMetadataOverride?> pair in doc.Chunks)
                    {
                        if (String.IsNullOrWhiteSpace(pair.Key) || pair.Value == null) continue;
                        byId[pair.Key] = pair.Value;
                    }
                }
            }
            catch
            {
                // A malformed sidecar is not partial metadata; it is no sidecar.
                return Empty;
            }

            return new ChunkMetadataSidecar(byId);
        }

        #endregion

        #region Public-Methods

        /// <summary>Get the override for a chunk id, or false when the sidecar carries none.</summary>
        public bool TryGet(string chunkId, out ChunkMetadataOverride ov)
        {
            if (!String.IsNullOrEmpty(chunkId) && _ById.TryGetValue(chunkId, out ChunkMetadataOverride? found) && found != null)
            {
                ov = found;
                return true;
            }
            ov = null!;
            return false;
        }

        #endregion
    }

    /// <summary>
    /// One chunk's sidecar override. Every field is optional: a null or empty field is not an
    /// override, so the generator keeps the auto-derived value for it. There is deliberately no
    /// <c>tier</c> field; the tier is owned by <see cref="ContextTierConfig"/> and metadata never
    /// changes it.
    /// </summary>
    public sealed class ChunkMetadataOverride
    {
        /// <summary>The one-line summary to use instead of the auto-derived one. Null or blank means no override.</summary>
        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        /// <summary>The concrete read-when trigger to use instead of the auto-derived one. Null or blank means no override.</summary>
        [JsonPropertyName("read_when")]
        public string? ReadWhen { get; set; }

        /// <summary>The applies_to scope to use instead of the auto-derived one. Null or empty means no override.</summary>
        [JsonPropertyName("applies_to")]
        public List<string>? AppliesTo { get; set; }

        /// <summary>The must_retrieve safety domains to set on this leaf. Null or empty means no override.</summary>
        [JsonPropertyName("must_retrieve")]
        public List<string>? MustRetrieve { get; set; }
    }

    /// <summary>The deserialization shape of the sidecar file: a version and the id-to-override map.</summary>
    internal sealed class ChunkMetadataSidecarDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("chunks")]
        public Dictionary<string, ChunkMetadataOverride?>? Chunks { get; set; }
    }
}
