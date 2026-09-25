namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end Event API descriptors covering listing, filtering, pagination, ordering, and
    /// enumeration against a live in-process Armada server provided by <see cref="E2EServerFixture"/>.
    /// </summary>
    public sealed class EventSuite : IArmadaTestSuite
    {
        #region Private-Members

        private readonly object _SeedLock = new object();
        private Task? _SharedEventSeed;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Event API end-to-end suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            #region Pagination-Tests

            cases.Add(CaseAsync("list_events_pagination_multi_page_verify_counts", "ListEvents_Pagination_MultiPage_VerifyCounts", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureEventsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?pageSize=5&pageNumber=1");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(5, result.Objects.Count);
                AssertTrue(result.TotalRecords >= 12);
                AssertEqual(1, result.PageNumber);
                AssertEqual(5, result.PageSize);
                AssertTrue(result.Success);
            }));

            cases.Add(CaseAsync("list_events_pagination_page2", "ListEvents_Pagination_Page2", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureEventsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?pageSize=5&pageNumber=2");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(5, result.Objects.Count);
                AssertEqual(2, result.PageNumber);
            }));

            cases.Add(CaseAsync("list_events_pagination_last_page_partial_results", "ListEvents_Pagination_LastPage_PartialResults", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureEventsSeededAsync(authClient);

                long totalRecords;
                {
                    HttpResponseMessage countResp = await authClient.GetAsync("/api/v1/events?pageSize=1000");
                    EnumerationResult<ArmadaEvent> countResult = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(countResp);
                    totalRecords = countResult.TotalRecords;
                }

                int pageSize = 5;
                int totalPages = (int)Math.Ceiling((double)totalRecords / pageSize);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?pageSize=" + pageSize + "&pageNumber=" + totalPages);
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                int expectedOnLastPage = (int)(totalRecords % pageSize);
                if (expectedOnLastPage == 0) expectedOnLastPage = pageSize;

                AssertEqual(expectedOnLastPage, result.Objects.Count);
                AssertEqual(totalPages, result.PageNumber);
            }));

            cases.Add(CaseAsync("list_events_pagination_first_page_has_correct_event_ids", "ListEvents_Pagination_FirstPageHasCorrectEventIds", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureEventsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?pageSize=3&pageNumber=1");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(3, result.Objects.Count);
                foreach (ArmadaEvent evt in result.Objects)
                {
                    AssertStartsWith("evt_", evt.Id);
                }
            }));

            #endregion

            #region Filter-By-Limit-Tests

            cases.Add(CaseAsync("list_events_filter_by_limit", "ListEvents_FilterByLimit", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureEventsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?limit=3");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(3, result.Objects.Count);
                AssertEqual(3, result.PageSize);
            }));

            #endregion

            #region Combined-Filters-Tests

            cases.Add(CaseAsync("list_events_combined_filters_type_and_vessel_id", "ListEvents_CombinedFilters_TypeAndVesselId", TestTags.Positive, async () =>
            {
                // Event filtering needs an explicit transition, not a race with captains created by earlier cases.
                E2EServerFixture fx = await E2EServerFixture.StartIsolatedAsync(_ => { });
                try
                {
                    HttpClient authClient = fx.AuthClient;

                    string fleetId = await CreateFleetAsync(authClient);
                    string vesselId = await CreateVesselAsync(authClient, fleetId);

                    Mission mission = await CreateMissionAsync(authClient, "CombinedVessel", vesselId: vesselId);
                    string missionId = mission.Id;
                    HttpResponseMessage transition = await authClient.PutAsync("/api/v1/missions/" + missionId + "/status",
                        JsonHelper.ToJsonContent(new { Status = "Assigned" }));
                    AssertEqual(HttpStatusCode.OK, transition.StatusCode);

                    HttpResponseMessage response = await authClient.GetAsync(
                        "/api/v1/events?type=mission.status_changed&vesselId=" + vesselId);
                    AssertEqual(HttpStatusCode.OK, response.StatusCode);

                    EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                    AssertTrue(result.Objects.Count >= 1);
                    foreach (ArmadaEvent evt in result.Objects)
                    {
                        AssertEqual("mission.status_changed", evt.EventType);
                        AssertEqual(vesselId, evt.VesselId);
                    }
                }
                finally { fx.Stop(); }
            }));

            cases.Add(CaseAsync("list_events_combined_filters_limit_and_type", "ListEvents_CombinedFilters_LimitAndType", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureEventsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync(
                    "/api/v1/events?type=mission.status_changed&limit=2");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(2, result.Objects.Count);
            }));

            #endregion

            #region Enumerate-Tests

            cases.Add(CaseAsync("enumerate_events_with_page_size_and_page_number", "EnumerateEvents_WithPageSizeAndPageNumber", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureEventsSeededAsync(authClient);

                StringContent content = JsonHelper.ToJsonContent(new { PageSize = 5, PageNumber = 2 });

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/events/enumerate", content);
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(5, result.Objects.Count);
                AssertEqual(2, result.PageNumber);
                AssertEqual(5, result.PageSize);
                AssertTrue(result.TotalRecords >= 12);
            }));

            cases.Add(CaseAsync("enumerate_events_querystring_overrides_page_size", "EnumerateEvents_QuerystringOverrides_PageSize", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureEventsSeededAsync(authClient);

                StringContent content = JsonHelper.ToJsonContent(new { });

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/events/enumerate?pageSize=3", content);
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(3, result.Objects.Count);
                AssertEqual(3, result.PageSize);
            }));

            #endregion

            return new TestSuiteDescriptor(
                suiteId: "E2E.Event",
                displayName: "Event Routes",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Creates a fleet and returns its identifier.
        /// </summary>
        private static async Task<string> CreateFleetAsync(HttpClient client)
        {
            StringContent content = JsonHelper.ToJsonContent(new { Name = "EventTestFleet-" + Guid.NewGuid().ToString("N").Substring(0, 8) });
            HttpResponseMessage response = await client.PostAsync("/api/v1/fleets", content);
            Fleet fleet = await JsonHelper.DeserializeAsync<Fleet>(response);
            return fleet.Id;
        }

        /// <summary>
        /// Creates a vessel in the given fleet and returns its identifier.
        /// </summary>
        private static async Task<string> CreateVesselAsync(HttpClient client, string fleetId)
        {
            StringContent content = JsonHelper.ToJsonContent(new { Name = "EventTestVessel-" + Guid.NewGuid().ToString("N").Substring(0, 8), RepoUrl = TestRepoHelper.GetLocalBareRepoUrl(), FleetId = fleetId });
            HttpResponseMessage response = await client.PostAsync("/api/v1/vessels", content);
            Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(response);
            return vessel.Id;
        }

        /// <summary>
        /// Creates a mission with an optional vessel and voyage and returns the mission.
        /// </summary>
        private static async Task<Mission> CreateMissionAsync(HttpClient client, string title, string? vesselId = null, string? voyageId = null)
        {
            object payload;
            if (vesselId != null && voyageId != null)
                payload = new { Title = title, Description = "Test mission", VesselId = vesselId, VoyageId = voyageId };
            else if (vesselId != null)
                payload = new { Title = title, Description = "Test mission", VesselId = vesselId };
            else if (voyageId != null)
                payload = new { Title = title, Description = "Test mission", VoyageId = voyageId };
            else
                payload = new { Title = title, Description = "Test mission" };

            StringContent content = JsonHelper.ToJsonContent(payload);
            HttpResponseMessage response = await client.PostAsync("/api/v1/missions", content);
            string body = await response.Content.ReadAsStringAsync();

            // When mission stays Pending (no captain available), the API returns
            // { "Mission": {...}, "Warning": "..." } instead of the mission directly.
            MissionCreateResponse wrapper = JsonHelper.Deserialize<MissionCreateResponse>(body);
            if (wrapper.Mission != null)
                return wrapper.Mission;

            return JsonHelper.Deserialize<Mission>(body);
        }

        /// <summary>
        /// Transitions a mission to the given status.
        /// </summary>
        private static async Task TransitionAsync(HttpClient client, string missionId, string status)
        {
            StringContent content = JsonHelper.ToJsonContent(new { Status = status });
            await client.PutAsync("/api/v1/missions/" + missionId + "/status", content);
        }

        /// <summary>
        /// Ensure the shared server holds a reusable dataset of events, seeding it exactly once for the
        /// suite. Every case runs against the same in-process server fixture and events accumulate
        /// globally, so the pagination/enumerate cases only assert accumulation-tolerant conditions (a
        /// full page, a total at or above a threshold, event ids beginning with "evt_"). They can share
        /// one dataset instead of each re-creating a dozen missions and status transitions over
        /// sequential HTTP round-trips. The seed Task is memoized: the first converted case to run pays
        /// the cost and the rest await the completed Task.
        /// </summary>
        /// <param name="authClient">Authenticated client for the shared e2e server.</param>
        /// <returns>A task that completes once the shared dataset exists.</returns>
        private Task EnsureEventsSeededAsync(HttpClient authClient)
        {
            lock (_SeedLock)
            {
                if (_SharedEventSeed == null) _SharedEventSeed = SeedSharedEventsAsync(authClient);
                return _SharedEventSeed;
            }
        }

        /// <summary>
        /// Seeds the shared server with events once. Each event is produced exactly as the retired
        /// per-case loops did: create a mission, then transition it to "Assigned" (which emits a
        /// "mission.status_changed" event). Twelve events are created, enough to satisfy the largest
        /// full-page and threshold assertions among the converted cases (page 2 at page size 5 and
        /// TotalRecords &gt;= 12).
        /// </summary>
        /// <param name="authClient">Authenticated client for the shared e2e server.</param>
        /// <returns>A task that completes once the shared dataset exists.</returns>
        private static async Task SeedSharedEventsAsync(HttpClient authClient)
        {
            for (int i = 0; i < 12; i++)
            {
                Mission mission = await CreateMissionAsync(authClient, "SharedEventSeed-" + i);
                string missionId = mission.Id;
                await TransitionAsync(authClient, missionId, "Assigned");
            }
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "E2E.Event",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
