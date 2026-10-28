namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Raised when startup refuses to continue because of the seeded default credentials: the listener is bound beyond
    /// loopback while they are active, or the configured initial admin password is not acceptable.
    /// </summary>
    public sealed class DefaultCredentialPolicyException : InvalidOperationException
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="message">Operator-facing explanation, including how to resolve the refusal.</param>
        public DefaultCredentialPolicyException(string message) : base(message)
        {
        }
    }
}
