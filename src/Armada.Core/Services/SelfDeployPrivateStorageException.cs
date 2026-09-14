namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Named fail-closed error for a storage location that is not private.
    /// </summary>
    internal sealed class SelfDeployPrivateStorageException : Exception
    {
        /// <summary>
        /// Initializes a storage privacy failure.
        /// </summary>
        /// <param name="failureReason">Stable reason code for the failed prerequisite.</param>
        public SelfDeployPrivateStorageException(string failureReason) : base(failureReason)
        {
            FailureReason = failureReason;
        }

        /// <summary>Gets the stable reason code.</summary>
        public string FailureReason { get; }
    }
}
