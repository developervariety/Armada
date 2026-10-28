namespace Armada.Core.Storage
{
    using System;

    /// <summary>Capacity of the volume that holds one or more measured locations.</summary>
    public class StorageVolume
    {
        /// <summary>Mount point or drive root.</summary>
        public string Path { get; set; } = String.Empty;

        /// <summary>Total volume size in bytes.</summary>
        public long? TotalBytes { get; set; } = null;

        /// <summary>Bytes available to the server process.</summary>
        public long? AvailableBytes { get; set; } = null;
    }
}
