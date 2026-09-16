namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// Configuration for the captain-facing context fetch tool (<c>armada_fetch_context</c>) and the
    /// flagged brief-slimming path. The fetch tool is read-only and informative: it returns
    /// already-sanitized AI-Memory and docs leaf text for a query and has no side effect on any Armada
    /// record. These settings bound it — a per-call leaf byte budget and a per-mission call budget —
    /// and can turn it off without a code change. The brief-slimming flag replaces the full
    /// read-every-file AI-Memory brief section with the always-on core plus the mission's retrieved
    /// leaves; it defaults OFF so brief generation is unchanged until it is deliberately enabled.
    /// </summary>
    public class ContextRetrievalSettings
    {
        /// <summary>
        /// Whether captains may call the context fetch tool. Default true: the tool is read-only,
        /// makes no external egress, and returns only sanitized memory text, so it ships enabled.
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
        /// The leaf byte budget one fetch call may return. Core and matching-domain must-retrieve
        /// leaves are exempt from this budget in the retrieval layer, but the fetch tool returns only
        /// leaves (core already ships in the brief), so this bounds one call's returned bytes.
        /// </summary>
        public int MaxLeafBytesPerCall
        {
            get => _MaxLeafBytesPerCall;
            set => _MaxLeafBytesPerCall = Math.Max(0, value);
        }

        /// <summary>
        /// The maximum number of fetch calls one mission may make. When exhausted, the tool returns a
        /// clear budget message instead of more leaves.
        /// </summary>
        public int MaxCallsPerMission
        {
            get => _MaxCallsPerMission;
            set => _MaxCallsPerMission = Math.Max(0, value);
        }

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
        }

        private int _BriefLeafBudgetBytes = 24000;
        private int _MaxLeafBytesPerCall = 24000;
        private int _MaxCallsPerMission = 60;
    }
}
