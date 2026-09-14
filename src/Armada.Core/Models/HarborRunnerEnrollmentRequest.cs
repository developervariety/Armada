namespace Armada.Core.Models
{
    /// <summary>Administrator request to bind a Harbor runner to an existing durable credential.</summary>
    public sealed class HarborRunnerEnrollmentRequest
    {
        /// <summary>Runner identifier presented by the Harbor during handshake.</summary>
        public string RunnerId { get; set; } = string.Empty;

        /// <summary>Credential identifier whose verified principal owns the runner.</summary>
        public string CredentialId { get; set; } = string.Empty;
    }
}
