#nullable enable

namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Pages of rows that share one creation time neither repeat a row nor drop one, in either order and at every scope.
    /// </summary>
    internal sealed class PageOrderDatabaseTests
    {
        private const int _Rows = 7;
        private const int _PageSize = 2;

        private readonly DatabaseDriver _Driver;
        private readonly bool _NoCleanup;

        internal PageOrderDatabaseTests(DatabaseDriver driver, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _NoCleanup = noCleanup;
        }

        /// <summary>
        /// Walk every page of missions, mission summaries, voyages, signals and events created at one instant and compare
        /// the walk with a single page of the same rows.
        /// </summary>
        internal async Task VerifyAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            List<Func<CancellationToken, Task>> cleanup = new List<Func<CancellationToken, Task>>();
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("page-order-tenant", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "page-order-user", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "page-order-fleet", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "page-order-vessel", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "page-order-captain", token).ConfigureAwait(false);
                DateTime instant = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

                List<Voyage> voyages = new List<Voyage>();
                List<Mission> missions = new List<Mission>();
                for (int i = 0; i < _Rows; i++)
                {
                    voyages.Add(await fixture.CreateVoyageAsync(tenant.Id, user.Id, "page-order-voyage", token, item => item.CreatedUtc = instant).ConfigureAwait(false));
                    missions.Add(await fixture.CreateMissionAsync(tenant.Id, user.Id, voyages[0].Id, vessel.Id, captain.Id, "page-order-mission", token, null, null, item => item.CreatedUtc = instant).ConfigureAwait(false));
                }

                DatabaseAssert.UtcInstant(instant, DatabaseAssert.NotNull(await _Driver.Missions.ReadAsync(missions[0].Id, token).ConfigureAwait(false), "Mission read").CreatedUtc, "Mission.CreatedUtc");
                DatabaseAssert.UtcInstant(instant, DatabaseAssert.NotNull(await _Driver.Voyages.ReadAsync(voyages[0].Id, token).ConfigureAwait(false), "Voyage read").CreatedUtc, "Voyage.CreatedUtc");

                List<string> signalIds = new List<string>();
                List<string> eventIds = new List<string>();
                for (int i = 0; i < _Rows; i++)
                {
                    Signal signal = new Signal(SignalTypeEnum.Nudge, "{\"message\":\"page order\"}") { TenantId = tenant.Id, UserId = user.Id, ToCaptainId = captain.Id, CreatedUtc = instant };
                    await _Driver.Signals.CreateAsync(signal, token).ConfigureAwait(false);
                    signalIds.Add(signal.Id);
                    cleanup.Add(ct => _Driver.Signals.DeleteAsync(signal.Id, ct));

                    ArmadaEvent evt = new ArmadaEvent("page.order", "Page order") { TenantId = tenant.Id, UserId = user.Id, MissionId = missions[0].Id, CreatedUtc = instant };
                    await _Driver.Events.CreateAsync(evt, token).ConfigureAwait(false);
                    eventIds.Add(evt.Id);
                    cleanup.Add(ct => _Driver.Events.DeleteAsync(evt.Id, ct));
                }

                await WalkAsync("missions", missions.Select(item => item.Id), (q, ct) => IdsAsync(_Driver.Missions.EnumerateAsync(tenant.Id, q, ct), item => item.Id), token).ConfigureAwait(false);
                await WalkAsync("missions (user scope)", missions.Select(item => item.Id), (q, ct) => IdsAsync(_Driver.Missions.EnumerateAsync(tenant.Id, user.Id, q, ct), item => item.Id), token).ConfigureAwait(false);
                await WalkAsync("mission summaries", missions.Select(item => item.Id), (q, ct) => IdsAsync(_Driver.Missions.EnumerateSummariesAsync(tenant.Id, q, ct), item => item.Id), token).ConfigureAwait(false);
                await WalkAsync("voyages", voyages.Select(item => item.Id), (q, ct) => IdsAsync(_Driver.Voyages.EnumerateAsync(tenant.Id, q, ct), item => item.Id), token).ConfigureAwait(false);
                await WalkAsync("signals", signalIds, (q, ct) => IdsAsync(_Driver.Signals.EnumerateAsync(tenant.Id, q, ct), item => item.Id), token).ConfigureAwait(false);
                await WalkAsync("events", eventIds, (q, ct) =>
                {
                    q.EventType = "page.order";
                    return IdsAsync(_Driver.Events.EnumerateAsync(tenant.Id, q, ct), item => item.Id);
                }, token).ConfigureAwait(false);
            }
            finally
            {
                if (!_NoCleanup)
                {
                    foreach (Func<CancellationToken, Task> step in cleanup) await step(token).ConfigureAwait(false);
                }
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private static async Task<List<string>> IdsAsync<T>(Task<EnumerationResult<T>> page, Func<T, string> id)
        {
            return (await page.ConfigureAwait(false)).Objects.Select(id).ToList();
        }

        private static async Task WalkAsync(string label, IEnumerable<string> created, Func<EnumerationQuery, CancellationToken, Task<List<string>>> read, CancellationToken token)
        {
            HashSet<string> expected = new HashSet<string>(created, StringComparer.Ordinal);
            foreach (EnumerationOrderEnum order in new[] { EnumerationOrderEnum.CreatedAscending, EnumerationOrderEnum.CreatedDescending })
            {
                List<string> whole = (await read(new EnumerationQuery { PageNumber = 1, PageSize = 100, Order = order }, token).ConfigureAwait(false))
                    .Where(expected.Contains).ToList();
                DatabaseAssert.True(whole.Count == expected.Count, label + " " + order + ": one page holds every row (" + whole.Count + " of " + expected.Count + ")");

                List<string> walked = new List<string>();
                for (int page = 1; page <= expected.Count; page++)
                {
                    walked.AddRange(await read(new EnumerationQuery { PageNumber = page, PageSize = _PageSize, Order = order }, token).ConfigureAwait(false));
                }

                List<string> walkedRows = walked.Where(expected.Contains).ToList();
                DatabaseAssert.True(walkedRows.Distinct(StringComparer.Ordinal).Count() == walkedRows.Count, label + " " + order + ": no page repeats a row");
                DatabaseAssert.True(walkedRows.Count == expected.Count, label + " " + order + ": no page drops a row (" + walkedRows.Count + " of " + expected.Count + ")");
                DatabaseAssert.True(walkedRows.SequenceEqual(whole, StringComparer.Ordinal), label + " " + order + ": paging keeps the order of the single page");
            }
        }
    }
}
