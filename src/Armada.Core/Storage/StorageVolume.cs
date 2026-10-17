namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Storage Volume measurement contract.
    /// </summary>
    public class StorageVolume
    {
        /// <summary>Volume mount path visible to this process.</summary>
        public string Path { get; set; } = String.Empty;

        /// <summary>Volume capacity, including unrelated data.</summary>
        public long? TotalBytes { get; set; } = null;

        /// <summary>Space available to this process.</summary>
        public long? AvailableBytes { get; set; } = null;

        /// <summary>Complete or Unavailable.</summary>
        public string Status { get; set; } = "Unavailable";

    }
}
