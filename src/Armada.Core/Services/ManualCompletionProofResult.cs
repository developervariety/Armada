namespace Armada.Core.Services
{
    /// <summary>
    /// Immutable result of the manual completion proof.
    /// </summary>
    public sealed class ManualCompletionProofResult
    {
        private ManualCompletionProofResult(bool allowed, string reason)
        {
            Allowed = allowed;
            Reason = reason;
        }

        /// <summary>True when the route may proceed.</summary>
        public bool Allowed { get; }

        /// <summary>Stable reason suitable for an API response and audit event.</summary>
        public string Reason { get; }

        /// <summary>Creates a passing result.</summary>
        public static ManualCompletionProofResult Pass(string reason) => new ManualCompletionProofResult(true, reason);

        /// <summary>Creates a fail-closed result.</summary>
        public static ManualCompletionProofResult Fail(string reason) => new ManualCompletionProofResult(false, reason);
    }
}
