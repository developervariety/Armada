namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Validates and resolves project profiles for vessels. Resolution mirrors the workflow-profile
    /// precedent: an explicit id, else the best Vessel-scoped match, else Fleet-scoped, else Global.
    /// </summary>
    public class ProjectProfileService
    {
        #region Private-Members

        private readonly string _Header = "[ProjectProfileService] ";
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver. Required.</param>
        /// <param name="logging">Logging module. Required.</param>
        public ProjectProfileService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Validate a project profile: scope/fleet/vessel consistency, referenced entity existence,
        /// and persona-override well-formedness.
        /// </summary>
        /// <param name="profile">The profile to validate.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The validation result.</returns>
        public async Task<ProjectProfileValidationResult> ValidateAsync(ProjectProfile profile, CancellationToken token = default)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            ProjectProfileValidationResult result = new ProjectProfileValidationResult();

            if (String.IsNullOrWhiteSpace(profile.Name))
                result.Errors.Add("Name is required.");

            switch (profile.Scope)
            {
                case ProjectProfileScopeEnum.Global:
                    break;
                case ProjectProfileScopeEnum.Fleet:
                    if (String.IsNullOrWhiteSpace(profile.FleetId))
                    {
                        result.Errors.Add("Fleet-scoped profiles require a fleetId.");
                    }
                    else
                    {
                        Fleet? fleet = await _Database.Fleets.ReadAsync(profile.FleetId, token).ConfigureAwait(false);
                        if (fleet == null)
                            result.Errors.Add("Fleet not found for fleet-scoped profile.");
                        else if (!String.IsNullOrWhiteSpace(profile.TenantId)
                            && !String.Equals(fleet.TenantId, profile.TenantId, StringComparison.Ordinal))
                            result.Errors.Add("Fleet does not belong to the project profile tenant.");
                    }
                    break;
                case ProjectProfileScopeEnum.Vessel:
                    if (String.IsNullOrWhiteSpace(profile.VesselId))
                    {
                        result.Errors.Add("Vessel-scoped profiles require a vesselId.");
                    }
                    else
                    {
                        Vessel? vessel = await _Database.Vessels.ReadAsync(profile.VesselId, token).ConfigureAwait(false);
                        if (vessel == null)
                            result.Errors.Add("Vessel not found for vessel-scoped profile.");
                        else if (!String.IsNullOrWhiteSpace(profile.TenantId)
                            && !String.Equals(vessel.TenantId, profile.TenantId, StringComparison.Ordinal))
                            result.Errors.Add("Vessel does not belong to the project profile tenant.");
                    }
                    break;
            }

            if (profile.Scope != ProjectProfileScopeEnum.Fleet && !String.IsNullOrWhiteSpace(profile.FleetId))
                result.Warnings.Add("fleetId is set but the profile scope is not Fleet.");
            if (profile.Scope != ProjectProfileScopeEnum.Vessel && !String.IsNullOrWhiteSpace(profile.VesselId))
                result.Warnings.Add("vesselId is set but the profile scope is not Vessel.");

            List<PersonaOverride> overrides = profile.PersonaOverrides ?? new List<PersonaOverride>();
            if (overrides.Any(item => item == null || String.IsNullOrWhiteSpace(item.PersonaName)))
                result.Errors.Add("Persona overrides must each specify a persona name.");
            if (overrides.Where(item => item != null && !String.IsNullOrWhiteSpace(item.PersonaName))
                    .GroupBy(item => item.PersonaName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() > 1))
                result.Errors.Add("Persona overrides must reference each persona at most once.");

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        /// <summary>
        /// Resolve the best matching project profile for a vessel.
        /// </summary>
        /// <param name="auth">Authentication context. Required.</param>
        /// <param name="vessel">The target vessel. Required.</param>
        /// <param name="explicitProfileId">Optional explicit profile id override.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The resolved profile, or null when none matches.</returns>
        public async Task<ProjectProfile?> ResolveForVesselAsync(
            AuthContext auth,
            Vessel vessel,
            string? explicitProfileId = null,
            CancellationToken token = default)
        {
            ProjectProfileResolutionResult result = await ResolveWithModeForVesselAsync(auth, vessel, explicitProfileId, token).ConfigureAwait(false);
            return result.Profile;
        }

        /// <summary>
        /// Resolve the best matching project profile for a vessel, including the mode of selection.
        /// </summary>
        /// <param name="auth">Authentication context. Required.</param>
        /// <param name="vessel">The target vessel. Required.</param>
        /// <param name="explicitProfileId">Optional explicit profile id override.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The resolution result.</returns>
        public async Task<ProjectProfileResolutionResult> ResolveWithModeForVesselAsync(
            AuthContext auth,
            Vessel vessel,
            string? explicitProfileId = null,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            ProjectProfileQuery query = new ProjectProfileQuery
            {
                TenantId = auth.IsAdmin ? vessel.TenantId : auth.TenantId,
                UserId = null,
                Active = true,
                PageNumber = 1,
                PageSize = 1000
            };

            if (!String.IsNullOrWhiteSpace(explicitProfileId))
            {
                ProjectProfile? explicitProfile = await _Database.ProjectProfiles.ReadAsync(explicitProfileId, query, token).ConfigureAwait(false);
                return new ProjectProfileResolutionResult
                {
                    Profile = explicitProfile != null && explicitProfile.Active ? explicitProfile : null,
                    Mode = ProjectProfileResolutionModeEnum.Explicit
                };
            }

            List<ProjectProfile> candidates = await _Database.ProjectProfiles.EnumerateAllAsync(query, token).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return new ProjectProfileResolutionResult
                {
                    Profile = null,
                    Mode = ProjectProfileResolutionModeEnum.None
                };
            }

            ProjectProfile? match = ChooseBestMatch(
                candidates.Where(profile => profile.Scope == ProjectProfileScopeEnum.Vessel
                    && String.Equals(profile.VesselId, vessel.Id, StringComparison.Ordinal)).ToList());
            if (match != null)
            {
                return new ProjectProfileResolutionResult
                {
                    Profile = match,
                    Mode = ProjectProfileResolutionModeEnum.Vessel
                };
            }

            if (!String.IsNullOrWhiteSpace(vessel.FleetId))
            {
                match = ChooseBestMatch(
                    candidates.Where(profile => profile.Scope == ProjectProfileScopeEnum.Fleet
                        && String.Equals(profile.FleetId, vessel.FleetId, StringComparison.Ordinal)).ToList());
                if (match != null)
                {
                    return new ProjectProfileResolutionResult
                    {
                        Profile = match,
                        Mode = ProjectProfileResolutionModeEnum.Fleet
                    };
                }
            }

            match = ChooseBestMatch(candidates.Where(profile => profile.Scope == ProjectProfileScopeEnum.Global).ToList());
            return new ProjectProfileResolutionResult
            {
                Profile = match,
                Mode = match != null ? ProjectProfileResolutionModeEnum.Global : ProjectProfileResolutionModeEnum.None
            };
        }

        #endregion

        #region Private-Methods

        private static ProjectProfile? ChooseBestMatch(List<ProjectProfile> profiles)
        {
            return profiles
                .Where(profile => profile.Active)
                .OrderByDescending(profile => profile.IsDefault)
                .ThenByDescending(profile => profile.LastUpdateUtc)
                .FirstOrDefault();
        }

        #endregion
    }
}
