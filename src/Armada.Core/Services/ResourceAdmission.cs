namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Pure admission check that decides whether the host has enough free memory to launch another captain.
    /// Launching onto a memory-starved host gets the captain OOM-killed mid-mission and burns a redispatch,
    /// so when available memory falls below the configured floor the mission is deferred (left pending) for
    /// a later tick instead. The check fails open: if the probe cannot measure memory, it admits rather than
    /// wedging all dispatch. Side-effect free so it unit tests with injected numbers.
    /// </summary>
    public static class ResourceAdmission
    {
        #region Public-Methods

        /// <summary>
        /// Evaluate whether a launch is admissible given current and total memory and the configured floor.
        /// </summary>
        /// <param name="availableBytes">Available physical memory in bytes; a non-positive value means the
        /// probe could not measure it, in which case the decision fails open (admit).</param>
        /// <param name="totalBytes">Total physical memory in bytes (informational; guards against divide
        /// errors in callers).</param>
        /// <param name="minAvailableBytes">The minimum available bytes required to admit a launch. Zero or
        /// negative disables the gate (always admit).</param>
        /// <returns>The admission decision.</returns>
        public static AdmissionDecision Evaluate(long availableBytes, long totalBytes, long minAvailableBytes)
        {
            // Gate disabled.
            if (minAvailableBytes <= 0) return AdmissionDecision.Admitted();

            // Probe could not measure available memory: fail open so dispatch never wedges on a bad reading.
            if (availableBytes <= 0) return AdmissionDecision.Admitted();

            if (availableBytes < minAvailableBytes)
            {
                double availableMb = availableBytes / (1024.0 * 1024.0);
                double floorMb = minAvailableBytes / (1024.0 * 1024.0);
                string reason = "deferred: available memory "
                    + availableMb.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
                    + " MB is below the "
                    + floorMb.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
                    + " MB launch floor";
                return AdmissionDecision.Deferred(reason);
            }

            return AdmissionDecision.Admitted();
        }

        #endregion
    }
}
