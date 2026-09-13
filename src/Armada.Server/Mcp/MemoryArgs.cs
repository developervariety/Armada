namespace Armada.Server.Mcp
{
    using System.Collections.Generic;

    /// <summary>
    /// Arguments naming one memory record.
    /// </summary>
    public class MemoryIdArgs
    {
        /// <summary>
        /// Memory identifier (mem_ prefix). Required.
        /// </summary>
        public string MemoryId { get; set; } = "";
    }

    /// <summary>
    /// Arguments for searching native memory before writing to it.
    /// </summary>
    public class MemorySearchArgs
    {
        /// <summary>
        /// Case-insensitive substring over content, summary, topic, key and tags.
        /// </summary>
        public string? Search { get; set; }

        /// <summary>
        /// Type filter: Episodic, Semantic, or Procedural.
        /// </summary>
        public string? Type { get; set; }

        /// <summary>
        /// Exact topic filter.
        /// </summary>
        public string? Topic { get; set; }

        /// <summary>
        /// Vessel filter. Matches the vessel a record is about and the vessel it came from.
        /// </summary>
        public string? VesselId { get; set; }

        /// <summary>
        /// Page number, one based.
        /// </summary>
        public int? PageNumber { get; set; }

        /// <summary>
        /// Records per page.
        /// </summary>
        public int? PageSize { get; set; }
    }

    /// <summary>
    /// Arguments for creating a record, or updating the record that carries the same key.
    /// </summary>
    public class MemoryUpsertArgs
    {
        /// <summary>
        /// Episodic, Semantic, or Procedural. Defaults to Semantic.
        /// </summary>
        public string? Type { get; set; }

        /// <summary>
        /// Grouping inside the type, for example "build".
        /// </summary>
        public string? Topic { get; set; }

        /// <summary>
        /// Stable key (slug). Writing the same key again updates that record in place.
        /// </summary>
        public string? Key { get; set; }

        /// <summary>
        /// One-line recall hook.
        /// </summary>
        public string? Summary { get; set; }

        /// <summary>
        /// The memory content. Required.
        /// </summary>
        public string Content { get; set; } = "";

        /// <summary>
        /// Importance in [0.0, 1.0]. Defaults to 0.5.
        /// </summary>
        public double? Salience { get; set; }

        /// <summary>
        /// Retrieval tags.
        /// </summary>
        public List<string>? Tags { get; set; }

        /// <summary>
        /// Voyage, Mission, Vessel, Conversation, Manual, or Other.
        /// </summary>
        public string? SourceKind { get; set; }

        /// <summary>
        /// Originating voyage identifier.
        /// </summary>
        public string? SourceVoyageId { get; set; }

        /// <summary>
        /// Originating mission identifier.
        /// </summary>
        public string? SourceMissionId { get; set; }

        /// <summary>
        /// Originating vessel identifier.
        /// </summary>
        public string? SourceVesselId { get; set; }

        /// <summary>
        /// Free-text provenance note.
        /// </summary>
        public string? SourceDetail { get; set; }

        /// <summary>
        /// Vessel this record is about.
        /// </summary>
        public string? VesselId { get; set; }

        /// <summary>
        /// TenantWide or UserSpecific.
        /// </summary>
        public string? Scope { get; set; }

        /// <summary>
        /// Version the caller read. Supply it to get a conflict instead of an overwrite when the
        /// keyed record changed since the read.
        /// </summary>
        public int? ExpectedVersion { get; set; }
    }

    /// <summary>
    /// Arguments for changing one record. A field left out keeps its value.
    /// </summary>
    public class MemoryUpdateArgs
    {
        /// <summary>
        /// Memory identifier (mem_ prefix). Required.
        /// </summary>
        public string MemoryId { get; set; } = "";

        /// <summary>
        /// New type: Episodic, Semantic, or Procedural.
        /// </summary>
        public string? Type { get; set; }

        /// <summary>
        /// New topic.
        /// </summary>
        public string? Topic { get; set; }

        /// <summary>
        /// New key.
        /// </summary>
        public string? Key { get; set; }

        /// <summary>
        /// New summary.
        /// </summary>
        public string? Summary { get; set; }

        /// <summary>
        /// New content.
        /// </summary>
        public string? Content { get; set; }

        /// <summary>
        /// New salience in [0.0, 1.0].
        /// </summary>
        public double? Salience { get; set; }

        /// <summary>
        /// Replacement tag list.
        /// </summary>
        public List<string>? Tags { get; set; }

        /// <summary>
        /// New vessel association.
        /// </summary>
        public string? VesselId { get; set; }

        /// <summary>
        /// New provenance note.
        /// </summary>
        public string? SourceDetail { get; set; }

        /// <summary>
        /// New scope: TenantWide or UserSpecific.
        /// </summary>
        public string? Scope { get; set; }

        /// <summary>
        /// Version the caller read. Supply it to get a conflict instead of an overwrite.
        /// </summary>
        public int? ExpectedVersion { get; set; }
    }
}
