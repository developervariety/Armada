namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Validates and resolves workflow profiles for vessels and fleets.
    /// </summary>
    public class WorkflowProfileService
    {
        private readonly string _Header = "[WorkflowProfileService] ";
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public WorkflowProfileService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <summary>
        /// Validate a workflow profile and return preview information.
        /// </summary>
        public async Task<WorkflowProfileValidationResult> ValidateAsync(WorkflowProfile profile, CancellationToken token = default)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            WorkflowProfileValidationResult result = new WorkflowProfileValidationResult();

            if (String.IsNullOrWhiteSpace(profile.Name))
                result.Errors.Add("Name is required.");

            switch (profile.Scope)
            {
                case WorkflowProfileScopeEnum.Global:
                    break;
                case WorkflowProfileScopeEnum.Fleet:
                    if (String.IsNullOrWhiteSpace(profile.FleetId))
                    {
                        result.Errors.Add("Fleet-scoped profiles require a fleetId.");
                    }
                    else
                    {
                        Fleet? fleet = await _Database.Fleets.ReadAsync(profile.FleetId, token).ConfigureAwait(false);
                        if (fleet == null)
                        {
                            result.Errors.Add("Fleet not found for fleet-scoped profile.");
                        }
                        else if (!String.IsNullOrWhiteSpace(profile.TenantId)
                            && !String.Equals(fleet.TenantId, profile.TenantId, StringComparison.Ordinal))
                        {
                            result.Errors.Add("Fleet does not belong to the workflow profile tenant.");
                        }
                    }
                    break;
                case WorkflowProfileScopeEnum.Vessel:
                    if (String.IsNullOrWhiteSpace(profile.VesselId))
                    {
                        result.Errors.Add("Vessel-scoped profiles require a vesselId.");
                    }
                    else
                    {
                        Vessel? vessel = await _Database.Vessels.ReadAsync(profile.VesselId, token).ConfigureAwait(false);
                        if (vessel == null)
                        {
                            result.Errors.Add("Vessel not found for vessel-scoped profile.");
                        }
                        else if (!String.IsNullOrWhiteSpace(profile.TenantId)
                            && !String.Equals(vessel.TenantId, profile.TenantId, StringComparison.Ordinal))
                        {
                            result.Errors.Add("Vessel does not belong to the workflow profile tenant.");
                        }
                    }
                    break;
            }

            if (profile.Scope != WorkflowProfileScopeEnum.Fleet && !String.IsNullOrWhiteSpace(profile.FleetId))
                result.Warnings.Add("fleetId is set but the profile scope is not Fleet.");
            if (profile.Scope != WorkflowProfileScopeEnum.Vessel && !String.IsNullOrWhiteSpace(profile.VesselId))
                result.Warnings.Add("vesselId is set but the profile scope is not Vessel.");

            List<string> availableTypes = GetAvailableCheckTypeNames(profile);
            result.AvailableCheckTypes = availableTypes;

            if (availableTypes.Count == 0)
                result.Errors.Add("At least one build, test, release, deploy, or verification command is required.");

            if (profile.Environments.GroupBy(env => env.EnvironmentName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                result.Errors.Add("Environment names must be unique within a workflow profile.");

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        /// <summary>
        /// Resolve the best matching workflow profile for a vessel.
        /// </summary>
        public async Task<WorkflowProfile?> ResolveForVesselAsync(
            AuthContext auth,
            Vessel vessel,
            string? explicitProfileId = null,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            WorkflowProfileQuery query = new WorkflowProfileQuery
            {
                TenantId = auth.IsAdmin ? vessel.TenantId : auth.TenantId,
                UserId = null,
                Active = true,
                PageNumber = 1,
                PageSize = 1000
            };

            if (!String.IsNullOrWhiteSpace(explicitProfileId))
            {
                WorkflowProfile? explicitProfile = await _Database.WorkflowProfiles.ReadAsync(explicitProfileId, query, token).ConfigureAwait(false);
                if (explicitProfile != null && explicitProfile.Active)
                    return explicitProfile;
                return null;
            }

            List<WorkflowProfile> candidates = await _Database.WorkflowProfiles.EnumerateAllAsync(query, token).ConfigureAwait(false);
            if (candidates.Count == 0) return null;

            WorkflowProfile? match = ChooseBestMatch(
                candidates.Where(profile => profile.Scope == WorkflowProfileScopeEnum.Vessel
                    && String.Equals(profile.VesselId, vessel.Id, StringComparison.Ordinal)).ToList());
            if (match != null) return match;

            if (!String.IsNullOrWhiteSpace(vessel.FleetId))
            {
                match = ChooseBestMatch(
                    candidates.Where(profile => profile.Scope == WorkflowProfileScopeEnum.Fleet
                        && String.Equals(profile.FleetId, vessel.FleetId, StringComparison.Ordinal)).ToList());
                if (match != null) return match;
            }

            match = ChooseBestMatch(candidates.Where(profile => profile.Scope == WorkflowProfileScopeEnum.Global).ToList());
            return match;
        }

        /// <summary>
        /// Resolve the command for a specific check type.
        /// </summary>
        public string? ResolveCommand(WorkflowProfile profile, CheckRunTypeEnum type, string? environmentName = null)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            switch (type)
            {
                case CheckRunTypeEnum.Lint:
                    return NullIfWhiteSpace(profile.LintCommand);
                case CheckRunTypeEnum.Build:
                    return NullIfWhiteSpace(profile.BuildCommand);
                case CheckRunTypeEnum.UnitTest:
                    return NullIfWhiteSpace(profile.UnitTestCommand);
                case CheckRunTypeEnum.IntegrationTest:
                    return NullIfWhiteSpace(profile.IntegrationTestCommand);
                case CheckRunTypeEnum.E2ETest:
                    return NullIfWhiteSpace(profile.E2ETestCommand);
                case CheckRunTypeEnum.Package:
                    return NullIfWhiteSpace(profile.PackageCommand);
                case CheckRunTypeEnum.PublishArtifact:
                    return NullIfWhiteSpace(profile.PublishArtifactCommand);
                case CheckRunTypeEnum.ReleaseVersioning:
                    return NullIfWhiteSpace(profile.ReleaseVersioningCommand);
                case CheckRunTypeEnum.Changelog:
                    return NullIfWhiteSpace(profile.ChangelogGenerationCommand);
                case CheckRunTypeEnum.Deploy:
                    return ResolveEnvironmentCommand(profile, environmentName, env => env.DeployCommand);
                case CheckRunTypeEnum.Rollback:
                    return ResolveEnvironmentCommand(profile, environmentName, env => env.RollbackCommand);
                case CheckRunTypeEnum.SmokeTest:
                    return ResolveEnvironmentCommand(profile, environmentName, env => env.SmokeTestCommand);
                case CheckRunTypeEnum.HealthCheck:
                    return ResolveEnvironmentCommand(profile, environmentName, env => env.HealthCheckCommand);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Get available check type names for a profile.
        /// </summary>
        public static List<string> GetAvailableCheckTypeNames(WorkflowProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            List<string> results = new List<string>();

            AddIfPresent(results, CheckRunTypeEnum.Lint, profile.LintCommand);
            AddIfPresent(results, CheckRunTypeEnum.Build, profile.BuildCommand);
            AddIfPresent(results, CheckRunTypeEnum.UnitTest, profile.UnitTestCommand);
            AddIfPresent(results, CheckRunTypeEnum.IntegrationTest, profile.IntegrationTestCommand);
            AddIfPresent(results, CheckRunTypeEnum.E2ETest, profile.E2ETestCommand);
            AddIfPresent(results, CheckRunTypeEnum.Package, profile.PackageCommand);
            AddIfPresent(results, CheckRunTypeEnum.PublishArtifact, profile.PublishArtifactCommand);
            AddIfPresent(results, CheckRunTypeEnum.ReleaseVersioning, profile.ReleaseVersioningCommand);
            AddIfPresent(results, CheckRunTypeEnum.Changelog, profile.ChangelogGenerationCommand);

            if (profile.Environments.Any(env => !String.IsNullOrWhiteSpace(env.DeployCommand)))
                results.Add(CheckRunTypeEnum.Deploy.ToString());
            if (profile.Environments.Any(env => !String.IsNullOrWhiteSpace(env.RollbackCommand)))
                results.Add(CheckRunTypeEnum.Rollback.ToString());
            if (profile.Environments.Any(env => !String.IsNullOrWhiteSpace(env.SmokeTestCommand)))
                results.Add(CheckRunTypeEnum.SmokeTest.ToString());
            if (profile.Environments.Any(env => !String.IsNullOrWhiteSpace(env.HealthCheckCommand)))
                results.Add(CheckRunTypeEnum.HealthCheck.ToString());

            return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddIfPresent(List<string> values, CheckRunTypeEnum type, string? command)
        {
            if (!String.IsNullOrWhiteSpace(command))
                values.Add(type.ToString());
        }

        private static WorkflowProfile? ChooseBestMatch(List<WorkflowProfile> profiles)
        {
            return profiles
                .Where(profile => profile.Active)
                .OrderByDescending(profile => profile.IsDefault)
                .ThenByDescending(profile => profile.LastUpdateUtc)
                .FirstOrDefault();
        }

        private string? ResolveEnvironmentCommand(
            WorkflowProfile profile,
            string? environmentName,
            Func<WorkflowEnvironmentProfile, string?> selector)
        {
            if (profile.Environments == null || profile.Environments.Count == 0)
                return null;

            WorkflowEnvironmentProfile? environment = null;
            if (!String.IsNullOrWhiteSpace(environmentName))
            {
                environment = profile.Environments.FirstOrDefault(env =>
                    String.Equals(env.EnvironmentName, environmentName, StringComparison.OrdinalIgnoreCase));
            }

            environment ??= profile.Environments.Count == 1 ? profile.Environments[0] : null;
            if (environment == null)
            {
                _Logging.Debug(_Header + "environment command could not be resolved because no unique environment matched");
                return null;
            }

            return NullIfWhiteSpace(selector(environment));
        }

        private static string? NullIfWhiteSpace(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
