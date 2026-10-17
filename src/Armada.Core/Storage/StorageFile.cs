namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Storage File measurement contract.
    /// </summary>
    public class StorageFile
    {
        /// <summary>File path.</summary>
        public string Path { get; set; } = String.Empty;

        /// <summary>Logical length in bytes.</summary>
        public long Bytes { get; set; } = 0;

        /// <summary>Modification time in UTC.</summary>
        public DateTime LastWriteUtc { get; set; } = DateTime.MinValue;

    }
}
