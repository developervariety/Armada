namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule every persona update surface applies to a requested default captain. A null or empty id
    /// clears the default. Any other id must name a captain in the persona's own tenant, and that captain's
    /// persona allow-list must admit the persona; otherwise the update is refused with a named reason and the
    /// persona is left unchanged.
    /// </summary>
    public static class PersonaDefaultCaptainRule
    {
        #region Public-Members

        /// <summary>
        /// Reason code when the requested captain does not exist in the persona's tenant.
        /// </summary>
        public const string NotFoundErrorCode = "default_captain_not_found";

        /// <summary>
        /// Reason code when the requested captain's persona allow-list excludes the persona.
        /// </summary>
        public const string PersonaLockedErrorCode = "default_captain_persona_locked";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Validate a requested default captain and, when it is accepted, set it on the persona.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="persona">Persona being updated; changed only when the request is accepted.</param>
        /// <param name="captainId">Requested captain id; null or empty clears the default.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Null when accepted; otherwise a message that starts with the reason code.</returns>
        public static async Task<string?> ApplyAsync(DatabaseDriver database, Persona persona, string? captainId, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (persona == null) throw new ArgumentNullException(nameof(persona));

            if (String.IsNullOrWhiteSpace(captainId))
            {
                persona.DefaultCaptainId = null;
                return null;
            }

            string requested = captainId.Trim();
            Captain? captain = await database.Captains.ReadAsync(requested, token).ConfigureAwait(false);

            // A captain in another tenant is reported exactly like a missing one, so the reply never
            // confirms that an id exists outside the caller's tenant.
            if (captain == null
                || !String.Equals(
                    OwnershipPolicy.TenantOfRecord(captain.TenantId),
                    OwnershipPolicy.TenantOfRecord(persona.TenantId),
                    StringComparison.Ordinal))
            {
                return NotFoundErrorCode + ": no captain " + requested + " exists for this persona's tenant";
            }

            if (!MissionService.CaptainAllowsPersona(captain, persona.Name))
            {
                return PersonaLockedErrorCode + ": captain " + requested + " does not allow persona " + persona.Name;
            }

            persona.DefaultCaptainId = captain.Id;
            return null;
        }

        #endregion
    }
}
