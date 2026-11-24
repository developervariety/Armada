namespace Armada.Core.Models
{
    using System;

    /// <summary>An operator's expiring permission for one mission launch and produced source.</summary>
    public sealed class MissionAutomaticWorkGrant
    {
        /// <summary>Exact mission authorized by this grant.</summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>Tenant that owns the authorized mission.</summary>
        public string? TenantId { get; set; }

        /// <summary>User that owns the authorized mission.</summary>
        public string? UserId { get; set; }

        /// <summary>Start identity of the authorized launch, not a retry counter.</summary>
        public DateTime StartedUtc { get; set; }

        /// <summary>Exact captured source commit authorized by this grant.</summary>
        public string CommitHash { get; set; } = String.Empty;

        /// <summary>Authenticated operator who issued the grant.</summary>
        public string GrantedByUserId { get; set; } = String.Empty;

        /// <summary>Server time when the permission was issued.</summary>
        public DateTime GrantedUtc { get; set; }

        /// <summary>Time after which this grant cannot admit new work.</summary>
        public DateTime ExpiresUtc { get; set; }
    }
}
