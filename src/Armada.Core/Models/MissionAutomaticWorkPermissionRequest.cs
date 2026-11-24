namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>Operator intent; ownership and issuer identity come from authentication, never this body.</summary>
    public sealed class MissionAutomaticWorkPermissionRequest
    {
        /// <summary>Required explicit permission operation.</summary>
        public MissionAutomaticPermissionOperationEnum? Operation { get; set; }

        /// <summary>Required for Grant or Revoke; absent for Enable.</summary>
        public MissionAutomaticActionEnum? Action { get; set; }

        /// <summary>Exact expected current launch identity.</summary>
        public DateTime? ExpectedStartedUtc { get; set; }

        /// <summary>Exact expected captured source commit.</summary>
        public string? ExpectedCommitHash { get; set; }

        /// <summary>UTC expiry required for Grant; no more than one hour from server time.</summary>
        public DateTime? ExpiresUtc { get; set; }

        /// <summary>Bounded reason recorded with the atomic permission change.</summary>
        public string Reason { get; set; } = String.Empty;
    }
}
