namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One persona record the tier record migration flags as a specialist, because the retired specialist
    /// persona list named it.
    /// </summary>
    public sealed class PersonaSpecialistMigrationChange
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
