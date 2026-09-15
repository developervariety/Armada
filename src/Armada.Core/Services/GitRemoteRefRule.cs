namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Classifies the outcome of deleting a ref on a remote. Every cleanup path that deletes a remote
    /// branch or ref calls this one rule, so a ref the remote never held reads as already deleted
    /// everywhere, while a push, network, or authentication failure still reads as a failure.
    /// </summary>
    public static class GitRemoteRefRule
    {
        #region Public-Members

        /// <summary>
        /// The text git prints when a delete names a ref the remote does not hold.
        /// </summary>
        public const string RemoteRefAbsentText = "remote ref does not exist";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Returns true when a git failure message says the remote ref being deleted does not exist.
        /// Armada does not push mission branches, so deleting one on the remote normally finds nothing;
        /// that is the desired end state, not a cleanup failure.
        /// </summary>
        /// <param name="gitFailureMessage">The failure text git produced.</param>
        /// <returns>True when the remote ref is absent.</returns>
        public static bool IsRemoteRefAbsent(string? gitFailureMessage)
        {
            if (String.IsNullOrWhiteSpace(gitFailureMessage)) return false;
            return gitFailureMessage.Contains(RemoteRefAbsentText, StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
