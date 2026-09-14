namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Bounded, tenant-scoped window query shared by every production fact store.
    /// </summary>
    public sealed class ProductionFactQuery
    {
        /// <summary>Tenant filter. Null reads every tenant and is reserved for administrators.</summary>
        public string? TenantId { get; set; } = null;

        /// <summary>User filter. Null reads every user in the tenant.</summary>
        public string? UserId { get; set; } = null;

        /// <summary>Inclusive UTC start.</summary>
        public DateTime FromUtc { get; set; } = DateTime.MinValue;

        /// <summary>Exclusive UTC end.</summary>
        public DateTime ToUtc { get; set; } = DateTime.MaxValue;

        /// <summary>Maximum rows returned. The store reports truncation when more rows exist.</summary>
        public int Limit
        {
            get => _Limit;
            set => _Limit = Math.Clamp(value, 1, 100000);
        }

        private int _Limit = 100000;
    }
}
