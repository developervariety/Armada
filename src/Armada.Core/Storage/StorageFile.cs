namespace Armada.Core.Storage
{
    using System;

    /// <summary>One file in a location's largest-file list.</summary>
    public class StorageFile
    {
        /// <summary>Full path of the file.</summary>
        public string Path { get; set; } = String.Empty;

        /// <summary>Logical length in bytes.</summary>
        public long Bytes { get; set; } = 0;

        /// <summary>Last write time in UTC.</summary>
        public DateTime LastWriteUtc { get; set; } = DateTime.MinValue;
    }
}
