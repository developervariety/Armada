namespace Armada.Core.Services.Interfaces
{
    /// <summary>
    /// Reads current host memory figures. Isolated behind an interface so the admission logic that consumes
    /// it can be unit tested with injected numbers rather than real OS state.
    /// </summary>
    public interface ISystemResourceProbe
    {
        /// <summary>
        /// Available physical memory in bytes, or a non-positive value when it cannot be determined.
        /// </summary>
        /// <returns>Available physical memory in bytes.</returns>
        long GetAvailablePhysicalBytes();

        /// <summary>
        /// Total physical memory in bytes, or a non-positive value when it cannot be determined.
        /// </summary>
        /// <returns>Total physical memory in bytes.</returns>
        long GetTotalPhysicalBytes();
    }
}
