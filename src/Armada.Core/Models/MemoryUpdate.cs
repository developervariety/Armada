namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Partial change to a native captain memory record. A field left null keeps its stored value, so a
    /// caller that knows one field does not have to resend the whole record.
    /// </summary>
    public class MemoryUpdate
    {
        /// <summary>
        /// New memory type: Episodic, Semantic, or Procedural.
        /// </summary>
        public string? Type { get; set; } = null;

        /// <summary>
        /// New topic. An empty string clears it.
        /// </summary>
        public string? Topic { get; set; } = null;

        /// <summary>
        /// New stable key. An empty string clears it.
        /// </summary>
        public string? Key { get; set; } = null;

        /// <summary>
        /// New one-line summary. An empty string clears it.
        /// </summary>
        public string? Summary { get; set; } = null;

        /// <summary>
        /// New content.
        /// </summary>
        public string? Content { get; set; } = null;

        /// <summary>
        /// New salience in [0.0, 1.0].
        /// </summary>
        public double? Salience { get; set; } = null;

        /// <summary>
        /// Replacement tag list. An empty list clears every tag.
        /// </summary>
        public List<string>? Tags { get; set; } = null;

        /// <summary>
        /// New vessel association. An empty string clears it.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// New provenance note. An empty string clears it.
        /// </summary>
        public string? SourceDetail { get; set; } = null;

        /// <summary>
        /// New ownership scope: TenantWide or UserSpecific. Only a tenant or global administrator may
        /// change the scope.
        /// </summary>
        public string? Scope { get; set; } = null;

        /// <summary>
        /// Version the caller read. When supplied and different from the stored version, the write is
        /// refused as a conflict instead of overwriting another writer.
        /// </summary>
        public int? ExpectedVersion { get; set; } = null;
    }
}
