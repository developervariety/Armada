namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A measured directory or file. A location owns its whole subtree except the subtrees of other,
    /// more specific locations, so no file is counted twice.
    /// </summary>
    public class StorageLocation
    {
        /// <summary>Category the location belongs to, such as <c>Docks</c> or <c>MissionLogs</c>.</summary>
        public string Category { get; set; } = String.Empty;

        /// <summary>Full path of the location.</summary>
        public string Path { get; set; } = String.Empty;

        /// <summary>Total logical bytes of the files this location owns; null until measured.</summary>
        public long? Bytes { get; set; } = null;

        /// <summary>Number of files this location owns.</summary>
        public long FileCount { get; set; } = 0;

        /// <summary>Oldest last-write time among the measured files.</summary>
        public DateTime? OldestWriteUtc { get; set; } = null;

        /// <summary>Newest last-write time among the measured files.</summary>
        public DateTime? NewestWriteUtc { get; set; } = null;

        /// <summary>Symbolic links and reparse points found below the location and not followed. They do not make the figure partial.</summary>
        public long LinksSkipped { get; set; } = 0;

        /// <summary>Files and directories that disappeared while the scan was running. They do not make the figure partial.</summary>
        public long VanishedEntries { get; set; } = 0;

        /// <summary>Measurement outcome.</summary>
        public StorageStatusEnum Status { get; set; } = StorageStatusEnum.Pending;

        /// <summary>Codes from <see cref="StorageWarningCodes"/> that explain a partial figure.</summary>
        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>Largest files found, largest first.</summary>
        public List<StorageFile> LargestFiles { get; set; } = new List<StorageFile>();
    }
}
