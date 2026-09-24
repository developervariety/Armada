#nullable enable

namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Mission summary reads return every light field of the full row and leave the heavy text fields unset, and
    /// they never transfer the heavy columns from the database. Grouped voyage counts count only that voyage.
    /// </summary>
    internal sealed class MissionSummaryDatabaseTests
    {
        private const int _HeavyChars = 256 * 1024;
        private static readonly string[] _HeavyFields = { "Description", "DiffSnapshot", "AgentOutput", "PlaybookSnapshots" };

        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        internal MissionSummaryDatabaseTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("summary-tenant", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "summary-user", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "summary-fleet", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "summary-vessel", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "summary-captain", token).ConfigureAwait(false);
                Voyage voyage = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "summary-voyage", token).ConfigureAwait(false);
                Voyage otherVoyage = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "summary-other-voyage", token).ConfigureAwait(false);

                MissionStatusEnum[] statuses =
                {
                    MissionStatusEnum.Complete,
                    MissionStatusEnum.Complete,
                    MissionStatusEnum.InProgress,
                    MissionStatusEnum.Failed,
                    MissionStatusEnum.WorkProduced,
                    MissionStatusEnum.Pending
                };
                List<Mission> missions = new List<Mission>();
                for (int index = 0; index < statuses.Length; index++)
                {
                    MissionStatusEnum status = statuses[index];
                    char fill = (char)('a' + index);
                    Mission mission = await fixture.CreateMissionAsync(tenant.Id, user.Id, voyage.Id, vessel.Id, captain.Id, "summary-" + status, token,
                        DateTime.UtcNow.AddMinutes(-10), null, item =>
                        {
                            item.Status = status;
                            item.Persona = "Worker";
                            item.Description = new string(fill, _HeavyChars);
                            item.DiffSnapshot = new string(fill, _HeavyChars);
                            item.AgentOutput = new string(fill, _HeavyChars);
                            SetLightFields(item, index, captain.Id);
                        }).ConfigureAwait(false);
                    missions.Add(mission);
                }

                await fixture.CreateMissionAsync(tenant.Id, user.Id, otherVoyage.Id, vessel.Id, captain.Id, "summary-other", token, null, null,
                    item => item.Status = MissionStatusEnum.Complete).ConfigureAwait(false);

                Dictionary<string, Mission> fullRows = new Dictionary<string, Mission>(StringComparer.Ordinal);
                foreach (Mission mission in missions)
                {
                    Mission full = DatabaseAssert.NotNull(await _Driver.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false), "Full mission read");
                    DatabaseAssert.Equal(_HeavyChars, full.Description?.Length ?? 0, "A full read returns the description");
                    DatabaseAssert.Equal(_HeavyChars, full.DiffSnapshot?.Length ?? 0, "A full read returns the diff snapshot");
                    DatabaseAssert.Equal(_HeavyChars, full.AgentOutput?.Length ?? 0, "A full read returns the agent output");
                    fullRows[mission.Id] = full;
                }

                // Every summary read matches the full row on every light property and leaves the heavy ones unset.
                foreach (Mission mission in missions)
                {
                    Mission summary = DatabaseAssert.NotNull(await _Driver.Missions.ReadSummaryAsync(mission.Id, token).ConfigureAwait(false), "Mission summary read");
                    AssertSummary(fullRows[mission.Id], summary, "ReadSummary");
                }

                EnumerationQuery query = new EnumerationQuery { VoyageId = voyage.Id, PageNumber = 1, PageSize = 50 };
                List<EnumerationResult<Mission>> scopes = new List<EnumerationResult<Mission>>
                {
                    await _Driver.Missions.EnumerateSummariesAsync(query, token).ConfigureAwait(false),
                    await _Driver.Missions.EnumerateSummariesAsync(tenant.Id, query, token).ConfigureAwait(false),
                    await _Driver.Missions.EnumerateSummariesAsync(tenant.Id, user.Id, query, token).ConfigureAwait(false)
                };
                string[] scopeNames = { "unscoped", "tenant", "tenant and user" };
                for (int index = 0; index < scopes.Count; index++)
                {
                    DatabaseAssert.Equal((long)missions.Count, scopes[index].TotalRecords, scopeNames[index] + " summary total");
                    DatabaseAssert.Equal(missions.Count, scopes[index].Objects.Count, scopeNames[index] + " summary page");
                    foreach (Mission summary in scopes[index].Objects)
                    {
                        DatabaseAssert.True(fullRows.ContainsKey(summary.Id), scopeNames[index] + " summary lists only the voyage's missions");
                        AssertSummary(fullRows[summary.Id], summary, scopeNames[index] + " EnumerateSummaries");
                    }
                }

                DatabaseAssert.Equal(0, (await _Driver.Missions.EnumerateSummariesAsync("ten_absent_" + Guid.NewGuid().ToString("N"), query, token).ConfigureAwait(false)).Objects.Count,
                    "Another tenant's summary read is empty");

                Dictionary<MissionStatusEnum, int> counts = await _Driver.Missions.CountByVoyageStatusAsync(voyage.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(5, counts.Count, "Only the statuses present on the voyage are counted");
                DatabaseAssert.Equal(2, counts[MissionStatusEnum.Complete], "Complete missions of this voyage only");
                DatabaseAssert.Equal(1, counts[MissionStatusEnum.InProgress], "InProgress count");
                DatabaseAssert.Equal(1, counts[MissionStatusEnum.Failed], "Failed count");
                DatabaseAssert.Equal(1, counts[MissionStatusEnum.WorkProduced], "WorkProduced count");
                DatabaseAssert.Equal(1, counts[MissionStatusEnum.Pending], "Pending count");
                DatabaseAssert.Equal(0, (await _Driver.Missions.CountByVoyageStatusAsync("vyg_absent_" + Guid.NewGuid().ToString("N"), token).ConfigureAwait(false)).Count,
                    "A voyage without missions has no counts");

                // The heavy columns of these rows total several megabytes. A summary or count path that reads the
                // full rows and discards the text afterwards allocates at least that much; one that never selects
                // the columns allocates a small fraction of it.
                long heavyBytes = (long)missions.Count * 3 * _HeavyChars * sizeof(char);
                long budget = heavyBytes / 4;
                await AssertAllocatesUnderAsync(() => _Driver.Missions.EnumerateSummariesAsync(query, token), budget, "EnumerateSummariesAsync").ConfigureAwait(false);
                await AssertAllocatesUnderAsync(() => _Driver.Missions.EnumerateSummariesAsync(tenant.Id, query, token), budget, "tenant EnumerateSummariesAsync").ConfigureAwait(false);
                await AssertAllocatesUnderAsync(() => _Driver.Missions.EnumerateSummariesAsync(tenant.Id, user.Id, query, token), budget, "user EnumerateSummariesAsync").ConfigureAwait(false);
                await AssertAllocatesUnderAsync(() => _Driver.Missions.CountByVoyageStatusAsync(voyage.Id, token), budget, "CountByVoyageStatusAsync").ConfigureAwait(false);
                await AssertAllocatesUnderAsync(async () =>
                {
                    foreach (Mission mission in missions)
                        await _Driver.Missions.ReadSummaryAsync(mission.Id, token).ConfigureAwait(false);
                    return 0;
                }, budget, "ReadSummaryAsync for every mission").ConfigureAwait(false);
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Give every light column a non-default value, so a summary projection that leaves a column out reads
        /// back a default and differs from the full row.
        /// </summary>
        private static void SetLightFields(Mission mission, int index, string captainId)
        {
            DateTime stamp = DateTime.UtcNow.AddMinutes(-20 + index);
            mission.Persona = "Worker";
            mission.Priority = 40 + index;
            mission.PrUrl = "https://example.invalid/pull/" + index;
            mission.CommitHash = "commit" + index;
            mission.RequestedCaptainId = captainId;
            mission.Tier = CaptainTierEnum.Premium;
            mission.StageOrder = index + 1;
            mission.PreferredModel = "mid";
            mission.CapabilityHint = "hint-" + index;
            mission.Mode = MissionModeEnum.Audit;
            mission.FailureReason = "Failure reason " + index;
            mission.ReconciledUtc = stamp;
            mission.ReconciledReason = "Reconciled " + index;
            mission.HeldForOperatorReview = true;
            mission.HeldForOperatorReviewReason = "Held " + index;
            mission.RequiresReview = true;
            mission.ReviewDenyAction = ReviewDenyActionEnum.FailPipeline;
            mission.ReviewComment = "Review comment " + index;
            mission.ReviewedByUserId = "usr_reviewer_" + index;
            mission.ReviewRequestedUtc = stamp.AddSeconds(1);
            mission.ReviewedUtc = stamp.AddSeconds(2);
            mission.PrestagedFiles = new List<PrestagedFile> { new PrestagedFile { SourcePath = "source-" + index, DestPath = "dest-" + index, ReadOnly = true } };
            mission.RecoveryAttempts = 2;
            mission.LandingRetryCount = 3;
            mission.StartFromRef = "refs/heads/base-" + index;
            mission.RetrySkipCaptainIds = "cpt_skip_" + index;
            mission.LastRecoveryActionUtc = stamp.AddSeconds(3);
        }

        private static void AssertSummary(Mission full, Mission summary, string label)
        {
            DatabaseAssert.True(summary.Description == null, label + " leaves the description unset");
            DatabaseAssert.True(summary.DiffSnapshot == null, label + " leaves the diff snapshot unset");
            DatabaseAssert.True(summary.AgentOutput == null, label + " leaves the agent output unset");
            DatabaseAssert.Equal(0, summary.PlaybookSnapshots.Count, label + " leaves the playbook snapshots unset");
            DatabaseAssert.AllProperties(full, summary, label + " Mission", _HeavyFields);
        }

        private static async Task AssertAllocatesUnderAsync<T>(Func<Task<T>> read, long budget, string label)
        {
            // Warm the connection pool and code paths so the measurement sees the read, not first-use setup.
            await read().ConfigureAwait(false);
            long before = GC.GetTotalAllocatedBytes(true);
            await read().ConfigureAwait(false);
            long allocated = GC.GetTotalAllocatedBytes(true) - before;
            DatabaseAssert.True(allocated < budget,
                label + " allocated " + allocated + " bytes; a read that never loads the heavy columns stays under " + budget);
        }
        /// <summary>
        /// Full and summary mission enumeration apply the same status, voyage, vessel, captain and mission filters
        /// at every scope. A scoped read that drops a filter returns rows the filter excludes.
        /// </summary>
        internal async Task VerifyEnumerationFiltersAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("filter-tenant", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "filter-user", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "filter-fleet", token).ConfigureAwait(false);
                Vessel vesselA = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "filter-vessel-a", token).ConfigureAwait(false);
                Vessel vesselB = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "filter-vessel-b", token).ConfigureAwait(false);
                Captain captainA = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "filter-captain-a", token).ConfigureAwait(false);
                Captain captainB = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "filter-captain-b", token).ConfigureAwait(false);
                Voyage voyageA = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "filter-voyage-a", token).ConfigureAwait(false);
                Voyage voyageB = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "filter-voyage-b", token).ConfigureAwait(false);

                List<Mission> missions = new List<Mission>
                {
                    await CreateFilterMissionAsync(fixture, tenant.Id, user.Id, voyageA.Id, vesselA.Id, captainA.Id, MissionStatusEnum.Complete, token).ConfigureAwait(false),
                    await CreateFilterMissionAsync(fixture, tenant.Id, user.Id, voyageA.Id, vesselB.Id, captainB.Id, MissionStatusEnum.Failed, token).ConfigureAwait(false),
                    await CreateFilterMissionAsync(fixture, tenant.Id, user.Id, voyageB.Id, vesselA.Id, captainB.Id, MissionStatusEnum.Complete, token).ConfigureAwait(false),
                    await CreateFilterMissionAsync(fixture, tenant.Id, user.Id, voyageB.Id, vesselB.Id, captainA.Id, MissionStatusEnum.Pending, token).ConfigureAwait(false)
                };

                List<FilterCase> cases = new List<FilterCase>
                {
                    new FilterCase("status", new EnumerationQuery { Status = "Complete" }, m => m.Status == MissionStatusEnum.Complete, false),
                    new FilterCase("voyage", new EnumerationQuery { VoyageId = voyageA.Id }, m => m.VoyageId == voyageA.Id, true),
                    new FilterCase("vessel", new EnumerationQuery { VesselId = vesselB.Id }, m => m.VesselId == vesselB.Id, true),
                    new FilterCase("captain", new EnumerationQuery { CaptainId = captainA.Id }, m => m.CaptainId == captainA.Id, true),
                    new FilterCase("mission", new EnumerationQuery { MissionId = missions[2].Id }, m => m.Id == missions[2].Id, true),
                    new FilterCase("status and voyage", new EnumerationQuery { Status = "Complete", VoyageId = voyageB.Id },
                        m => m.Status == MissionStatusEnum.Complete && m.VoyageId == voyageB.Id, true)
                };

                foreach (FilterCase filter in cases)
                {
                    List<string> expected = new List<string>();
                    foreach (Mission mission in missions)
                        if (filter.Matches(mission)) expected.Add(mission.Id);
                    expected.Sort(StringComparer.Ordinal);

                    // An unscoped read also sees other tenants' rows, so it is compared only where the filter names
                    // an identifier this fixture owns.
                    if (filter.UniqueToFixture)
                    {
                        AssertIds(expected, await _Driver.Missions.EnumerateAsync(Page(filter.Query), token).ConfigureAwait(false), "unscoped full " + filter.Name);
                        AssertIds(expected, await _Driver.Missions.EnumerateSummariesAsync(Page(filter.Query), token).ConfigureAwait(false), "unscoped summary " + filter.Name);
                    }

                    AssertIds(expected, await _Driver.Missions.EnumerateAsync(tenant.Id, Page(filter.Query), token).ConfigureAwait(false), "tenant full " + filter.Name);
                    AssertIds(expected, await _Driver.Missions.EnumerateSummariesAsync(tenant.Id, Page(filter.Query), token).ConfigureAwait(false), "tenant summary " + filter.Name);
                    AssertIds(expected, await _Driver.Missions.EnumerateAsync(tenant.Id, user.Id, Page(filter.Query), token).ConfigureAwait(false), "user full " + filter.Name);
                    AssertIds(expected, await _Driver.Missions.EnumerateSummariesAsync(tenant.Id, user.Id, Page(filter.Query), token).ConfigureAwait(false), "user summary " + filter.Name);
                }
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private static EnumerationQuery Page(EnumerationQuery filter)
        {
            return new EnumerationQuery
            {
                Status = filter.Status,
                VoyageId = filter.VoyageId,
                VesselId = filter.VesselId,
                CaptainId = filter.CaptainId,
                MissionId = filter.MissionId,
                PageNumber = 1,
                PageSize = 50
            };
        }

        private static void AssertIds(List<string> expected, EnumerationResult<Mission> result, string label)
        {
            List<string> actual = new List<string>();
            foreach (Mission mission in result.Objects) actual.Add(mission.Id);
            actual.Sort(StringComparer.Ordinal);
            DatabaseAssert.Equal(String.Join(",", expected), String.Join(",", actual), label + " mission ids");
            DatabaseAssert.Equal((long)expected.Count, result.TotalRecords, label + " total");
        }

        private static Task<Mission> CreateFilterMissionAsync(DatabaseFixture fixture, string tenantId, string userId, string voyageId, string vesselId, string captainId,
            MissionStatusEnum status, CancellationToken token)
        {
            return fixture.CreateMissionAsync(tenantId, userId, voyageId, vesselId, captainId, "filter-" + status, token, null, null, item => item.Status = status);
        }

        private sealed class FilterCase
        {
            internal FilterCase(string name, EnumerationQuery query, Func<Mission, bool> matches, bool uniqueToFixture)
            {
                Name = name;
                Query = query;
                Matches = matches;
                UniqueToFixture = uniqueToFixture;
            }

            internal string Name { get; }

            internal EnumerationQuery Query { get; }

            internal Func<Mission, bool> Matches { get; }

            internal bool UniqueToFixture { get; }
        }
    }
}
