namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One persona record the tier record migration receives a minimum tier from the retired specialist
    /// persona list.
    /// </summary>
    public sealed class PersonaMinimumTierMigrationChange
    {
        #region Public-Members

        /// <summary>Persona identifier.</summary>
        public string PersonaId { get; set; } = String.Empty;

        /// <summary>Persona name.</summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>Tenant of the persona record, when set.</summary>
        public string? TenantId { get; set; } = null;

        #endregion
    }
}
