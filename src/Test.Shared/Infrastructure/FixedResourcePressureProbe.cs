namespace Test.Shared.Infrastructure
{
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Resource-pressure probe that reports a value the test chose instead of the memory of the
    /// machine running the suite. Host memory moves with every other process on the host, so a
    /// test that reads it passes or fails depending on concurrent load rather than on the code.
    /// </summary>
    public sealed class FixedResourcePressureProbe : IResourcePressureProbe
    {
        #region Public-Members

        /// <summary>
        /// Available memory reported by every probe. Null reports that no measurement exists.
        /// </summary>
        public long? AvailableMemoryBytes { get; set; }

        /// <summary>
        /// Number of snapshots taken, so a test can prove the admission gate consulted the probe.
        /// </summary>
        public int ProbeCount { get; private set; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="availableMemoryBytes">Available memory to report; null reports no measurement.</param>
        public FixedResourcePressureProbe(long? availableMemoryBytes = null)
        {
            AvailableMemoryBytes = availableMemoryBytes;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public ResourcePressureSnapshot Probe()
        {
            ProbeCount++;
            return new ResourcePressureSnapshot { AvailableMemoryBytes = AvailableMemoryBytes };
        }

        #endregion
    }
}
