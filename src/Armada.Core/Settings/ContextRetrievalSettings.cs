namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// Configuration for the captain-facing context fetch tool (<c>armada_fetch_context</c>). The tool
    /// is read-only and informative: it returns already-sanitized AI-Memory and docs leaf text for a
    /// query and has no side effect on any Armada record. These settings bound it — a per-call leaf
    /// byte budget and a per-mission call budget — and can turn it off without a code change.
    /// </summary>
    public class ContextRetrievalSettings
    {
        /// <summary>
        /// Whether captains may call the context fetch tool. Default true: the tool is read-only,
        /// makes no external egress, and returns only sanitized memory text, so it ships enabled.
        /// </summary>
        public bool FetchToolEnabled { get; set; } = true;

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

        private int _MaxLeafBytesPerCall = 24000;
        private int _MaxCallsPerMission = 60;
    }
}
