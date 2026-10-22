namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// A pointer to archived raw data that belongs to a trace record.
    /// </summary>
    public sealed class TraceArchivePointer
    {
        #region Public-Members

        /// <summary>Object key in the archive.</summary>
        public string Key { get; set; } = String.Empty;

        /// <summary>SHA-256 of the archived object, lowercase hex.</summary>
        public string? Sha256 { get; set; } = null;

        /// <summary>Size of the archived object in bytes.</summary>
        public long? Bytes { get; set; } = null;

        /// <summary>True once the raw object has passed its retention and been removed from the archive.</summary>
        public bool Expired { get; set; } = false;

        #endregion
    }
}
