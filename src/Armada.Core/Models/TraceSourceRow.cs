namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One source row a trace backfill visits: its id and creation time, the backfill's ordering key.
    /// </summary>
    public sealed class TraceSourceRow
    {
        #region Public-Members

        /// <summary>Row id.</summary>
        public string Id { get; set; } = String.Empty;

        /// <summary>When the row was created.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        #endregion
    }
}
