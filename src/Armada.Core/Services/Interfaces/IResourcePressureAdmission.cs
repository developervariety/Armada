namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Injectable resource-pressure admission policy. Evaluated before a captain
    /// is launched; defers safely with a clear resource-pressure reason when host
    /// memory or active captain/build pressure is insufficient. After a kernel
    /// OOM (exit 137) classification, <see cref="MarkOom"/> suspends admission
    /// until the cooldown elapses AND the memory probe reports capacity returned.
    /// </summary>
    public interface IResourcePressureAdmission
    {
        /// <summary>
        /// Evaluate whether a new captain may launch given the current active
        /// captain/build pressure count.
        /// </summary>
        /// <param name="activeBuildPressure">Number of currently active captain/build workloads.</param>
        /// <returns>Admission decision with a clear deferral reason when not admitted.</returns>
        ResourcePressureDecision Evaluate(int activeBuildPressure);

        /// <summary>
        /// Record a kernel OOM (exit 137) classification, suspending admission
        /// for the configured cooldown window.
        /// </summary>
        void MarkOom();

        /// <summary>
        /// Whether admission is currently suspended pending capacity recovery.
        /// </summary>
        /// <returns>True when admission is suspended.</returns>
        bool IsCapacitySuspended();
    }
}