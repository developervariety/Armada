namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// A configuration record that carries an owning tenant, an owning user and an ownership scope,
    /// so one visibility rule can decide who may read it.
    /// </summary>
    public interface IOwnedRecord
    {
        /// <summary>
        /// Owning tenant.
        /// </summary>
        string? TenantId { get; }

        /// <summary>
        /// Owning user.
        /// </summary>
        string? UserId { get; }

        /// <summary>
        /// Ownership scope inside the owning tenant.
        /// </summary>
        OwnershipScopeEnum OwnershipScope { get; }

        /// <summary>
        /// True for a record the server seeds. A built-in record is readable by every authenticated caller.
        /// </summary>
        bool IsBuiltIn { get; }

        /// <summary>
        /// Creation timestamp in UTC, used for ordering and date filters.
        /// </summary>
        DateTime CreatedUtc { get; }
    }
}
