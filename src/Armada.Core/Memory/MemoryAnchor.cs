namespace Armada.Core.Memory
{
    using System.Collections.Generic;

    /// <summary>Lightweight structural anchor linking an accepted reflection memory note to its source evidence.</summary>
    public sealed class MemoryAnchor
    {
        #region Public-Members

        /// <summary>Mission IDs (msn_ prefix) referenced by or contributing to this note.</summary>
        public List<string> SourceMissionIds { get; set; } = new List<string>();

        /// <summary>File-path-like references extracted from the accepted note text (e.g. src/Armada.Core/Models/Vessel.cs).</summary>
        public List<string> FilePaths { get; set; } = new List<string>();

        /// <summary>Optional symbol names; empty when degraded to path/mission-only anchoring.</summary>
        public List<string> SymbolNames { get; set; } = new List<string>();

        /// <summary>Evidence confidence: high, mixed, or low. Inherited from the reflections-diff block; defaults to high for editsMarkdown overrides.</summary>
        public string Confidence { get; set; } = "high";

        /// <summary>How the accepted content was sourced: verbatim, edits, pack_curate, identity_curate, or fleet_curate.</summary>
        public string EvidenceKind { get; set; } = "verbatim";

        #endregion
    }
}
