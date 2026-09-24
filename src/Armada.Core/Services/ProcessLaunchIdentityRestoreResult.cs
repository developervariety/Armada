namespace Armada.Core.Services
{
    using System.Collections.Generic;

    /// <summary>
    /// Outcome of restoring stored agent process launch identities.
    /// </summary>
    public sealed class ProcessLaunchIdentityRestoreResult
    {
        #region Public-Members

        /// <summary>
        /// Number of process identifiers whose stored start time was restored.
        /// </summary>
        public int Restored { get; set; } = 0;

        /// <summary>
        /// Process identifiers stored without a start time. Their processes stay unverified: they read as running and
        /// a stop refuses to kill them.
        /// </summary>
        public List<int> UnverifiedProcessIds { get; set; } = new List<int>();

        #endregion
    }
}
