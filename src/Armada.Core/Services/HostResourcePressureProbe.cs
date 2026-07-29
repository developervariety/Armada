namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Default resource-pressure probe. Reads the available (host/container)
    /// memory visible to the managed runtime via the GC API, which is container
    /// aware and does not expose private infrastructure details. Does not depend
    /// on swap and never kills processes.
    /// </summary>
    public sealed class HostResourcePressureProbe : IResourcePressureProbe
    {
        /// <summary>
        /// Take a resource-pressure snapshot.
        /// </summary>
        /// <returns>Snapshot of available host/container memory.</returns>
        public ResourcePressureSnapshot Probe()
        {
            long availableBytes = 0;
            try
            {
                GCMemoryInfo info = GC.GetGCMemoryInfo();
                long total = info.TotalAvailableMemoryBytes;
                long heap = info.HeapSizeBytes;
                availableBytes = total - heap;
                if (availableBytes < 0) availableBytes = 0;
            }
            catch
            {
                availableBytes = 0;
            }

            return new ResourcePressureSnapshot
            {
                AvailableMemoryBytes = availableBytes,
                ObservedUtc = DateTime.UtcNow
            };
        }
    }
}