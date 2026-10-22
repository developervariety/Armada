namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// A stored trace record: the compact description of one record, kept after the record's raw data
    /// is archived or expired so an operator can still trace it. One record per described id; a later
    /// write replaces it.
    /// </summary>
    public sealed class TraceRecord
    {
        #region Public-Members

        /// <summary>Id of the described record; the primary key.</summary>
        public string Id
        {
            get => _Id;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Id));
                _Id = value;
            }
        }

        /// <summary>Kind of the described record.</summary>
        public TraceEntityKindEnum Kind { get; set; } = TraceEntityKindEnum.Mission;

        /// <summary>Tenant of the described record.</summary>
        public string? TenantId { get; set; } = null;

        /// <summary>Status of the described record at the last write.</summary>
        public string? Status { get; set; } = null;

        /// <summary>When the described record was created.</summary>
        public DateTime SubjectCreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>When the described record last changed; trace retention counts from here.</summary>
        public DateTime SubjectLastUpdateUtc { get; set; } = DateTime.UtcNow;

        /// <summary>When this trace record was written.</summary>
        public DateTime RecordedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>The description.</summary>
        public TraceDocument Document { get; set; } = new TraceDocument();

        #endregion

        #region Private-Members

        private string _Id = "unknown";

        #endregion
    }
}
