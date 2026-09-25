namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end descriptors for merge queue routes: enqueue, get, cancel, list, enumerate, and
    /// process. Ported 1:1 from the retired automated MergeQueueTests suite. Each case is
    /// self-contained and creates its own fleet/vessel/mission prerequisites against the shared
    /// e2e server fixture.
    /// </summary>
    public sealed class MergeQueueSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.MergeQueue";

        private readonly object _SeedLock = new object();
        private Task? _SharedQueueSeed;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Merge Queue Routes suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("list_25_entries_page_size_10_returns_3_pages", "List_25Entries_PageSize10_Returns3Pages", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureQueueSeededAsync(authClient).ConfigureAwait(false);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/merge-queue?pageSize=10&pageNumber=1").ConfigureAwait(false);
                EnumerationResult<MergeEntry> result = await JsonHelper.DeserializeAsync<EnumerationResult<MergeEntry>>(response).ConfigureAwait(false);

                AssertEqual(10, result.Objects.Count);
                AssertTrue(result.TotalRecords >= 25, "TotalRecords should be >= 25");
                AssertTrue(result.TotalPages >= 3, "TotalPages should be >= 3");
                AssertEqual(1, result.PageNumber);
                AssertEqual(10, result.PageSize);
            }));

            cases.Add(CaseAsync("list_25_entries_page_size_10_page_2_returns_10", "List_25Entries_PageSize10_Page2_Returns10", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureQueueSeededAsync(authClient).ConfigureAwait(false);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/merge-queue?pageSize=10&pageNumber=2").ConfigureAwait(false);
                EnumerationResult<MergeEntry> result = await JsonHelper.DeserializeAsync<EnumerationResult<MergeEntry>>(response).ConfigureAwait(false);

                AssertEqual(10, result.Objects.Count);
                AssertEqual(2, result.PageNumber);
            }));

            cases.Add(CaseAsync("list_25_entries_page_size_10_page_3_returns_5", "List_25Entries_PageSize10_Page3_Returns5", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureQueueSeededAsync(authClient).ConfigureAwait(false);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/merge-queue?pageSize=10&pageNumber=3").ConfigureAwait(false);
                EnumerationResult<MergeEntry> result = await JsonHelper.DeserializeAsync<EnumerationResult<MergeEntry>>(response).ConfigureAwait(false);

                AssertTrue(result.Objects.Count >= 5, "Page 3 should have at least 5 items");
                AssertEqual(3, result.PageNumber);
            }));

            cases.Add(CaseAsync("list_25_entries_page_size_10_page_4_beyond_last_page_returns_empty", "List_25Entries_PageSize10_Page4_BeyondLastPage_ReturnsEmpty", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureQueueSeededAsync(authClient).ConfigureAwait(false);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/merge-queue?pageSize=10&pageNumber=999").ConfigureAwait(false);
                EnumerationResult<MergeEntry> result = await JsonHelper.DeserializeAsync<EnumerationResult<MergeEntry>>(response).ConfigureAwait(false);

                AssertEqual(0, result.Objects.Count);
                AssertTrue(result.TotalRecords >= 25, "TotalRecords should be >= 25");
            }));

            cases.Add(CaseAsync("list_page_boundaries_first_and_last_records", "List_PageBoundaries_FirstAndLastRecords", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureQueueSeededAsync(authClient).ConfigureAwait(false);

                HttpResponseMessage page1Response = await authClient.GetAsync("/api/v1/merge-queue?pageSize=3&pageNumber=1").ConfigureAwait(false);
                EnumerationResult<MergeEntry> page1Result = await JsonHelper.DeserializeAsync<EnumerationResult<MergeEntry>>(page1Response).ConfigureAwait(false);
                AssertEqual(3, page1Result.Objects.Count);

                HttpResponseMessage page2Response = await authClient.GetAsync("/api/v1/merge-queue?pageSize=3&pageNumber=2").ConfigureAwait(false);
                EnumerationResult<MergeEntry> page2Result = await JsonHelper.DeserializeAsync<EnumerationResult<MergeEntry>>(page2Response).ConfigureAwait(false);
                AssertTrue(page2Result.Objects.Count >= 2, "Page 2 should have at least 2 items");

                List<string> page1Ids = page1Result.Objects.Select(obj => obj.Id).ToList();
                List<string> page2Ids = page2Result.Objects.Select(obj => obj.Id).ToList();

                AssertEqual(0, page1Ids.Intersect(page2Ids).Count());
            }));

            cases.Add(CaseAsync("enumerate_with_page_size_and_page_number_returns_correct_page", "Enumerate_WithPageSizeAndPageNumber_ReturnsCorrectPage", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureQueueSeededAsync(authClient).ConfigureAwait(false);

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/merge-queue/enumerate", JsonHelper.ToJsonContent(new { PageSize = 5, PageNumber = 2 })).ConfigureAwait(false);
                EnumerationResult<MergeEntry> result = await JsonHelper.DeserializeAsync<EnumerationResult<MergeEntry>>(response).ConfigureAwait(false);

                AssertEqual(5, result.Objects.Count);
                AssertEqual(2, result.PageNumber);
                AssertEqual(5, result.PageSize);
                AssertTrue(result.TotalRecords >= 15, "TotalRecords should be >= 15");
                AssertTrue(result.TotalPages >= 3, "TotalPages should be >= 3");
            }));

            cases.Add(CaseAsync("enumerate_querystring_overrides_work", "Enumerate_QuerystringOverrides_Work", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureQueueSeededAsync(authClient).ConfigureAwait(false);

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/merge-queue/enumerate?pageSize=3", JsonHelper.ToJsonContent(new { })).ConfigureAwait(false);
                EnumerationResult<MergeEntry> result = await JsonHelper.DeserializeAsync<EnumerationResult<MergeEntry>>(response).ConfigureAwait(false);

                AssertEqual(3, result.Objects.Count);
                AssertEqual(3, result.PageSize);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Merge Queue Routes",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<string> CreateFleetAsync(HttpClient authClient, string name = "MergeTestFleet")
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            HttpResponseMessage resp = await authClient.PostAsync("/api/v1/fleets", JsonHelper.ToJsonContent(new { Name = uniqueName })).ConfigureAwait(false);
            Fleet fleet = await JsonHelper.DeserializeAsync<Fleet>(resp).ConfigureAwait(false);
            return fleet.Id;
        }

        private static async Task<string> CreateVesselAsync(HttpClient authClient, string fleetId, string name = "MergeTestVessel")
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            HttpResponseMessage resp = await authClient.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new { Name = uniqueName, RepoUrl = TestRepoHelper.GetLocalBareRepoUrl(), FleetId = fleetId })).ConfigureAwait(false);
            Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(resp).ConfigureAwait(false);
            return vessel.Id;
        }

        private static async Task<string> CreateMissionAsync(HttpClient authClient, string title = "MergeTestMission")
        {
            HttpResponseMessage resp = await authClient.PostAsync("/api/v1/missions", JsonHelper.ToJsonContent(new { Title = title, Description = "Mission for merge queue testing" })).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            MissionCreateResponse wrapper = JsonHelper.Deserialize<MissionCreateResponse>(body);
            string missionId;
            if (wrapper.Mission != null)
                missionId = wrapper.Mission.Id;
            else
                missionId = JsonHelper.Deserialize<Mission>(body).Id;
            return missionId;
        }

        private static async Task<MergeEntry> EnqueueAsync(HttpClient authClient, string missionId, string vesselId, string branch, string targetBranch = "main")
        {
            HttpResponseMessage resp = await authClient.PostAsync("/api/v1/merge-queue", JsonHelper.ToJsonContent(new
            {
                MissionId = missionId,
                VesselId = vesselId,
                BranchName = branch,
                TargetBranch = targetBranch
            })).ConfigureAwait(false);
            MergeEntry entry = await JsonHelper.DeserializeAsync<MergeEntry>(resp).ConfigureAwait(false);
            return entry;
        }

        private static async Task<MergeQueuePrerequisiteResult> CreatePrerequisitesAsync(HttpClient authClient, string suffix = "")
        {
            string fleetId = await CreateFleetAsync(authClient, "Fleet" + suffix).ConfigureAwait(false);
            string vesselId = await CreateVesselAsync(authClient, fleetId, "Vessel" + suffix).ConfigureAwait(false);
            string missionId = await CreateMissionAsync(authClient, "Mission" + suffix).ConfigureAwait(false);
            return new MergeQueuePrerequisiteResult(fleetId, vesselId, missionId);
        }

        /// <summary>
        /// Ensure the shared merge queue holds a reusable dataset of at least 25 queued entries, seeding it
        /// exactly once for the suite. Every case runs against the same in-process server fixture and the
        /// list/enumerate pagination cases only assert accumulation-tolerant conditions (a full page, a
        /// total at or above a threshold, no page overlap), so they can share one dataset instead of each
        /// re-creating 25 missions and 25 enqueues over sequential HTTP round-trips. The seed Task is
        /// memoized: the first pagination case to run pays the cost and the rest await the completed Task.
        /// </summary>
        /// <param name="authClient">Authenticated client for the shared e2e server.</param>
        /// <returns>A task that completes once the shared dataset exists.</returns>
        private Task EnsureQueueSeededAsync(HttpClient authClient)
        {
            lock (_SeedLock)
            {
                if (_SharedQueueSeed == null) _SharedQueueSeed = SeedSharedQueueAsync(authClient);
                return _SharedQueueSeed;
            }
        }

        private static async Task SeedSharedQueueAsync(HttpClient authClient)
        {
            MergeQueuePrerequisiteResult prereqs = await CreatePrerequisitesAsync(authClient, "PagShared").ConfigureAwait(false);

            for (int i = 0; i < 25; i++)
            {
                string missionId = await CreateMissionAsync(authClient, "SharedPagMission" + i).ConfigureAwait(false);
                await EnqueueAsync(authClient, missionId, prereqs.VesselId, "feat/shared-pag-" + i.ToString("D2")).ConfigureAwait(false);
            }
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
