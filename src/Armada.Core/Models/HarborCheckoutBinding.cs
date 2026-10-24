namespace Armada.Core.Models
{
    using System;
    using System.Text.RegularExpressions;
    using System.Text.Json.Serialization;

    /// <summary>Durable identity for a dock checkout owned by one Harbor enrollment.</summary>
    public sealed class HarborCheckoutBinding
    {
        /// <summary>Binding format version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Authenticated Harbor runner identifier.</summary>
        public string RunnerId { get; set; } = String.Empty;

        /// <summary>Runner enrollment generation that owns the checkout.</summary>
        public long EnrollmentGeneration { get; set; }

        /// <summary>Tenant that owns the vessel and checkout.</summary>
        public string TenantId { get; set; } = String.Empty;

        /// <summary>User that owns the vessel and checkout.</summary>
        public string UserId { get; set; } = String.Empty;

        /// <summary>Repository grant used to create the checkout.</summary>
        public string RepositoryGrantId { get; set; } = String.Empty;

        /// <summary>SHA-256 fingerprint of the granted repository identity.</summary>
        public string RepositoryFingerprint { get; set; } = String.Empty;

        /// <summary>Opaque checkout identifier assigned by the Admiral before provisioning.</summary>
        public string CheckoutId { get; set; } = String.Empty;

        /// <summary>Full commit identifier used as the checkout base.</summary>
        public string BaseCommit { get; set; } = String.Empty;

        /// <summary>Branch associated with the checkout.</summary>
        public string BranchName { get; set; } = String.Empty;

        /// <summary>Immutable revision fence for checkout operations.</summary>
        public string Revision { get; set; } = String.Empty;

        /// <summary>True when this binding has every required identity field.</summary>
        [JsonIgnore]
        public bool IsComplete => Version == 1
            && Identifier(RunnerId)
            && EnrollmentGeneration > 0
            && Identifier(TenantId)
            && Identifier(UserId)
            && Identifier(RepositoryGrantId)
            && Regex.IsMatch(RepositoryFingerprint ?? String.Empty, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)
            && Regex.IsMatch(CheckoutId ?? String.Empty, @"\Ahco_[0-9a-f]{32}\z", RegexOptions.CultureInvariant)
            && Regex.IsMatch(BaseCommit ?? String.Empty, @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z", RegexOptions.CultureInvariant)
            && Identifier(BranchName)
            && Identifier(Revision);

        private static bool Identifier(string? value) => !String.IsNullOrWhiteSpace(value)
            && value.Length <= 900
            && !value.Contains("\r", StringComparison.Ordinal)
            && !value.Contains("\n", StringComparison.Ordinal);
    }
}
