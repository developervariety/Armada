namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// Configuration for captain context recall (<c>armada_search_context</c> and mission-token
    /// <c>search_memory</c>) and the flagged brief-slimming path. Recall is read-only: it returns
    /// already-sanitized AI-Memory and docs text, native facts and indexed code, and has no side effect
    /// on any Armada source record. One durable mission counter bounds the combined recall calls, with
    /// the code-index caller cap acting as a conservative ceiling while code indexing is enabled. These
    /// settings also set a per-call byte budget and can turn recall off without a code change. The
    /// brief-slimming flag replaces the full read-every-file AI-Memory brief section with the always-on
    /// core plus the mission's retrieved leaves; it defaults OFF so brief generation is unchanged until
    /// it is deliberately enabled.
    /// </summary>
    public class ContextRetrievalSettings
    {
        /// <summary>
        /// Whether captains may call context recall, and whether the slimmed brief points them at it.
        /// Default true: recall is read-only, makes no hosted egress, and returns only sanitized text.
        /// </summary>
        public bool FetchToolEnabled { get; set; } = true;

        /// <summary>
        /// Whether the captain brief's AI-Memory section is slimmed to the always-on core plus the
        /// mission's retrieved leaves, instead of instructing the captain to read every file under
        /// <c>shared/</c>. Default FALSE: while off, brief generation is byte-for-byte unchanged.
        /// Enabling it is a deliberate, separate step. When on and retrieval is unavailable or
        /// degraded, the brief falls back to the full memory section, never to fewer rules.
        /// </summary>
        public bool BriefSlimmingEnabled { get; set; } = false;

        /// <summary>
        /// The leaf byte budget for the slimmed brief section's ranked leaves. Core chunks and
        /// matching-domain must-retrieve safety leaves are exempt in the retrieval layer and are never
        /// counted against it, so a smaller budget trims only the ranked-relevance leaves.
        /// </summary>
        public int BriefLeafBudgetBytes
        {
            get => _BriefLeafBudgetBytes;
            set => _BriefLeafBudgetBytes = Math.Max(0, value);
        }

        /// <summary>
        /// The optional-result byte budget one recall call may return. Required core and matching-domain
        /// must-retrieve guidance is exempt and always returned whole, so this bounds only the ranked
        /// results of one call.
        /// </summary>
        public int MaxLeafBytesPerCall
        {
            get => _MaxLeafBytesPerCall;
            set => _MaxLeafBytesPerCall = Math.Max(0, value);
        }

        /// <summary>
        /// The maximum combined recall calls one mission may make across <c>armada_search_context</c>
        /// and mission-token <c>search_memory</c>. The code-index caller cap also caps this while code
        /// indexing is enabled. Usage is stored atomically on the mission and survives restart.
        /// </summary>
        public int MaxCallsPerMission
        {
            get => _MaxCallsPerMission;
            set => _MaxCallsPerMission = Math.Max(0, value);
        }

        /// <summary>
        /// Absolute path to the chunk-metadata sidecar (the file that carries the must_retrieve safety
        /// leaves and read_when triggers). When set, the context index generator loads the sidecar from
        /// this path instead of the default <c>&lt;docs root&gt;/context-index/chunk-metadata.json</c>. It
        /// lets the real, operator-local sidecar live at a stable path outside the git checkout, where a
        /// <c>git clean</c> cannot remove it. Null (the default) keeps the docs-root default.
        /// </summary>
        public string? ChunkMetadataPath { get; set; } = null;

        /// <summary>
        /// Identifier of the registered Embedding model endpoint that unified context search uses for
        /// semantic ranking of guidance and native memory. Null (the default) keeps that search lexical.
        /// The endpoint must be enabled, use the Ollama or OpenAI-compatible protocol, and address a
        /// loopback, private-network or single-label host, because the embedded text is private
        /// memory. A hosted endpoint is refused, and the code-index embedding endpoint is never used
        /// in its place.
        /// </summary>
        public string? SemanticEndpointId { get; set; } = null;

        /// <summary>
        /// Copy the runtime-tunable values from another instance in place, so a live service that
        /// captured this nested object at construction observes a hot reload. Null is ignored.
        /// </summary>
        /// <param name="source">Settings to copy from.</param>
        public void CopyFrom(ContextRetrievalSettings source)
        {
            if (source == null) return;
            FetchToolEnabled = source.FetchToolEnabled;
            BriefSlimmingEnabled = source.BriefSlimmingEnabled;
            BriefLeafBudgetBytes = source.BriefLeafBudgetBytes;
            MaxLeafBytesPerCall = source.MaxLeafBytesPerCall;
            MaxCallsPerMission = source.MaxCallsPerMission;
            ChunkMetadataPath = source.ChunkMetadataPath;
            SemanticEndpointId = source.SemanticEndpointId;
        }

        private int _BriefLeafBudgetBytes = 24000;
        private int _MaxLeafBytesPerCall = 24000;
        private int _MaxCallsPerMission = 60;
    }
}
