namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Aggregated result of a dock-boundary scan performed by DockBoundaryScanner.
    /// Contains the pass/fail verdict and the ordered list of blocking findings.
    /// </summary>
    public sealed class DockBoundaryScanResult
    {
        #region Public-Members

        /// <summary>
        /// True when the scan produced no blocking findings; false when one or more
        /// protected-path, secret, or private-identifier findings were raised.
        /// </summary>
        public bool Passed { get; set; } = true;

        /// <summary>
        /// Ordered list of findings raised during the scan. Empty when Passed is true.
        /// </summary>
        public List<DockBoundaryFinding> Findings { get; set; } = new List<DockBoundaryFinding>();

        #endregion
    }
}
