namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Authorization;
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
        /// Validate a workflow profile and return preview information. Fleets and vessels are read without a
        /// caller, so this is for server paths only; a request validates through <see cref="ValidateForCallerAsync"/>.
        /// </summary>
        public Task<WorkflowProfileValidationResult> ValidateAsync(WorkflowProfile profile, CancellationToken token = default)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            return ValidateCoreAsync(
                profile,
                fleetId => _Database.Fleets.ReadAsync(fleetId, token),
                vesselId => _Database.Vessels.ReadAsync(vesselId, token));
        }

        /// <summary>
        /// Validate a requested profile exactly as <see cref="CreateAsync"/> would store it for this caller: the
        /// same field normalization and tenant, and fleets and vessels read within the caller's scope, so a
        /// record outside that scope reads as missing and validation never reveals that it exists.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="requested">Requested profile.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Validation result.</returns>
        public async Task<WorkflowProfileValidationResult> ValidateForCallerAsync(AuthContext caller, WorkflowProfile requested, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (requested == null) throw new ArgumentNullException(nameof(requested));
            WorkflowProfile candidate = new WorkflowProfile();
            CopyAllowedFields(requested, candidate);
            candidate.TenantId = await ResolveCreateTenantAsync(caller, requested, candidate, token).ConfigureAwait(false);
            return await ValidateScopedAsync(caller, candidate, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Read a workflow profile by id within the caller's scope: a global administrator reads any profile,
        /// anyone else only the profiles of its own tenant.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="id">Profile identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The profile, or null when absent or outside the caller's scope.</returns>
        public Task<WorkflowProfile?> ReadForCallerAsync(AuthContext caller, string? id, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (String.IsNullOrWhiteSpace(id)) return Task.FromResult<WorkflowProfile?>(null);
            return _Database.WorkflowProfiles.ReadAsync(id!.Trim(), ScopedQuery(caller), token);
        }

        /// <summary>
        /// Create a workflow profile. Only allow-listed fields are read, ids and commands are trimmed, a global
        /// administrator's profile belongs to the tenant it names (or the tenant of the fleet or vessel it is
        /// scoped to), anyone else's to its own tenant, and a new default clears the other defaults of its scope.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="requested">Requested profile.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Write result; a validation refusal carries the validation result as its details.</returns>
        public async Task<RecordWriteResult<WorkflowProfile>> CreateAsync(AuthContext caller, WorkflowProfile? requested, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (!CanManage(caller)) return RecordWriteResult<WorkflowProfile>.Forbidden(ManageRefusal);
            if (requested == null) return RecordWriteResult<WorkflowProfile>.Invalid("profile is required");

            WorkflowProfile profile = new WorkflowProfile();
            CopyAllowedFields(requested, profile);
            profile.TenantId = await ResolveCreateTenantAsync(caller, requested, profile, token).ConfigureAwait(false);
            profile.UserId = OwnershipPolicy.UserOf(caller);

            WorkflowProfileValidationResult validation = await ValidateScopedAsync(caller, profile, token).ConfigureAwait(false);
            if (!validation.IsValid) return Refuse(validation);

            await ClearOtherDefaultsAsync(profile, token).ConfigureAwait(false);
            WorkflowProfile created = await _Database.WorkflowProfiles.CreateAsync(profile, token).ConfigureAwait(false);
            return RecordWriteResult<WorkflowProfile>.Success(created);
        }

        /// <summary>
        /// Replace a workflow profile with a complete record. Every allow-listed field takes the requested
        /// value; identity, owner, tenant and creation time are kept.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="id">Profile identifier.</param>
        /// <param name="requested">Requested profile.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Write result; a validation refusal carries the validation result as its details.</returns>
        public async Task<RecordWriteResult<WorkflowProfile>> ReplaceAsync(AuthContext caller, string? id, WorkflowProfile? requested, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (!CanManage(caller)) return RecordWriteResult<WorkflowProfile>.Forbidden(ManageRefusal);
            if (String.IsNullOrWhiteSpace(id)) return RecordWriteResult<WorkflowProfile>.Invalid("workflowProfileId is required");
            if (requested == null) return RecordWriteResult<WorkflowProfile>.Invalid("profile is required");

            WorkflowProfile? existing = await ReadForCallerAsync(caller, id, token).ConfigureAwait(false);
            if (existing == null) return RecordWriteResult<WorkflowProfile>.NotFound("Workflow profile not found");

            CopyAllowedFields(requested, existing);
            existing.LastUpdateUtc = DateTime.UtcNow;

            WorkflowProfileValidationResult validation = await ValidateScopedAsync(caller, existing, token).ConfigureAwait(false);
            if (!validation.IsValid) return Refuse(validation);

            await ClearOtherDefaultsAsync(existing, token).ConfigureAwait(false);
            WorkflowProfile updated = await _Database.WorkflowProfiles.UpdateAsync(existing, token).ConfigureAwait(false);
            return RecordWriteResult<WorkflowProfile>.Success(updated);
        }

        /// <summary>
        /// Delete a workflow profile within the caller's scope.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="id">Profile identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Write result carrying the deleted profile.</returns>
        public async Task<RecordWriteResult<WorkflowProfile>> DeleteAsync(AuthContext caller, string? id, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (!CanManage(caller)) return RecordWriteResult<WorkflowProfile>.Forbidden(ManageRefusal);
            WorkflowProfile? existing = await ReadForCallerAsync(caller, id, token).ConfigureAwait(false);
            if (existing == null) return RecordWriteResult<WorkflowProfile>.NotFound("Workflow profile not found");

            await _Database.WorkflowProfiles.DeleteAsync(existing.Id, ScopedQuery(caller), token).ConfigureAwait(false);
            return RecordWriteResult<WorkflowProfile>.Success(existing);
        }

        private async Task<WorkflowProfileValidationResult> ValidateCoreAsync(
            WorkflowProfile profile,
            Func<string, Task<Fleet?>> readFleet,
            Func<string, Task<Vessel?>> readVessel)
        {
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
                        Fleet? fleet = await readFleet(profile.FleetId).ConfigureAwait(false);
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
                        Vessel? vessel = await readVessel(profile.VesselId).ConfigureAwait(false);
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
            result.CommandPreviews = BuildCommandPreviews(profile);

            if (availableTypes.Count == 0)
                result.Errors.Add("At least one build, test, release, deploy, verification, migration, security, or performance command is required.");

            if (profile.Environments.GroupBy(env => env.EnvironmentName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                result.Errors.Add("Environment names must be unique within a workflow profile.");

            List<WorkflowInputReference> inputs = profile.RequiredInputs ?? new List<WorkflowInputReference>();
            if (inputs.Any(input => String.IsNullOrWhiteSpace(input.Key)))
                result.Errors.Add("Required input references must include a key or path.");
            if (inputs.GroupBy(input => WorkflowInputReference.Serialize(input), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                result.Errors.Add("Required input references must be unique within a workflow profile.");
            List<string> knownEnvironmentNames = profile.Environments
                .Where(environment => !String.IsNullOrWhiteSpace(environment.EnvironmentName))
                .Select(environment => environment.EnvironmentName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> unknownInputEnvironments = inputs
                .Where(input => !String.IsNullOrWhiteSpace(input.EnvironmentName))
                .Select(input => input.EnvironmentName!.Trim())
                .Where(environmentName => !knownEnvironmentNames.Contains(environmentName, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unknownInputEnvironments.Count > 0)
            {
                result.Errors.Add("Required input references are scoped to unknown environments: " + String.Join(", ", unknownInputEnvironments) + ".");
            }

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
            WorkflowProfileResolutionResult result = await ResolveWithModeForVesselAsync(auth, vessel, explicitProfileId, token).ConfigureAwait(false);
            return result.Profile;
        }

        /// <summary>
        /// Build a fully resolved preview for a target vessel.
        /// </summary>
        public async Task<WorkflowProfileResolutionPreviewResult?> PreviewForVesselAsync(
            AuthContext auth,
            Vessel vessel,
            string? explicitProfileId = null,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            WorkflowProfileResolutionResult resolved = await ResolveWithModeForVesselAsync(auth, vessel, explicitProfileId, token).ConfigureAwait(false);
            if (resolved.Profile == null)
                return null;

            return new WorkflowProfileResolutionPreviewResult
            {
                ResolvedProfile = resolved.Profile,
                ResolutionMode = resolved.Mode,
                AvailableCheckTypes = GetAvailableCheckTypeNames(resolved.Profile),
                CommandPreviews = BuildCommandPreviews(resolved.Profile)
            };
        }

        /// <summary>
        /// Build fully resolved command previews for a workflow profile.
        /// </summary>
        public static List<WorkflowProfileCommandPreview> BuildCommandPreviews(WorkflowProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            List<WorkflowProfileCommandPreview> results = new List<WorkflowProfileCommandPreview>();
            AddPreview(results, CheckRunTypeEnum.Lint, profile.LintCommand);
            AddPreview(results, CheckRunTypeEnum.Build, profile.BuildCommand);
            AddPreview(results, CheckRunTypeEnum.UnitTest, profile.UnitTestCommand);
            AddPreview(results, CheckRunTypeEnum.IntegrationTest, profile.IntegrationTestCommand);
            AddPreview(results, CheckRunTypeEnum.E2ETest, profile.E2ETestCommand);
            AddPreview(results, CheckRunTypeEnum.Migration, profile.MigrationCommand);
            AddPreview(results, CheckRunTypeEnum.SecurityScan, profile.SecurityScanCommand);
            AddPreview(results, CheckRunTypeEnum.Performance, profile.PerformanceCommand);
            AddPreview(results, CheckRunTypeEnum.Package, profile.PackageCommand);
            AddPreview(results, CheckRunTypeEnum.DeploymentVerification, profile.DeploymentVerificationCommand);
            AddPreview(results, CheckRunTypeEnum.RollbackVerification, profile.RollbackVerificationCommand);
            AddPreview(results, CheckRunTypeEnum.PublishArtifact, profile.PublishArtifactCommand);
            AddPreview(results, CheckRunTypeEnum.ReleaseVersioning, profile.ReleaseVersioningCommand);
            AddPreview(results, CheckRunTypeEnum.Changelog, profile.ChangelogGenerationCommand);

            foreach (WorkflowEnvironmentProfile environment in profile.Environments ?? new List<WorkflowEnvironmentProfile>())
            {
                string? environmentName = NullIfWhiteSpace(environment.EnvironmentName);
                AddPreview(results, CheckRunTypeEnum.Deploy, environment.DeployCommand, environmentName);
                AddPreview(results, CheckRunTypeEnum.Rollback, environment.RollbackCommand, environmentName);
                AddPreview(results, CheckRunTypeEnum.SmokeTest, environment.SmokeTestCommand, environmentName);
                AddPreview(results, CheckRunTypeEnum.HealthCheck, environment.HealthCheckCommand, environmentName);
                AddPreview(results, CheckRunTypeEnum.DeploymentVerification, environment.DeploymentVerificationCommand, environmentName);
                AddPreview(results, CheckRunTypeEnum.RollbackVerification, environment.RollbackVerificationCommand, environmentName);
            }

            return results;
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
                case CheckRunTypeEnum.Migration:
                    return NullIfWhiteSpace(profile.MigrationCommand);
                case CheckRunTypeEnum.SecurityScan:
                    return NullIfWhiteSpace(profile.SecurityScanCommand);
                case CheckRunTypeEnum.Performance:
                    return NullIfWhiteSpace(profile.PerformanceCommand);
                case CheckRunTypeEnum.Package:
                    return NullIfWhiteSpace(profile.PackageCommand);
                case CheckRunTypeEnum.DeploymentVerification:
                    return ResolveEnvironmentCommand(profile, environmentName, env => env.DeploymentVerificationCommand)
                        ?? NullIfWhiteSpace(profile.DeploymentVerificationCommand);
                case CheckRunTypeEnum.RollbackVerification:
                    return ResolveEnvironmentCommand(profile, environmentName, env => env.RollbackVerificationCommand)
                        ?? NullIfWhiteSpace(profile.RollbackVerificationCommand);
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
            AddIfPresent(results, CheckRunTypeEnum.Migration, profile.MigrationCommand);
            AddIfPresent(results, CheckRunTypeEnum.SecurityScan, profile.SecurityScanCommand);
            AddIfPresent(results, CheckRunTypeEnum.Performance, profile.PerformanceCommand);
            AddIfPresent(results, CheckRunTypeEnum.Package, profile.PackageCommand);
            AddIfPresent(results, CheckRunTypeEnum.DeploymentVerification, profile.DeploymentVerificationCommand);
            AddIfPresent(results, CheckRunTypeEnum.RollbackVerification, profile.RollbackVerificationCommand);
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
            if (profile.Environments.Any(env => !String.IsNullOrWhiteSpace(env.DeploymentVerificationCommand)))
                results.Add(CheckRunTypeEnum.DeploymentVerification.ToString());
            if (profile.Environments.Any(env => !String.IsNullOrWhiteSpace(env.RollbackVerificationCommand)))
                results.Add(CheckRunTypeEnum.RollbackVerification.ToString());

            return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddIfPresent(List<string> values, CheckRunTypeEnum type, string? command)
        {
            if (!String.IsNullOrWhiteSpace(command))
                values.Add(type.ToString());
        }

        private async Task<WorkflowProfileResolutionResult> ResolveWithModeForVesselAsync(
            AuthContext auth,
            Vessel vessel,
            string? explicitProfileId,
            CancellationToken token)
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
                {
                    return new WorkflowProfileResolutionResult
                    {
                        Profile = explicitProfile,
                        Mode = WorkflowProfileResolutionModeEnum.Explicit
                    };
                }

                return new WorkflowProfileResolutionResult
                {
                    Profile = null,
                    Mode = WorkflowProfileResolutionModeEnum.Explicit
                };
            }

            List<WorkflowProfile> candidates = await _Database.WorkflowProfiles.EnumerateAllAsync(query, token).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return new WorkflowProfileResolutionResult
                {
                    Profile = null,
                    Mode = WorkflowProfileResolutionModeEnum.Global
                };
            }

            WorkflowProfile? match = ChooseBestMatch(
                candidates.Where(profile => profile.Scope == WorkflowProfileScopeEnum.Vessel
                    && String.Equals(profile.VesselId, vessel.Id, StringComparison.Ordinal)).ToList());
            if (match != null)
            {
                return new WorkflowProfileResolutionResult
                {
                    Profile = match,
                    Mode = WorkflowProfileResolutionModeEnum.Vessel
                };
            }

            if (!String.IsNullOrWhiteSpace(vessel.FleetId))
            {
                match = ChooseBestMatch(
                    candidates.Where(profile => profile.Scope == WorkflowProfileScopeEnum.Fleet
                        && String.Equals(profile.FleetId, vessel.FleetId, StringComparison.Ordinal)).ToList());
                if (match != null)
                {
                    return new WorkflowProfileResolutionResult
                    {
                        Profile = match,
                        Mode = WorkflowProfileResolutionModeEnum.Fleet
                    };
                }
            }

            match = ChooseBestMatch(candidates.Where(profile => profile.Scope == WorkflowProfileScopeEnum.Global).ToList());
            return new WorkflowProfileResolutionResult
            {
                Profile = match,
                Mode = WorkflowProfileResolutionModeEnum.Global
            };
        }

        private static void AddPreview(List<WorkflowProfileCommandPreview> values, CheckRunTypeEnum type, string? command, string? environmentName = null)
        {
            string? normalized = NullIfWhiteSpace(command);
            if (normalized == null)
                return;

            values.Add(new WorkflowProfileCommandPreview
            {
                CheckType = type,
                EnvironmentName = NullIfWhiteSpace(environmentName),
                Command = normalized
            });
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

        private const string ManageRefusal = "Only tenant administrators can manage workflow profiles";

        private static bool CanManage(AuthContext caller)
        {
            return caller.IsAdmin || caller.IsTenantAdmin;
        }

        private static WorkflowProfileQuery? ScopedQuery(AuthContext caller)
        {
            if (caller.IsAdmin) return null;
            return new WorkflowProfileQuery { TenantId = OwnershipPolicy.TenantOf(caller) };
        }

        private static RecordWriteResult<WorkflowProfile> Refuse(WorkflowProfileValidationResult validation)
        {
            RecordWriteResult<WorkflowProfile> refusal = RecordWriteResult<WorkflowProfile>.Invalid(String.Join(" ", validation.Errors));
            refusal.Details = validation;
            return refusal;
        }

        private Task<WorkflowProfileValidationResult> ValidateScopedAsync(AuthContext caller, WorkflowProfile profile, CancellationToken token)
        {
            return ValidateCoreAsync(
                profile,
                fleetId => CallerScopedRead.ReadFleetAsync(_Database, caller, fleetId, token),
                vesselId => CallerScopedRead.ReadVesselAsync(_Database, caller, vesselId, token));
        }

        /// <summary>
        /// Tenant a new profile belongs to. A global administrator's named tenant wins, then the tenant of the
        /// fleet or vessel the profile is scoped to; everyone else creates in its own tenant.
        /// </summary>
        private async Task<string> ResolveCreateTenantAsync(AuthContext caller, WorkflowProfile requested, WorkflowProfile candidate, CancellationToken token)
        {
            if (!caller.IsAdmin) return OwnershipPolicy.TenantOf(caller);
            string? named = NullIfWhiteSpace(requested.TenantId);
            if (named != null) return named;

            if (candidate.Scope == WorkflowProfileScopeEnum.Fleet && candidate.FleetId != null)
            {
                Fleet? fleet = await CallerScopedRead.ReadFleetAsync(_Database, caller, candidate.FleetId, token).ConfigureAwait(false);
                if (fleet != null) return OwnershipPolicy.TenantOfRecord(fleet.TenantId);
            }

            if (candidate.Scope == WorkflowProfileScopeEnum.Vessel && candidate.VesselId != null)
            {
                Vessel? vessel = await CallerScopedRead.ReadVesselAsync(_Database, caller, candidate.VesselId, token).ConfigureAwait(false);
                if (vessel != null) return OwnershipPolicy.TenantOfRecord(vessel.TenantId);
            }

            return OwnershipPolicy.TenantOf(caller);
        }

        /// <summary>
        /// Copy the caller-settable fields, trimming ids and commands and dropping blank list entries.
        /// </summary>
        private static void CopyAllowedFields(WorkflowProfile source, WorkflowProfile target)
        {
            target.Name = (source.Name ?? "").Trim();
            target.Description = NullIfWhiteSpace(source.Description);
            target.Scope = source.Scope;
            target.FleetId = NullIfWhiteSpace(source.FleetId);
            target.VesselId = NullIfWhiteSpace(source.VesselId);
            target.IsDefault = source.IsDefault;
            target.Active = source.Active;
            target.LanguageHints = (source.LanguageHints ?? new List<string>())
                .Where(hint => !String.IsNullOrWhiteSpace(hint))
                .Select(hint => hint.Trim())
                .ToList();
            target.EnvironmentVariables = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> variable in source.EnvironmentVariables ?? new Dictionary<string, string>())
            {
                if (String.IsNullOrWhiteSpace(variable.Key)) continue;
                target.EnvironmentVariables[variable.Key.Trim()] = variable.Value ?? "";
            }
            target.LintCommand = NullIfWhiteSpace(source.LintCommand);
            target.BuildCommand = NullIfWhiteSpace(source.BuildCommand);
            target.UnitTestCommand = NullIfWhiteSpace(source.UnitTestCommand);
            target.ContainerlessUnitTestCommand = NullIfWhiteSpace(source.ContainerlessUnitTestCommand);
            target.IntegrationTestCommand = NullIfWhiteSpace(source.IntegrationTestCommand);
            target.E2ETestCommand = NullIfWhiteSpace(source.E2ETestCommand);
            target.MigrationCommand = NullIfWhiteSpace(source.MigrationCommand);
            target.SecurityScanCommand = NullIfWhiteSpace(source.SecurityScanCommand);
            target.PerformanceCommand = NullIfWhiteSpace(source.PerformanceCommand);
            target.PackageCommand = NullIfWhiteSpace(source.PackageCommand);
            target.DeploymentVerificationCommand = NullIfWhiteSpace(source.DeploymentVerificationCommand);
            target.RollbackVerificationCommand = NullIfWhiteSpace(source.RollbackVerificationCommand);
            target.PublishArtifactCommand = NullIfWhiteSpace(source.PublishArtifactCommand);
            target.ReleaseVersioningCommand = NullIfWhiteSpace(source.ReleaseVersioningCommand);
            target.ChangelogGenerationCommand = NullIfWhiteSpace(source.ChangelogGenerationCommand);
            target.RequiredInputs = source.RequiredInputs ?? new List<WorkflowInputReference>();
            target.ExpectedArtifacts = (source.ExpectedArtifacts ?? new List<string>())
                .Where(artifact => !String.IsNullOrWhiteSpace(artifact))
                .Select(artifact => artifact.Trim())
                .ToList();
            target.Environments = source.Environments ?? new List<WorkflowEnvironmentProfile>();
        }

        /// <summary>
        /// A profile marked default is the only default of its tenant and scope target.
        /// </summary>
        private async Task ClearOtherDefaultsAsync(WorkflowProfile profile, CancellationToken token)
        {
            if (!profile.IsDefault) return;

            WorkflowProfileQuery query = new WorkflowProfileQuery
            {
                TenantId = profile.TenantId,
                Scope = profile.Scope,
                FleetId = profile.Scope == WorkflowProfileScopeEnum.Fleet ? profile.FleetId : null,
                VesselId = profile.Scope == WorkflowProfileScopeEnum.Vessel ? profile.VesselId : null,
                PageNumber = 1,
                PageSize = 1000
            };
            List<WorkflowProfile> peers = await _Database.WorkflowProfiles.EnumerateAllAsync(query, token).ConfigureAwait(false);
            foreach (WorkflowProfile peer in peers.Where(candidate => candidate.IsDefault && !String.Equals(candidate.Id, profile.Id, StringComparison.Ordinal)))
            {
                peer.IsDefault = false;
                peer.LastUpdateUtc = DateTime.UtcNow;
                await _Database.WorkflowProfiles.UpdateAsync(peer, token).ConfigureAwait(false);
            }
        }
    }
}
