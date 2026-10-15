namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;

    /// <summary>
    /// One merge queue processing pass: when it started (leftover integration worktrees and branches from earlier runs
    /// are older than this) and which hosts have already been cleaned of them during the pass.
    /// </summary>
    internal sealed class MergeQueuePass
    {
        #region Public-Members

        /// <summary>
        /// When the pass started (UTC).
        /// </summary>
        public DateTime StartedUtc { get; }

        #endregion

        #region Private-Members

        private readonly ConcurrentDictionary<string, byte> _PrunedHosts = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="startedUtc">When the pass started (UTC).</param>
        public MergeQueuePass(DateTime startedUtc)
        {
            StartedUtc = startedUtc;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Claim the leftover cleanup for a host repository in this pass.
        /// </summary>
        /// <param name="hostKey">Host and repository key.</param>
        /// <returns>True the first time a key is claimed in this pass; false afterwards.</returns>
        public bool TryClaimPrune(string hostKey)
        {
            return _PrunedHosts.TryAdd(hostKey ?? String.Empty, 0);
        }

        #endregion
    }
}
