namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Json;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;

    /// <summary>
    /// Background resolver for pending check gates that become eligible as work lands.
    /// </summary>
    public sealed class AutomaticCheckRunOrchestrator
    {
        private const int MaxChecksPerSweep = 3;
        private readonly string _Header = "[AutomaticCheckRunOrchestrator] ";
        private readonly DatabaseDriver _Database;
        private readonly CheckRunService _CheckRuns;
        private readonly ReleaseService _Releases;
        private readonly IncidentService _Incidents;
        private readonly LoggingModule _Logging;
        private readonly SemaphoreSlim _SweepGate = new SemaphoreSlim(1, 1);
        private readonly JsonSerializerOptions _JsonOptions = JsonDefaults.Web;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public AutomaticCheckRunOrchestrator(
            DatabaseDriver database,
            CheckRunService checkRuns,
            ReleaseService releases,
            IncidentService incidents,
            LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _CheckRuns = checkRuns ?? throw new ArgumentNullException(nameof(checkRuns));
            _Releases = releases ?? throw new ArgumentNullException(nameof(releases));
            _Incidents = incidents ?? throw new ArgumentNullException(nameof(incidents));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <summary>
        /// Start a non-blocking sweep if another sweep is not already running.
        /// </summary>
        public void TriggerBackgroundSweep(CancellationToken token = default)
        {
            if (!_SweepGate.Wait(0)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await RunSweepAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "sweep failed: " + ex.Message);
                }
                finally
                {
                    _SweepGate.Release();
                }
            }, token);
        }

        /// <summary>
        /// Execute one bounded sweep of eligible pending checks.
        /// </summary>
        public async Task<int> RunSweepAsync(CancellationToken token = default)
        {
            AuthContext auth = BuildSystemAuth();
            CheckRunQuery query = new CheckRunQuery
            {
                Status = CheckRunStatusEnum.Pending,
                Source = CheckRunSourceEnum.Armada,
                PageNumber = 1,
                PageSize = 200
            };

            EnumerationResult<CheckRun> page = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
            List<CheckRun> eligible = new List<CheckRun>();

            foreach (CheckRun run in page.Objects.OrderBy(run => run.CreatedUtc))
            {
                if (eligible.Count >= MaxChecksPerSweep) break;
                if (await IsEligibleAsync(run, token).ConfigureAwait(false))
                    eligible.Add(run);
            }

            if (eligible.Count == 0) return 0;

            _Logging.Info(_Header + "executing " + eligible.Count + " eligible pending check(s)");
            int executed = 0;
            foreach (CheckRun pending in eligible)
            {
                CheckRun result = await ExecutePendingAsync(auth, pending, token).ConfigureAwait(false);
                executed++;

                await RefreshLinkedReleasesAsync(auth, result, token).ConfigureAwait(false);
                if (result.Status == CheckRunStatusEnum.Failed)
                    await CreateFailureIncidentAsync(result, token).ConfigureAwait(false);
            }

            return executed;
        }

        private async Task<CheckRun> ExecutePendingAsync(AuthContext auth, CheckRun pending, CancellationToken token)
        {
            await WriteEventAsync(
                "check.auto_started",
                "Automated check started: " + (pending.Label ?? pending.Type.ToString()),
                pending,
                new { pending.Id, pending.Type, pending.Label },
                token).ConfigureAwait(false);

            CheckRun result = await _CheckRuns.RunPendingAsync(auth, pending.Id, token: token).ConfigureAwait(false);

            await WriteEventAsync(
                result.Status == CheckRunStatusEnum.Passed ? "check.auto_passed" : "check.auto_failed",
                "Automated check " + result.Status + ": " + (result.Label ?? result.Type.ToString()),
                result,
                new { result.Id, result.Type, result.Label, result.Status, result.ExitCode, result.Summary },
                token).ConfigureAwait(false);

            return result;
        }

        private async Task<bool> IsEligibleAsync(CheckRun run, CancellationToken token)
        {
            if (run.Status != CheckRunStatusEnum.Pending) return false;
            if (String.IsNullOrWhiteSpace(run.VesselId)) return false;
            if (run.Type == CheckRunTypeEnum.Deploy || run.Type == CheckRunTypeEnum.Rollback) return false;

            if (!String.IsNullOrWhiteSpace(run.DeploymentId))
                return false;

            if (!String.IsNullOrWhiteSpace(run.VoyageId))
            {
                Voyage? voyage = await _Database.Voyages.ReadAsync(run.VoyageId, token).ConfigureAwait(false);
                return voyage?.Status == VoyageStatusEnum.Complete;
            }

            if (!String.IsNullOrWhiteSpace(run.MissionId))
            {
                Mission? mission = await _Database.Missions.ReadAsync(run.MissionId, token).ConfigureAwait(false);
                return mission?.Status == MissionStatusEnum.Complete;
            }

            Release? release = await FindLinkedReleaseAsync(run.Id, token).ConfigureAwait(false);
            if (release != null)
                return await IsReleaseReadyForChecksAsync(release, token).ConfigureAwait(false);

            return await IsVesselIdleAsync(run.VesselId!, token).ConfigureAwait(false);
        }

        private async Task<bool> IsDeploymentCheckEligibleAsync(CheckRun run, CancellationToken token)
        {
            Deployment? deployment = await _Database.Deployments.ReadAsync(run.DeploymentId!, token: token).ConfigureAwait(false);
            if (deployment == null) return false;

            if (deployment.Status == DeploymentStatusEnum.PendingApproval
                || deployment.Status == DeploymentStatusEnum.Denied
                || deployment.Status == DeploymentStatusEnum.Failed)
                return false;

            if (run.Type == CheckRunTypeEnum.Deploy || run.Type == CheckRunTypeEnum.Rollback)
                return false;

            if (run.Type == CheckRunTypeEnum.RollbackVerification)
                return deployment.Status == DeploymentStatusEnum.RolledBack;

            return deployment.Status == DeploymentStatusEnum.Succeeded
                || deployment.Status == DeploymentStatusEnum.VerificationFailed;
        }

        private async Task<bool> IsReleaseReadyForChecksAsync(Release release, CancellationToken token)
        {
            if (release.Status == ReleaseStatusEnum.Shipped
                || release.Status == ReleaseStatusEnum.Failed
                || release.Status == ReleaseStatusEnum.RolledBack)
                return false;

            List<string> voyageIds = release.VoyageIds ?? new List<string>();
            List<string> missionIds = release.MissionIds ?? new List<string>();

            foreach (string voyageId in voyageIds)
            {
                Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
                if (voyage?.Status != VoyageStatusEnum.Complete)
                    return false;
            }

            foreach (string missionId in missionIds)
            {
                Mission? mission = await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
                if (mission?.Status != MissionStatusEnum.Complete)
                    return false;
            }

            return voyageIds.Count > 0 || missionIds.Count > 0;
        }

        private async Task<bool> IsVesselIdleAsync(string vesselId, CancellationToken token)
        {
            foreach (MissionStatusEnum status in new[]
            {
                MissionStatusEnum.Pending,
                MissionStatusEnum.Assigned,
                MissionStatusEnum.InProgress,
                MissionStatusEnum.WorkProduced,
                MissionStatusEnum.LandingFailed,
                MissionStatusEnum.Testing,
                MissionStatusEnum.Review,
                MissionStatusEnum.PullRequestOpen
            })
            {
                List<Mission> missions = await _Database.Missions.EnumerateByStatusAsync(status, token).ConfigureAwait(false);
                if (missions.Any(mission => String.Equals(mission.VesselId, vesselId, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            return true;
        }

        private async Task RefreshLinkedReleasesAsync(AuthContext auth, CheckRun run, CancellationToken token)
        {
            List<Release> releases = await FindLinkedReleasesAsync(run.Id, token).ConfigureAwait(false);
            foreach (Release release in releases)
            {
                try
                {
                    await _Releases.RefreshAsync(auth, release.Id, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not refresh release " + release.Id + " after check " + run.Id + ": " + ex.Message);
                }
            }
        }

        private async Task<Release?> FindLinkedReleaseAsync(string checkRunId, CancellationToken token)
        {
            List<Release> releases = await FindLinkedReleasesAsync(checkRunId, token).ConfigureAwait(false);
            return releases.FirstOrDefault();
        }

        private async Task<List<Release>> FindLinkedReleasesAsync(string checkRunId, CancellationToken token)
        {
            List<Release> matches = new List<Release>();
            ReleaseQuery query = new ReleaseQuery { PageNumber = 1, PageSize = 200 };
            while (true)
            {
                EnumerationResult<Release> page = await _Database.Releases.EnumerateAsync(query, token).ConfigureAwait(false);
                matches.AddRange(page.Objects.Where(release =>
                    release.CheckRunIds.Contains(checkRunId, StringComparer.OrdinalIgnoreCase)));
                if (page.Objects.Count < query.PageSize) break;
                query.PageNumber++;
            }

            return matches;
        }

        private async Task CreateFailureIncidentAsync(CheckRun run, CancellationToken token)
        {
            string title = "Automated check failed: " + (run.Label ?? run.Type.ToString());
            int recentFailureCount = await CountRecentFailuresAsync(run, token).ConfigureAwait(false);
            AuthContext incidentAuth = AuthContext.Authenticated(
                run.TenantId ?? Constants.DefaultTenantId,
                run.UserId ?? Constants.DefaultUserId,
                isAdmin: false,
                isTenantAdmin: true,
                authMethod: "System",
                principalDisplay: "Automated checks");

            IncidentUpsertRequest request = new IncidentUpsertRequest
            {
                Title = title,
                Summary = (run.Summary ?? "Automated check failed.")
                    + " Recent failures for this vessel/check type: " + recentFailureCount + ".",
                Status = IncidentStatusEnum.Open,
                Severity = recentFailureCount >= 2 ? IncidentSeverityEnum.High : IncidentSeverityEnum.Medium,
                VesselId = run.VesselId,
                CheckRunId = run.Id,
                MissionId = run.MissionId,
                VoyageId = run.VoyageId,
                DeploymentId = run.DeploymentId,
                EnvironmentName = run.EnvironmentName,
                ReleaseId = (await FindLinkedReleaseAsync(run.Id, token).ConfigureAwait(false))?.Id,
                Impact = "Release or deployment promotion is blocked until the failed check is resolved.",
                RootCause = "Automated check command failed or could not run.",
                RecoveryNotes = "Inspect check run " + run.Id + ", fix the failure, then retry or create a new passing check run."
            };

            try
            {
                await _Incidents.CreateAsync(incidentAuth, request, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not create incident for failed check " + run.Id + ": " + ex.Message);
            }
        }

        private async Task<int> CountRecentFailuresAsync(CheckRun run, CancellationToken token)
        {
            CheckRunQuery query = new CheckRunQuery
            {
                TenantId = run.TenantId,
                VesselId = run.VesselId,
                Type = run.Type,
                Status = CheckRunStatusEnum.Failed,
                FromUtc = DateTime.UtcNow.AddDays(-30),
                PageNumber = 1,
                PageSize = 500
            };

            EnumerationResult<CheckRun> page = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
            return page.TotalRecords > Int32.MaxValue ? Int32.MaxValue : Convert.ToInt32(page.TotalRecords);
        }

        private async Task WriteEventAsync(string eventType, string message, CheckRun run, object payload, CancellationToken token)
        {
            ArmadaEvent evt = new ArmadaEvent(eventType, message)
            {
                TenantId = run.TenantId,
                UserId = run.UserId,
                EntityType = "check_run",
                EntityId = run.Id,
                MissionId = run.MissionId,
                VesselId = run.VesselId,
                VoyageId = run.VoyageId,
                Payload = JsonSerializer.Serialize(payload, _JsonOptions)
            };

            await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }

        private static AuthContext BuildSystemAuth()
        {
            return AuthContext.Authenticated(
                Constants.DefaultTenantId,
                Constants.DefaultUserId,
                isAdmin: true,
                isTenantAdmin: true,
                authMethod: "System",
                principalDisplay: "Automated checks");
        }
    }
}
