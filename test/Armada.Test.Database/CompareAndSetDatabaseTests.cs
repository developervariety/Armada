#nullable enable

namespace Armada.Test.Database
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Compare-and-set writes guard on a timestamp the row already holds. The guard compares the value it binds with
    /// the stored value, so it matches only while every write path stores a timestamp in the form a read hands back.
    /// Each case writes through one path, reads the row, and requires the compare-and-set to match that read, then
    /// requires a snapshot taken before the write to be refused.
    /// </summary>
    internal sealed class CompareAndSetDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        internal CompareAndSetDatabaseTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        /// <summary>
        /// The mission admission compare-and-set matches after every mission write path, through the same driver and
        /// a reopened one.
        /// </summary>
        internal async Task VerifyMissionAdmissionAfterEveryWriteAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("cas-mission", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "cas-mission", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "cas-mission", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "cas-mission", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "cas-mission", token).ConfigureAwait(false);
                Voyage voyage = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "cas-mission", token).ConfigureAwait(false);
                Mission mission = await fixture.CreateMissionAsync(tenant.Id, user.Id, voyage.Id, vessel.Id, captain.Id, "cas-mission", token).ConfigureAwait(false);

                Mission snapshot = await ReadMissionAsync(_Driver, mission.Id, token).ConfigureAwait(false);
                await ExpectAdmissionAsync(_Driver, snapshot, "after create").ConfigureAwait(false);

                Mission stale = await ReadMissionAsync(_Driver, mission.Id, token).ConfigureAwait(false);
                Mission updated = await ReadMissionAsync(_Driver, mission.Id, token).ConfigureAwait(false);
                updated.Priority = updated.Priority + 1;
                await _Driver.Missions.UpdateAsync(updated, token).ConfigureAwait(false);
                await ExpectAdmissionAsync(_Driver, await ReadMissionAsync(_Driver, mission.Id, token).ConfigureAwait(false), "after update").ConfigureAwait(false);
                DatabaseAssert.True(!await _Driver.Missions.TryRecordAdmissionAsync(stale, NewObservation(stale), token).ConfigureAwait(false),
                    "A snapshot read before the update is refused");

                Mission guarded = await ReadMissionAsync(_Driver, mission.Id, token).ConfigureAwait(false);
                guarded.Priority = guarded.Priority + 1;
                DatabaseAssert.True(await _Driver.Missions.TryUpdateIfStatusAsync(guarded, MissionStatusEnum.Pending, token).ConfigureAwait(false),
                    "Status-guarded update applies");
                await ExpectAdmissionAsync(_Driver, await ReadMissionAsync(_Driver, mission.Id, token).ConfigureAwait(false), "after status-guarded update").ConfigureAwait(false);

                await _Driver.Missions.UpdateHeartbeatAsync(mission.Id, token).ConfigureAwait(false);
                await ExpectAdmissionAsync(_Driver, await ReadMissionAsync(_Driver, mission.Id, token).ConfigureAwait(false), "after heartbeat").ConfigureAwait(false);

                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    await ExpectAdmissionAsync(reopened, await ReadMissionAsync(reopened, mission.Id, token).ConfigureAwait(false), "after admission, through a reopened driver").ConfigureAwait(false);
                }
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }

            async Task ExpectAdmissionAsync(DatabaseDriver driver, Mission read, string label)
            {
                DatabaseAssert.True(await driver.Missions.TryRecordAdmissionAsync(read, NewObservation(read), token).ConfigureAwait(false),
                    "The admission compare-and-set matches the row read " + label);
            }
        }

        /// <summary>
        /// The model endpoint health compare-and-set matches the last update time a read hands back, after a create
        /// and after an update, and refuses a time read before the update.
        /// </summary>
        internal async Task VerifyModelEndpointHealthAfterEveryWriteAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            string? endpointId = null;
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("cas-endpoint", token: token).ConfigureAwait(false);
                ModelEndpoint created = await _Driver.ModelEndpoints.CreateAsync(new ModelEndpoint
                {
                    Id = "mep_cas_" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    TenantId = tenant.Id,
                    Name = "Compare-and-set endpoint",
                    BaseUrl = "http://localhost:9999/v1"
                }, token).ConfigureAwait(false);
                endpointId = created.Id;

                ModelEndpoint afterCreate = await ReadEndpointAsync(_Driver, endpointId, token).ConfigureAwait(false);
                await ExpectHealthAsync(_Driver, afterCreate, afterCreate.LastUpdateUtc, true, "after create").ConfigureAwait(false);

                ModelEndpoint stale = await ReadEndpointAsync(_Driver, endpointId, token).ConfigureAwait(false);
                ModelEndpoint changed = await ReadEndpointAsync(_Driver, endpointId, token).ConfigureAwait(false);
                changed.Name = "Compare-and-set endpoint, renamed";
                // The endpoint service stamps the update time; the driver writes the time it is given.
                changed.LastUpdateUtc = DateTime.UtcNow;
                await _Driver.ModelEndpoints.UpdateAsync(changed, token).ConfigureAwait(false);
                ModelEndpoint afterUpdate = await ReadEndpointAsync(_Driver, endpointId, token).ConfigureAwait(false);
                await ExpectHealthAsync(_Driver, afterUpdate, stale.LastUpdateUtc, false, "with a time read before the update").ConfigureAwait(false);
                ModelEndpoint current = await ReadEndpointAsync(_Driver, endpointId, token).ConfigureAwait(false);
                await ExpectHealthAsync(_Driver, current, current.LastUpdateUtc, true, "after update").ConfigureAwait(false);

                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    ModelEndpoint afterHealth = await ReadEndpointAsync(reopened, endpointId, token).ConfigureAwait(false);
                    await ExpectHealthAsync(reopened, afterHealth, afterHealth.LastUpdateUtc, true, "after a health write, through a reopened driver").ConfigureAwait(false);
                }
            }
            finally
            {
                if (endpointId != null && !_NoCleanup) await _Driver.ModelEndpoints.DeleteAsync(endpointId, token).ConfigureAwait(false);
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }

            async Task ExpectHealthAsync(DatabaseDriver driver, ModelEndpoint read, DateTime expected, bool matches, string label)
            {
                read.HealthStatus = EndpointHealthStatusEnum.Healthy;
                read.LastHealthCheckUtc = DateTime.UtcNow;
                read.LastUpdateUtc = DateTime.UtcNow;
                bool applied = await driver.ModelEndpoints.UpdateHealthAsync(read, expected, token).ConfigureAwait(false);
                DatabaseAssert.Equal(matches, applied, "The endpoint health compare-and-set " + (matches ? "matches" : "refuses") + " " + label);
            }
        }

        private static MissionAdmissionObservation NewObservation(Mission read)
        {
            return new MissionAdmissionObservation
            {
                MissionId = read.Id,
                TenantId = read.TenantId,
                UserId = read.UserId,
                VesselId = read.VesselId,
                Admit = false,
                GlobalActiveWorkloads = 3,
                GlobalWorkloadLimit = 3,
                GlobalLimitReached = true,
                Reason = "Compare-and-set round trip"
            };
        }

        private static async Task<Mission> ReadMissionAsync(DatabaseDriver driver, string id, CancellationToken token)
        {
            return DatabaseAssert.NotNull(await driver.Missions.ReadAsync(id, token).ConfigureAwait(false), "Mission " + id);
        }

        private static async Task<ModelEndpoint> ReadEndpointAsync(DatabaseDriver driver, string id, CancellationToken token)
        {
            return DatabaseAssert.NotNull(await driver.ModelEndpoints.ReadAsync(id, token).ConfigureAwait(false), "Model endpoint " + id);
        }
    }
}
