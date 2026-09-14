namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// A self-deploy cutover step failed closed with a stable reason.
    /// </summary>
    public sealed class SelfDeployCutoverException : Exception
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="failureReason">Stable reason.</param>
        /// <param name="innerException">Optional cause.</param>
        public SelfDeployCutoverException(string failureReason, Exception? innerException = null)
            : base(failureReason, innerException)
        {
            FailureReason = String.IsNullOrWhiteSpace(failureReason) ? "self_deploy_cutover_failed" : failureReason;
        }

        /// <summary>
        /// Stable reason.
        /// </summary>
        public string FailureReason { get; }
    }
}
