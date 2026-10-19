namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The memory a voyage's first slimmed brief staged, persisted so every later stage of the voyage
    /// receives the same files and section, including across an admiral restart.
    /// </summary>
    public sealed class MemoryVoyagePin
    {
        #region Public-Members

        /// <summary>The voyage the memory is pinned for.</summary>
        public string VoyageId { get; set; } = String.Empty;

        /// <summary>The mission whose brief first staged the memory.</summary>
        public string? MissionId { get; set; }

        /// <summary>When the memory was first staged.</summary>
        public DateTime PinnedUtc { get; set; }

        /// <summary>The brief section that lists the files.</summary>
        public string Section { get; set; } = String.Empty;

        /// <summary>The staged memory files, in reading order.</summary>
        public List<ContextBriefFile> Files { get; set; } = new List<ContextBriefFile>();

        /// <summary>The section accounting recorded when the memory was first staged.</summary>
        public ContextBriefTelemetry? Telemetry { get; set; }

        #endregion
    }
}
