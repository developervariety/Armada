namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Outcome of a cascade cleanup: how many dependent rows were removed, and each row or list that was skipped
    /// with its reason.
    /// </summary>
    public class CascadeCleanupResult
    {
        #region Public-Members

        /// <summary>
        /// Number of dependent rows removed.
        /// </summary>
        public int Removed { get; set; } = 0;

        /// <summary>
        /// Number of dependent rows or lists skipped.
        /// </summary>
        public int Skipped => Skips.Count;

        /// <summary>
        /// Each skipped row or list with its reason.
        /// </summary>
        public List<CascadeCleanupSkip> Skips { get; set; } = new List<CascadeCleanupSkip>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CascadeCleanupResult()
        {
        }

        #endregion
    }
}
