namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The AI-Memory part of a captain brief: the short section written into the instruction file,
    /// and the memory files written into the dock that the section lists. With no files the section
    /// is self-contained (the full read-the-tree section).
    /// </summary>
    public sealed class ContextBriefDelivery
    {
        #region Public-Members

        /// <summary>The section written into the captain instruction file.</summary>
        public string Section { get; set; } = String.Empty;

        /// <summary>Memory files to write into the dock, in reading order. Empty on the full-section path.</summary>
        public List<ContextBriefFile> Files { get; set; } = new List<ContextBriefFile>();

        /// <summary>The section accounting when brief slimming was on; null when off.</summary>
        public ContextBriefTelemetry? Telemetry { get; set; }

        #endregion
    }
}
