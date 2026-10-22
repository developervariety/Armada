namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One object listed from the archive bucket.
    /// </summary>
    public class ArchiveObjectInfo
    {
        #region Public-Members

        /// <summary>
        /// Object key.
        /// </summary>
        public string Key { get; set; } = String.Empty;

        /// <summary>
        /// Object size in bytes.
        /// </summary>
        public long Bytes { get; set; } = 0;

        /// <summary>
        /// Time the bucket last wrote the object, in UTC.
        /// </summary>
        public DateTime LastModifiedUtc { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Entity tag, when the bucket returned one.
        /// </summary>
        public string? ETag { get; set; } = null;

        #endregion
    }
}
