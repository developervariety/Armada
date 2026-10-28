namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;

    /// <summary>The result of one bounded file scan.</summary>
    public class StorageScanReport
    {
        /// <summary><see cref="StorageStatusEnum.Complete"/> when no bound was reached and every location was measured; otherwise <see cref="StorageStatusEnum.Partial"/>.</summary>
        public StorageStatusEnum Status { get; set; } = StorageStatusEnum.Pending;

        /// <summary>When the scan finished, in UTC.</summary>
        public DateTime MeasuredUtc { get; set; } = DateTime.MinValue;

        /// <summary>Files and directories examined across all locations.</summary>
        public long EntriesExamined { get; set; } = 0;

        /// <summary>Scan duration in milliseconds.</summary>
        public long ElapsedMilliseconds { get; set; } = 0;

        /// <summary>Why the scan is partial: codes from <see cref="StorageWarningCodes"/> raised by the scan as a whole or by any location.</summary>
        public List<string> PartialReasons { get; set; } = new List<string>();

        /// <summary>Measured locations.</summary>
        public List<StorageLocation> Locations { get; set; } = new List<StorageLocation>();

        /// <summary>Volumes that hold the locations.</summary>
        public List<StorageVolume> Volumes { get; set; } = new List<StorageVolume>();
    }
}
