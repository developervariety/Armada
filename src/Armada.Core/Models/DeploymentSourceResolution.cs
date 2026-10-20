namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Outcome of resolving a deployment source ref to one commit in the vessel's repository.
    /// </summary>
    public class DeploymentSourceResolution
    {
        #region Public-Members

        /// <summary>
        /// The full 40-character commit the ref names, or null when it could not be resolved.
        /// </summary>
        public string? Commit { get; set; } = null;

        /// <summary>
        /// True when the vessel has a git repository the ref was resolved in. False means there was nothing to
        /// resolve against, which is different from a ref that names nothing in an existing repository.
        /// </summary>
        public bool RepositoryFound { get; set; } = false;

        /// <summary>
        /// Repository path the ref was resolved in, when one was found.
        /// </summary>
        public string? RepositoryPath { get; set; } = null;

        /// <summary>
        /// Why the ref did not resolve; empty when it did.
        /// </summary>
        public string Reason
        {
            get => _Reason;
            set => _Reason = value ?? String.Empty;
        }

        #endregion

        #region Private-Members

        private string _Reason = String.Empty;

        #endregion
    }
}
