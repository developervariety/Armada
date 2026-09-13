namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// A native captain memory record: working knowledge a captain or operator kept from earlier work,
    /// such as a vessel fact, a prior finding, or a repeatable procedure. Records are tenant-owned and
    /// carry provenance. Native memory is working memory, not an accepted rule: an external durable
    /// memory rule delivered in a mission brief wins over a native record on conflict.
    /// </summary>
    public class Memory
    {
        #region Public-Members

        /// <summary>
        /// Unique identifier (mem_ prefix).
        /// </summary>
        public string Id
        {
            get => _Id;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(Id));
                _Id = value;
            }
        }

        /// <summary>
        /// Owning tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Creating user identifier. Owner of a user-specific record.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Ownership scope inside the tenant.
        /// </summary>
        public MemoryScopeEnum Scope { get; set; } = MemoryScopeEnum.TenantWide;

        /// <summary>
        /// Memory classification.
        /// </summary>
        public MemoryTypeEnum Type { get; set; } = MemoryTypeEnum.Semantic;

        /// <summary>
        /// Optional grouping inside the type, for example "build" or "deployment".
        /// </summary>
        public string? Topic { get; set; } = null;

        /// <summary>
        /// Optional stable key (slug) that is unique inside the tenant. Writing a record with an
        /// existing key updates that record in place instead of creating a duplicate.
        /// </summary>
        public string? Key { get; set; } = null;

        /// <summary>
        /// One-line recall hook returned in listings and search.
        /// </summary>
        public string? Summary { get; set; } = null;

        /// <summary>
        /// The memory itself, written to stand on its own.
        /// </summary>
        public string Content
        {
            get => _Content;
            set => _Content = value ?? String.Empty;
        }

        /// <summary>
        /// Importance in [0.0, 1.0]. Recall orders by salience first, then by recency.
        /// </summary>
        public double Salience
        {
            get => _Salience;
            set => _Salience = Double.IsNaN(value) ? 0.5 : (value < 0.0 ? 0.0 : (value > 1.0 ? 1.0 : value));
        }

        /// <summary>
        /// Revision counter. Starts at 1 and increases by one on each successful update. Callers pass
        /// the version they read to detect a concurrent change.
        /// </summary>
        public int Version
        {
            get => _Version;
            set => _Version = value < 1 ? 1 : value;
        }

        /// <summary>
        /// Where this record came from.
        /// </summary>
        public MemorySourceKindEnum SourceKind { get; set; } = MemorySourceKindEnum.Manual;

        /// <summary>
        /// Originating voyage identifier, when known.
        /// </summary>
        public string? SourceVoyageId { get; set; } = null;

        /// <summary>
        /// Originating mission identifier, when known.
        /// </summary>
        public string? SourceMissionId { get; set; } = null;

        /// <summary>
        /// Originating vessel identifier (provenance).
        /// </summary>
        public string? SourceVesselId { get; set; } = null;

        /// <summary>
        /// Free-text provenance note.
        /// </summary>
        public string? SourceDetail { get; set; } = null;

        /// <summary>
        /// Vessel this record is about, so it can be recalled per vessel. May differ from
        /// <see cref="SourceVesselId"/>.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Retrieval tags. Stored in a child table.
        /// </summary>
        public List<string> Tags
        {
            get => _Tags;
            set => _Tags = value ?? new List<string>();
        }

        /// <summary>
        /// Creation timestamp in UTC.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last update timestamp in UTC.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.MemoryIdPrefix, 24);
        private string _Content = String.Empty;
        private double _Salience = 0.5;
        private int _Version = 1;
        private List<string> _Tags = new List<string>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public Memory()
        {
        }

        #endregion
    }
}
