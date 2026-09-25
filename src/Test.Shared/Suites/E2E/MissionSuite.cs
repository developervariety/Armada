namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end Mission API descriptors covering CRUD, status transitions (valid and invalid),
    /// diff retrieval, list pagination and filters, and enumeration against a live in-process
    /// Armada server provided by <see cref="E2EServerFixture"/>.
    /// </summary>
    public sealed class MissionSuite : IArmadaTestSuite
    {
        #region Private-Members

        private readonly object _SeedLock = new object();
        private Task? _SharedMissionSeed;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Mission API end-to-end suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            #region StatusTransition-Valid-HappyPath

            cases.Add(CaseAsync("status_transition_in_progress_to_complete_succeeds", "StatusTransition_InProgressToComplete_Succeeds", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdFleetIds = new List<string>();
                List<string> createdVesselIds = new List<string>();
                List<string> createdMissionIds = new List<string>();

                Mission created = await CreateUnboundMissionAsync(authClient, createdMissionIds, "IPToComplete", "Research");
                string missionId = created.Id;

                await TransitionAndAssertAsync(authClient, missionId, "Assigned");
                await TransitionAndAssertAsync(authClient, missionId, "InProgress");
                await TransitionAndAssertAsync(authClient, missionId, "Complete");
            }));

            cases.Add(CaseAsync("status_transition_testing_to_complete_succeeds", "StatusTransition_TestingToComplete_Succeeds", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdFleetIds = new List<string>();
                List<string> createdVesselIds = new List<string>();
                List<string> createdMissionIds = new List<string>();

                Mission created = await CreateUnboundMissionAsync(authClient, createdMissionIds, "TestToComplete", "Research");
                string missionId = created.Id;

                await TransitionAndAssertAsync(authClient, missionId, "Assigned");
                await TransitionAndAssertAsync(authClient, missionId, "InProgress");
                await TransitionAndAssertAsync(authClient, missionId, "Testing");
                await TransitionAndAssertAsync(authClient, missionId, "Complete");
            }));

            cases.Add(CaseAsync("status_transition_review_to_complete_requires_review_approval", "StatusTransition_ReviewToComplete_RequiresReviewApproval", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdMissionIds = new List<string>();

                // A report-only mission otherwise satisfies the completion proof, so the refusal can
                // only come from the review gate.
                Mission created = await CreateUnboundMissionAsync(authClient, createdMissionIds, "ReviewToComplete", "Research");
                string missionId = created.Id;

                await TransitionAndAssertAsync(authClient, missionId, "Assigned");
                await TransitionAndAssertAsync(authClient, missionId, "InProgress");
                await TransitionAndAssertAsync(authClient, missionId, "Review");
                await AssertCompleteRefusedAsync(authClient, missionId, "manual_completion_review_required", "Review");
            }));

            cases.Add(CaseAsync("status_transition_in_progress_to_complete_without_commit_refused_ancestry_unavailable", "StatusTransition_InProgressToComplete_WithoutCommit_RefusedAncestryUnavailable", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdMissionIds = new List<string>();

                Mission created = await CreateUnboundMissionAsync(authClient, createdMissionIds, "NoCommitComplete", null);
                string missionId = created.Id;

                await TransitionAndAssertAsync(authClient, missionId, "Assigned");
                await TransitionAndAssertAsync(authClient, missionId, "InProgress");
                await AssertCompleteRefusedAsync(authClient, missionId, "manual_completion_ancestry_unavailable", "InProgress");
            }));

            #endregion

            #region StatusTransition-Valid-Lifecycle

            cases.Add(CaseAsync("status_transition_full_lifecycle_pending_through_review_rework_to_complete", "StatusTransition_FullLifecycle_PendingThroughReviewReworkToComplete", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdMissionIds = new List<string>();

                Mission created = await CreateUnboundMissionAsync(authClient, createdMissionIds, "Lifecycle Full", "Research");
                string missionId = created.Id;

                await TransitionAndAssertAsync(authClient, missionId, "Assigned");
                await TransitionAndAssertAsync(authClient, missionId, "InProgress");
                await TransitionAndAssertAsync(authClient, missionId, "Testing");
                await TransitionAndAssertAsync(authClient, missionId, "Review");
                await TransitionAndAssertAsync(authClient, missionId, "InProgress");
                await TransitionAndAssertAsync(authClient, missionId, "Testing");

                HttpResponseMessage response = await TransitionAsync(authClient, missionId, "Complete");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
                Mission transitioned = await JsonHelper.DeserializeAsync<Mission>(response);
                AssertEqual(MissionStatusEnum.Complete, transitioned.Status);
            }));

            cases.Add(CaseAsync("status_transition_full_lifecycle_sets_completed_utc_on_complete", "StatusTransition_FullLifecycle_SetsCompletedUtcOnComplete", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdFleetIds = new List<string>();
                List<string> createdVesselIds = new List<string>();
                List<string> createdMissionIds = new List<string>();

                Mission created = await CreateUnboundMissionAsync(authClient, createdMissionIds, "Complete Timestamp", "Research");
                string missionId = created.Id;

                await TransitionAndAssertAsync(authClient, missionId, "Assigned");
                await TransitionAndAssertAsync(authClient, missionId, "InProgress");

                HttpResponseMessage response = await TransitionAsync(authClient, missionId, "Complete");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
                Mission transitioned = await JsonHelper.DeserializeAsync<Mission>(response);
                AssertTrue(transitioned.CompletedUtc != null);
            }));

            cases.Add(CaseAsync("status_transition_in_progress_to_complete_sets_total_runtime_ms", "StatusTransition_InProgressToComplete_SetsTotalRuntimeMs", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdFleetIds = new List<string>();
                List<string> createdVesselIds = new List<string>();
                List<string> createdMissionIds = new List<string>();

                Mission created = await CreateUnboundMissionAsync(authClient, createdMissionIds, "Runtime Timestamp", "Research");
                string missionId = created.Id;

                await TransitionAndAssertAsync(authClient, missionId, "Assigned");
                await TransitionAndAssertAsync(authClient, missionId, "InProgress");

                HttpResponseMessage response = await TransitionAsync(authClient, missionId, "Complete");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
                Mission transitioned = await JsonHelper.DeserializeAsync<Mission>(response);
                AssertTrue(transitioned.TotalRuntimeMs != null, "Complete transition should preserve TotalRuntimeMs when StartedUtc exists");
            }));

            cases.Add(CaseAsync("status_transition_testing_bounce_back_to_in_progress_then_complete", "StatusTransition_TestingBounceBackToInProgress_ThenComplete", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdFleetIds = new List<string>();
                List<string> createdVesselIds = new List<string>();
                List<string> createdMissionIds = new List<string>();

                Mission created = await CreateUnboundMissionAsync(authClient, createdMissionIds, "Bounce Back", "Research");
                string missionId = created.Id;

                await TransitionAndAssertAsync(authClient, missionId, "Assigned");
                await TransitionAndAssertAsync(authClient, missionId, "InProgress");
                await TransitionAndAssertAsync(authClient, missionId, "Testing");
                await TransitionAndAssertAsync(authClient, missionId, "InProgress");
                await TransitionAndAssertAsync(authClient, missionId, "Testing");
                await TransitionAndAssertAsync(authClient, missionId, "Complete");
            }));

            cases.Add(CaseAsync("status_transition_review_bounce_back_to_in_progress_then_complete", "StatusTransition_ReviewBounceBackToInProgress_ThenComplete", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdFleetIds = new List<string>();
                List<string> createdVesselIds = new List<string>();
                List<string> createdMissionIds = new List<string>();

                Mission created = await CreateUnboundMissionAsync(authClient, createdMissionIds, "Review Bounce", "Research");
                string missionId = created.Id;

                await TransitionAndAssertAsync(authClient, missionId, "Assigned");
                await TransitionAndAssertAsync(authClient, missionId, "InProgress");
                await TransitionAndAssertAsync(authClient, missionId, "Review");
                await TransitionAndAssertAsync(authClient, missionId, "InProgress");
                await TransitionAndAssertAsync(authClient, missionId, "Complete");
            }));

            #endregion

            #region StatusTransition-Invalid

            cases.Add(CaseAsync("status_transition_complete_to_anything_fails", "StatusTransition_CompleteToAnything_Fails", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdFleetIds = new List<string>();
                List<string> createdVesselIds = new List<string>();
                List<string> createdMissionIds = new List<string>();

                Mission created = await CreateUnboundMissionAsync(authClient, createdMissionIds, "CompleteTerminal", "Research");
                string missionId = created.Id;

                await TransitionAndAssertAsync(authClient, missionId, "Assigned");
                await TransitionAndAssertAsync(authClient, missionId, "InProgress");
                await TransitionAndAssertAsync(authClient, missionId, "Complete");

                string[] targets = new[] { "Pending", "Assigned", "InProgress", "Testing", "Review", "Failed", "Cancelled" };
                foreach (string target in targets)
                {
                    HttpResponseMessage response = await TransitionAsync(authClient, missionId, target);
                    string body = await response.Content.ReadAsStringAsync();
                    ArmadaErrorResponse error = JsonHelper.Deserialize<ArmadaErrorResponse>(body);
                    Assert(
                        error.Error != null || error.Message != null,
                        "Expected error for Complete->" + target + " but got: " + body);
                }
            }));

            #endregion

            #region List-Pagination

            cases.Add(CaseAsync("list_missions_pagination_25_missions_page_size_10_returns_correct_counts", "ListMissions_Pagination_25Missions_PageSize10_ReturnsCorrectCounts", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureMissionsSeededAsync(authClient);

                HttpResponseMessage page1Resp = await authClient.GetAsync("/api/v1/missions?pageSize=10&pageNumber=1");
                EnumerationResult<Mission> page1 = await JsonHelper.DeserializeAsync<EnumerationResult<Mission>>(page1Resp);
                AssertEqual(10, page1.Objects.Count);
                AssertEqual(1, page1.PageNumber);
                AssertEqual(10, page1.PageSize);
            }));

            cases.Add(CaseAsync("list_missions_pagination_page_2", "ListMissions_Pagination_Page2", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureMissionsSeededAsync(authClient);

                HttpResponseMessage page2Resp = await authClient.GetAsync("/api/v1/missions?pageSize=10&pageNumber=2");
                EnumerationResult<Mission> page2 = await JsonHelper.DeserializeAsync<EnumerationResult<Mission>>(page2Resp);
                AssertEqual(10, page2.Objects.Count);
                AssertEqual(2, page2.PageNumber);
            }));

            cases.Add(CaseAsync("list_missions_pagination_last_page_partial_results", "ListMissions_Pagination_LastPage_PartialResults", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureMissionsSeededAsync(authClient);

                // With shared data, just verify that a page beyond total returns empty
                HttpResponseMessage resp = await authClient.GetAsync("/api/v1/missions?pageSize=10&pageNumber=1");
                EnumerationResult<Mission> firstPage = await JsonHelper.DeserializeAsync<EnumerationResult<Mission>>(resp);
                int totalPages = firstPage.TotalPages;

                // The last page should have <= 10 items
                HttpResponseMessage lastPageResp = await authClient.GetAsync("/api/v1/missions?pageSize=10&pageNumber=" + totalPages);
                EnumerationResult<Mission> lastPage = await JsonHelper.DeserializeAsync<EnumerationResult<Mission>>(lastPageResp);
                int lastPageCount = lastPage.Objects.Count;
                AssertTrue(lastPageCount > 0 && lastPageCount <= 10, "Last page should have 1-10 items");
                AssertEqual(totalPages, lastPage.PageNumber);
            }));

            cases.Add(CaseAsync("list_missions_pagination_beyond_last_page_returns_empty", "ListMissions_Pagination_BeyondLastPage_ReturnsEmpty", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureMissionsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/missions?pageSize=10&pageNumber=99");
                EnumerationResult<Mission> result = await JsonHelper.DeserializeAsync<EnumerationResult<Mission>>(response);
                AssertEqual(0, result.Objects.Count);
            }));

            cases.Add(CaseAsync("list_missions_pagination_page_size_1_each_page_has_one_record", "ListMissions_Pagination_PageSize1_EachPageHasOneRecord", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureMissionsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/missions?pageSize=1&pageNumber=1");
                EnumerationResult<Mission> result = await JsonHelper.DeserializeAsync<EnumerationResult<Mission>>(response);
                AssertEqual(1, result.Objects.Count);
            }));

            cases.Add(CaseAsync("list_missions_pages_contain_distinct_records", "ListMissions_PagesContainDistinctRecords", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdFleetIds = new List<string>();
                List<string> createdVesselIds = new List<string>();
                List<string> createdMissionIds = new List<string>();

                string vesselId = await SetupVesselAsync(authClient, createdFleetIds, createdVesselIds);
                for (int i = 1; i <= 6; i++)
                {
                    await CreateMissionAsync(authClient, createdMissionIds, vesselId, "Distinct Mission " + i);
                }

                HttpResponseMessage page1Resp = await authClient.GetAsync("/api/v1/missions?pageSize=3&pageNumber=1");
                EnumerationResult<Mission> page1 = await JsonHelper.DeserializeAsync<EnumerationResult<Mission>>(page1Resp);

                HttpResponseMessage page2Resp = await authClient.GetAsync("/api/v1/missions?pageSize=3&pageNumber=2");
                EnumerationResult<Mission> page2 = await JsonHelper.DeserializeAsync<EnumerationResult<Mission>>(page2Resp);

                List<string> page1Ids = page1.Objects.Select(m => m.Id).ToList();

                foreach (Mission obj in page2.Objects)
                {
                    AssertFalse(page1Ids.Contains(obj.Id));
                }
            }));

            #endregion

            #region Enumerate-POST

            cases.Add(CaseAsync("enumerate_with_pagination", "Enumerate_WithPagination", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdFleetIds = new List<string>();
                List<string> createdVesselIds = new List<string>();
                List<string> createdMissionIds = new List<string>();

                string vesselId = await SetupVesselAsync(authClient, createdFleetIds, createdVesselIds);
                for (int i = 1; i <= 15; i++)
                {
                    await CreateMissionAsync(authClient, createdMissionIds, vesselId, "EnumPage Mission " + i);
                }

                StringContent content = JsonHelper.ToJsonContent(new { PageNumber = 1, PageSize = 5 });
                HttpResponseMessage response = await authClient.PostAsync("/api/v1/missions/enumerate", content);
                EnumerationResult<Mission> result = await JsonHelper.DeserializeAsync<EnumerationResult<Mission>>(response);

                AssertEqual(5, result.Objects.Count);
            }));

            cases.Add(CaseAsync("enumerate_page_2_returns_correct_page", "Enumerate_Page2_ReturnsCorrectPage", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdFleetIds = new List<string>();
                List<string> createdVesselIds = new List<string>();
                List<string> createdMissionIds = new List<string>();

                string vesselId = await SetupVesselAsync(authClient, createdFleetIds, createdVesselIds);
                for (int i = 1; i <= 8; i++)
                {
                    await CreateMissionAsync(authClient, createdMissionIds, vesselId, "EnumPage2 Mission " + i);
                }

                StringContent content = JsonHelper.ToJsonContent(new { PageNumber = 2, PageSize = 3 });
                HttpResponseMessage response = await authClient.PostAsync("/api/v1/missions/enumerate", content);
                EnumerationResult<Mission> result = await JsonHelper.DeserializeAsync<EnumerationResult<Mission>>(response);

                AssertEqual(3, result.Objects.Count);
                AssertEqual(2, result.PageNumber);
            }));

            #endregion

            return new TestSuiteDescriptor(
                suiteId: "E2E.Mission",
                displayName: "Missions",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Ensure the shared mission dataset holds at least 25 listable missions, seeding it exactly once
        /// for the suite. Every case runs against the same in-process server fixture and the list pagination
        /// cases only assert accumulation-tolerant conditions (a full page slice, a last page with 1-10
        /// items, an empty page beyond the end, a single-record page), so they can share one dataset instead
        /// of each re-creating a vessel and 25 missions over sequential HTTP round-trips. The seed Task is
        /// memoized: the first pagination case to run pays the cost and the rest await the completed Task.
        /// </summary>
        /// <param name="authClient">Authenticated client for the shared e2e server.</param>
        /// <returns>A task that completes once the shared dataset exists.</returns>
        private Task EnsureMissionsSeededAsync(HttpClient authClient)
        {
            lock (_SeedLock)
            {
                if (_SharedMissionSeed == null) _SharedMissionSeed = SeedSharedMissionsAsync(authClient);
                return _SharedMissionSeed;
            }
        }

        /// <summary>
        /// Create a dedicated vessel and 25 missions on it exactly once for the shared pagination dataset.
        /// </summary>
        /// <param name="authClient">Authenticated client for the shared e2e server.</param>
        /// <returns>A task that completes once the vessel and its 25 missions exist.</returns>
        private static async Task SeedSharedMissionsAsync(HttpClient authClient)
        {
            List<string> createdFleetIds = new List<string>();
            List<string> createdVesselIds = new List<string>();
            List<string> createdMissionIds = new List<string>();

            string vesselId = await SetupVesselAsync(authClient, createdFleetIds, createdVesselIds);
            for (int i = 1; i <= 25; i++)
            {
                await CreateMissionAsync(authClient, createdMissionIds, vesselId, "SharedPagMission " + i);
            }
        }

        /// <summary>
        /// Creates a fleet and returns its ID.
        /// </summary>
        private static async Task<string> CreateFleetAsync(HttpClient client, List<string> createdFleetIds)
        {
            StringContent content = JsonHelper.ToJsonContent(new { Name = "MissionTestFleet-" + Guid.NewGuid().ToString("N").Substring(0, 8) });
            HttpResponseMessage resp = await client.PostAsync("/api/v1/fleets", content);
            string body = await resp.Content.ReadAsStringAsync();
            Fleet fleet = JsonHelper.Deserialize<Fleet>(body);
            if (String.IsNullOrEmpty(fleet.Id))
                throw new Exception("CreateFleetAsync failed (" + (int)resp.StatusCode + "): " + body);
            createdFleetIds.Add(fleet.Id);
            return fleet.Id;
        }

        /// <summary>
        /// Creates a vessel against a local bare repo in the given fleet and returns its ID.
        /// </summary>
        private static async Task<string> CreateVesselAsync(HttpClient client, List<string> createdVesselIds, string fleetId)
        {
            string repoUrl = TestRepoHelper.GetLocalBareRepoUrl();
            StringContent content = JsonHelper.ToJsonContent(new { Name = "MissionTestVessel-" + Guid.NewGuid().ToString("N").Substring(0, 8), RepoUrl = repoUrl, FleetId = fleetId });
            HttpResponseMessage resp = await client.PostAsync("/api/v1/vessels", content);
            string body = await resp.Content.ReadAsStringAsync();
            Vessel vessel = JsonHelper.Deserialize<Vessel>(body);
            if (String.IsNullOrEmpty(vessel.Id))
                throw new Exception("CreateVesselAsync failed (" + (int)resp.StatusCode + "): " + body);
            createdVesselIds.Add(vessel.Id);
            return vessel.Id;
        }

        /// <summary>
        /// Creates a mission and returns the deserialized Mission object.
        /// </summary>
        private static async Task<Mission> CreateMissionAsync(HttpClient client, List<string> createdMissionIds, string vesselId, string title, string? voyageId = null, int priority = 100, string? description = null, string? captainId = null)
        {
            object requestBody;
            if (voyageId != null && captainId != null)
                requestBody = new { Title = title, VesselId = vesselId, VoyageId = voyageId, Priority = priority, Description = description ?? "", CaptainId = captainId };
            else if (voyageId != null)
                requestBody = new { Title = title, VesselId = vesselId, VoyageId = voyageId, Priority = priority, Description = description ?? "" };
            else if (captainId != null)
                requestBody = new { Title = title, VesselId = vesselId, Priority = priority, Description = description ?? "", CaptainId = captainId };
            else
                requestBody = new { Title = title, VesselId = vesselId, Priority = priority, Description = description ?? "" };

            StringContent content = JsonHelper.ToJsonContent(requestBody);
            HttpResponseMessage resp = await client.PostAsync("/api/v1/missions", content);
            string body = await resp.Content.ReadAsStringAsync();

            // When mission stays Pending (no captain available), the API returns
            // { "Mission": {...}, "Warning": "..." } instead of the mission directly.
            MissionCreateResponse wrapper = JsonHelper.Deserialize<MissionCreateResponse>(body);
            Mission mission;
            if (wrapper.Mission != null)
                mission = wrapper.Mission;
            else
                mission = JsonHelper.Deserialize<Mission>(body);

            if (String.IsNullOrEmpty(mission.Id))
                throw new Exception("CreateMissionAsync failed (" + (int)resp.StatusCode + "): " + body);
            createdMissionIds.Add(mission.Id);
            return mission;
        }

        /// <summary>
        /// Issues a status transition PUT request and returns the raw response.
        /// </summary>
        private static async Task<HttpResponseMessage> TransitionAsync(HttpClient client, string missionId, string status)
        {
            StringContent content = JsonHelper.ToJsonContent(new { Status = status });
            return await client.PutAsync("/api/v1/missions/" + missionId + "/status", content);
        }

        /// <summary>
        /// Issues a status transition and asserts it succeeded with the expected status.
        /// </summary>
        private static async Task TransitionAndAssertAsync(HttpClient client, string missionId, string status)
        {
            HttpResponseMessage resp = await TransitionAsync(client, missionId, status);
            AssertEqual(HttpStatusCode.OK, resp.StatusCode);
            Mission transitioned = await JsonHelper.DeserializeAsync<Mission>(resp);
            AssertEqual(status, transitioned.Status.ToString());
        }

        /// <summary>
        /// Creates a fleet and a vessel in it, returning the vessel ID.
        /// </summary>
        private static async Task<string> SetupVesselAsync(HttpClient client, List<string> createdFleetIds, List<string> createdVesselIds)
        {
            string fleetId = await CreateFleetAsync(client, createdFleetIds);
            return await CreateVesselAsync(client, createdVesselIds, fleetId);
        }

        /// <summary>
        /// Creates a mission without a vessel, so no idle captain on the shared server can claim it.
        /// A Research mode mission satisfies the manual completion proof as report-only work; an
        /// Implementation mission without a vessel or commit cannot prove target ancestry.
        /// </summary>
        private static async Task<Mission> CreateUnboundMissionAsync(HttpClient client, List<string> createdMissionIds, string title, string? mode)
        {
            object requestBody = mode != null
                ? (object)new { Title = title, Mode = mode }
                : new { Title = title };
            HttpResponseMessage resp = await client.PostAsync("/api/v1/missions", JsonHelper.ToJsonContent(requestBody));
            string body = await resp.Content.ReadAsStringAsync();
            MissionCreateResponse wrapper = JsonHelper.Deserialize<MissionCreateResponse>(body);
            Mission mission = wrapper.Mission ?? JsonHelper.Deserialize<Mission>(body);
            if (String.IsNullOrEmpty(mission.Id))
                throw new Exception("CreateUnboundMissionAsync failed (" + (int)resp.StatusCode + "): " + body);
            createdMissionIds.Add(mission.Id);
            return mission;
        }

        /// <summary>
        /// Requests Complete and asserts the named refusal with the stored status left unchanged.
        /// </summary>
        private static async Task AssertCompleteRefusedAsync(HttpClient client, string missionId, string reason, string unchangedStatus)
        {
            HttpResponseMessage response = await TransitionAsync(client, missionId, "Complete");
            string body = await response.Content.ReadAsStringAsync();
            AssertEqual(HttpStatusCode.Conflict, response.StatusCode);
            AssertTrue(body.Contains(reason, StringComparison.Ordinal), "refusal names " + reason + ": " + body);

            HttpResponseMessage read = await client.GetAsync("/api/v1/missions/" + missionId);
            Mission stored = await JsonHelper.DeserializeAsync<Mission>(read);
            AssertEqual(unchangedStatus, stored.Status.ToString());
            AssertTrue(stored.CompletedUtc == null, "refused completion does not stamp CompletedUtc");
        }

        private TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "E2E.Mission",
                caseId: caseId,
                displayName: displayName,
                executeAsync: async (CancellationToken ct) =>
                {
                    // Cancel the case's active work so accumulated rows never exhaust fleet capacity for later cases.
                    try
                    {
                        await body().ConfigureAwait(false);
                    }
                    finally
                    {
                        await E2EServerFixture.CancelActiveWorkAsync(this).ConfigureAwait(false);
                    }
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
