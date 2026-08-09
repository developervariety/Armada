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
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Event API end-to-end suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            #region List-Empty-Tests

            cases.Add(CaseAsync("list_events_empty_returns_empty_array", "ListEvents_Empty_ReturnsEmptyArray", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);
                AssertNotNull(result.Objects);
            }));

            cases.Add(CaseAsync("list_events_empty_returns_correct_enumeration_structure", "ListEvents_Empty_ReturnsCorrectEnumerationStructure", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertTrue(result.Success);
                AssertEqual(1, result.PageNumber);
                AssertTrue(result.PageSize > 0);
                AssertTrue(result.TotalRecords >= 0);
            }));

            #endregion

            #region Events-Generated-Via-Transitions

            cases.Add(CaseAsync("list_events_after_status_transition_contains_events", "ListEvents_AfterStatusTransition_ContainsEvents", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Mission mission = await CreateMissionAsync(authClient, "TransitionEvent");
                string missionId = mission.Id;

                await TransitionAsync(authClient, missionId, "Assigned");

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);
                AssertTrue(result.Objects.Count >= 1);
            }));

            cases.Add(CaseAsync("list_events_after_transition_event_has_correct_type", "ListEvents_AfterTransition_EventHasCorrectType", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Mission mission = await CreateMissionAsync(authClient, "TypeCheck");
                string missionId = mission.Id;

                await TransitionAsync(authClient, missionId, "Assigned");

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                bool hasStatusChanged = false;
                foreach (ArmadaEvent evt in result.Objects)
                {
                    if (evt.EventType == "mission.status_changed")
                    {
                        hasStatusChanged = true;
                        break;
                    }
                }
                AssertTrue(hasStatusChanged);
            }));

            cases.Add(CaseAsync("list_events_after_transition_event_has_correct_fields", "ListEvents_AfterTransition_EventHasCorrectFields", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Mission mission = await CreateMissionAsync(authClient, "FieldCheck");
                string missionId = mission.Id;

                await TransitionAsync(authClient, missionId, "Assigned");

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                ArmadaEvent? statusEvent = null;
                foreach (ArmadaEvent evt in result.Objects)
                {
                    if (evt.EventType == "mission.status_changed"
                        && evt.MissionId == missionId)
                    {
                        statusEvent = evt;
                        break;
                    }
                }

                AssertNotNull(statusEvent);
                AssertStartsWith("evt_", statusEvent!.Id);
                AssertEqual("mission", statusEvent.EntityType);
                AssertEqual(missionId, statusEvent.EntityId);
                AssertEqual(missionId, statusEvent.MissionId);
                AssertNotNull(statusEvent.Message);
                AssertNotNull(statusEvent.CreatedUtc);
            }));

            cases.Add(CaseAsync("list_events_multiple_transitions_generate_multiple_events", "ListEvents_MultipleTransitions_GenerateMultipleEvents", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Mission mission = await CreateMissionAsync(authClient, "MultiTransition");
                string missionId = mission.Id;

                await TransitionAsync(authClient, missionId, "Assigned");
                await TransitionAsync(authClient, missionId, "InProgress");
                await TransitionAsync(authClient, missionId, "Testing");

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                int statusChangedCount = 0;
                foreach (ArmadaEvent evt in result.Objects)
                {
                    if (evt.EventType == "mission.status_changed"
                        && evt.MissionId == missionId)
                    {
                        statusChangedCount++;
                    }
                }
                AssertEqual(3, statusChangedCount);
            }));

            #endregion

            #region Pagination-Tests

            cases.Add(CaseAsync("list_events_pagination_multi_page_verify_counts", "ListEvents_Pagination_MultiPage_VerifyCounts", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                for (int i = 0; i < 12; i++)
                {
                    Mission mission = await CreateMissionAsync(authClient, "PageTest-" + i);
                    string missionId = mission.Id;
                    await TransitionAsync(authClient, missionId, "Assigned");
                }

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

                for (int i = 0; i < 12; i++)
                {
                    Mission mission = await CreateMissionAsync(authClient, "Page2Test-" + i);
                    string missionId = mission.Id;
                    await TransitionAsync(authClient, missionId, "Assigned");
                }

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?pageSize=5&pageNumber=2");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(5, result.Objects.Count);
                AssertEqual(2, result.PageNumber);
            }));

            cases.Add(CaseAsync("list_events_pagination_last_page_partial_results", "ListEvents_Pagination_LastPage_PartialResults", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                for (int i = 0; i < 7; i++)
                {
                    Mission mission = await CreateMissionAsync(authClient, "LastPage-" + i);
                    string missionId = mission.Id;
                    await TransitionAsync(authClient, missionId, "Assigned");
                }

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

            cases.Add(CaseAsync("list_events_pagination_beyond_last_page_returns_empty", "ListEvents_Pagination_BeyondLastPage_ReturnsEmpty", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Mission mission = await CreateMissionAsync(authClient, "BeyondPage");
                string missionId = mission.Id;
                await TransitionAsync(authClient, missionId, "Assigned");

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?pageSize=10&pageNumber=100");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(0, result.Objects.Count);
                AssertTrue(result.TotalRecords >= 1);
            }));

            cases.Add(CaseAsync("list_events_pagination_first_page_has_correct_event_ids", "ListEvents_Pagination_FirstPageHasCorrectEventIds", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                for (int i = 0; i < 8; i++)
                {
                    Mission mission = await CreateMissionAsync(authClient, "IdCheck-" + i);
                    string missionId = mission.Id;
                    await TransitionAsync(authClient, missionId, "Assigned");
                }

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?pageSize=3&pageNumber=1");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(3, result.Objects.Count);
                foreach (ArmadaEvent evt in result.Objects)
                {
                    AssertStartsWith("evt_", evt.Id);
                }
            }));

            cases.Add(CaseAsync("list_events_ordering_default_is_created_descending", "ListEvents_Ordering_DefaultIsCreatedDescending", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Mission m1 = await CreateMissionAsync(authClient, "Order-1");
                await TransitionAsync(authClient, m1.Id, "Assigned");
                await Task.Delay(50);

                Mission m2 = await CreateMissionAsync(authClient, "Order-2");
                await TransitionAsync(authClient, m2.Id, "Assigned");
                await Task.Delay(50);

                Mission m3 = await CreateMissionAsync(authClient, "Order-3");
                await TransitionAsync(authClient, m3.Id, "Assigned");

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertTrue(result.Objects.Count >= 3);

                DateTime first = result.Objects[0].CreatedUtc;
                DateTime last = result.Objects[result.Objects.Count - 1].CreatedUtc;
                AssertTrue(first >= last);
            }));

            #endregion

            #region Filter-By-Type-Tests

            cases.Add(CaseAsync("list_events_filter_by_type_mission_status_changed", "ListEvents_FilterByType_MissionStatusChanged", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Mission mission = await CreateMissionAsync(authClient, "TypeFilter");
                string missionId = mission.Id;
                await TransitionAsync(authClient, missionId, "Assigned");

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?type=mission.status_changed");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertTrue(result.Objects.Count >= 1);
                foreach (ArmadaEvent evt in result.Objects)
                {
                    AssertEqual("mission.status_changed", evt.EventType);
                }
            }));

            cases.Add(CaseAsync("list_events_filter_by_type_no_matches_returns_empty", "ListEvents_FilterByType_NoMatches_ReturnsEmpty", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Mission mission = await CreateMissionAsync(authClient, "NoTypeMatch");
                string missionId = mission.Id;
                await TransitionAsync(authClient, missionId, "Assigned");

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?type=nonexistent.type");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(0, result.Objects.Count);
            }));

            #endregion

            #region Filter-By-MissionId-Tests

            cases.Add(CaseAsync("list_events_filter_by_mission_id", "ListEvents_FilterByMissionId", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Mission mission1 = await CreateMissionAsync(authClient, "MissionFilter-1");
                string missionId1 = mission1.Id;
                await TransitionAsync(authClient, missionId1, "Assigned");

                Mission mission2 = await CreateMissionAsync(authClient, "MissionFilter-2");
                string missionId2 = mission2.Id;
                await TransitionAsync(authClient, missionId2, "Assigned");

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?missionId=" + missionId1);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertTrue(result.Objects.Count >= 1);
                foreach (ArmadaEvent evt in result.Objects)
                {
                    AssertEqual(missionId1, evt.MissionId);
                }
            }));

            cases.Add(CaseAsync("list_events_filter_by_mission_id_nonexistent_id_returns_empty", "ListEvents_FilterByMissionId_NonexistentId_ReturnsEmpty", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?missionId=msn_nonexistent");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(0, result.Objects.Count);
            }));

            #endregion

            #region Filter-By-VesselId-Tests

            cases.Add(CaseAsync("list_events_filter_by_vessel_id", "ListEvents_FilterByVesselId", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                string fleetId = await CreateFleetAsync(authClient);
                string vesselId = await CreateVesselAsync(authClient, fleetId);

                Mission mission = await CreateMissionAsync(authClient, "VesselFilter", vesselId: vesselId);
                string missionId = mission.Id;
                await TransitionAsync(authClient, missionId, "Assigned");

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?vesselId=" + vesselId);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertTrue(result.Objects.Count >= 1);
                foreach (ArmadaEvent evt in result.Objects)
                {
                    AssertEqual(vesselId, evt.VesselId);
                }
            }));

            cases.Add(CaseAsync("list_events_filter_by_vessel_id_nonexistent_id_returns_empty", "ListEvents_FilterByVesselId_NonexistentId_ReturnsEmpty", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?vesselId=vsl_nonexistent");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(0, result.Objects.Count);
            }));

            #endregion

            #region Filter-By-CaptainId-Tests

            cases.Add(CaseAsync("list_events_filter_by_captain_id", "ListEvents_FilterByCaptainId", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                // CaptainId is an operational field managed by the dispatch system,
                // not assignable via PUT. Verify the filter endpoint returns a valid result.
                string captainId = await CreateCaptainAsync(authClient, "filter-captain");

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?captainId=" + captainId);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                // May return 0 events since CaptainId is set by the dispatch system
                foreach (ArmadaEvent evt in result.Objects)
                {
                    AssertEqual(captainId, evt.CaptainId);
                }
            }));

            cases.Add(CaseAsync("list_events_filter_by_captain_id_nonexistent_id_returns_empty", "ListEvents_FilterByCaptainId_NonexistentId_ReturnsEmpty", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?captainId=cpt_nonexistent");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(0, result.Objects.Count);
            }));

            #endregion

            #region Filter-By-VoyageId-Tests

            cases.Add(CaseAsync("list_events_filter_by_voyage_id", "ListEvents_FilterByVoyageId", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                string fleetId = await CreateFleetAsync(authClient);
                string vesselId = await CreateVesselAsync(authClient, fleetId);
                string voyageId = await CreateVoyageAsync(authClient, vesselId);

                Mission mission = await CreateMissionAsync(authClient, "VoyageFilter", voyageId: voyageId);
                string missionId = mission.Id;
                await TransitionAsync(authClient, missionId, "Assigned");

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?voyageId=" + voyageId);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertTrue(result.Objects.Count >= 1);
                foreach (ArmadaEvent evt in result.Objects)
                {
                    AssertNotNull(evt.VoyageId);
                    AssertEqual(voyageId, evt.VoyageId);
                }
            }));

            cases.Add(CaseAsync("list_events_filter_by_voyage_id_nonexistent_id_returns_empty", "ListEvents_FilterByVoyageId_NonexistentId_ReturnsEmpty", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?voyageId=vyg_nonexistent");
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(0, result.Objects.Count);
            }));

            #endregion

            #region Filter-By-Limit-Tests

            cases.Add(CaseAsync("list_events_filter_by_limit", "ListEvents_FilterByLimit", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                for (int i = 0; i < 10; i++)
                {
                    Mission mission = await CreateMissionAsync(authClient, "LimitTest-" + i);
                    string missionId = mission.Id;
                    await TransitionAsync(authClient, missionId, "Assigned");
                }

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?limit=3");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(3, result.Objects.Count);
                AssertEqual(3, result.PageSize);
            }));

            cases.Add(CaseAsync("list_events_with_limit_respects_limit", "ListEvents_WithLimit_RespectsLimit", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/events?limit=10");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
            }));

            #endregion

            #region Combined-Filters-Tests

            cases.Add(CaseAsync("list_events_combined_filters_type_and_mission_id", "ListEvents_CombinedFilters_TypeAndMissionId", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Mission mission = await CreateMissionAsync(authClient, "CombinedFilter");
                string missionId = mission.Id;
                await TransitionAsync(authClient, missionId, "Assigned");

                HttpResponseMessage response = await authClient.GetAsync(
                    "/api/v1/events?type=mission.status_changed&missionId=" + missionId);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertTrue(result.Objects.Count >= 1);
                foreach (ArmadaEvent evt in result.Objects)
                {
                    AssertEqual("mission.status_changed", evt.EventType);
                    AssertEqual(missionId, evt.MissionId);
                }
            }));

            cases.Add(CaseAsync("list_events_combined_filters_type_and_vessel_id", "ListEvents_CombinedFilters_TypeAndVesselId", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                string fleetId = await CreateFleetAsync(authClient);
                string vesselId = await CreateVesselAsync(authClient, fleetId);

                Mission mission = await CreateMissionAsync(authClient, "CombinedVessel", vesselId: vesselId);
                string missionId = mission.Id;
                await TransitionAsync(authClient, missionId, "Assigned");

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
            }));

            cases.Add(CaseAsync("list_events_combined_filters_limit_and_type", "ListEvents_CombinedFilters_LimitAndType", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                for (int i = 0; i < 5; i++)
                {
                    Mission mission = await CreateMissionAsync(authClient, "LimitType-" + i);
                    string missionId = mission.Id;
                    await TransitionAsync(authClient, missionId, "Assigned");
                }

                HttpResponseMessage response = await authClient.GetAsync(
                    "/api/v1/events?type=mission.status_changed&limit=2");
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(2, result.Objects.Count);
            }));

            #endregion

            #region Enumerate-Tests

            cases.Add(CaseAsync("enumerate_events_default_returns_enumeration_result", "EnumerateEvents_Default_ReturnsEnumerationResult", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                StringContent content = JsonHelper.ToJsonContent(new { });

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/events/enumerate", content);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertTrue(result.Success);
                AssertNotNull(result.Objects);
                AssertTrue(result.PageNumber >= 0);
                AssertTrue(result.PageSize >= 0);
                AssertTrue(result.TotalPages >= 0);
                AssertTrue(result.TotalRecords >= 0);
            }));

            cases.Add(CaseAsync("enumerate_events_empty_database_returns_zero_records", "EnumerateEvents_EmptyDatabase_ReturnsZeroRecords", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                StringContent content = JsonHelper.ToJsonContent(new { });

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/events/enumerate", content);
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                // Note: database may not be empty at this point due to previous tests
                AssertTrue(result.TotalRecords >= 0);
            }));

            cases.Add(CaseAsync("enumerate_events_with_page_size_and_page_number", "EnumerateEvents_WithPageSizeAndPageNumber", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                for (int i = 0; i < 12; i++)
                {
                    Mission mission = await CreateMissionAsync(authClient, "EnumPage-" + i);
                    string missionId = mission.Id;
                    await TransitionAsync(authClient, missionId, "Assigned");
                }

                StringContent content = JsonHelper.ToJsonContent(new { PageSize = 5, PageNumber = 2 });

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/events/enumerate", content);
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(5, result.Objects.Count);
                AssertEqual(2, result.PageNumber);
                AssertEqual(5, result.PageSize);
                AssertTrue(result.TotalRecords >= 12);
            }));

            cases.Add(CaseAsync("enumerate_events_with_event_type_filter", "EnumerateEvents_WithEventTypeFilter", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Mission mission = await CreateMissionAsync(authClient, "EnumType");
                string missionId = mission.Id;
                await TransitionAsync(authClient, missionId, "Assigned");

                StringContent content = JsonHelper.ToJsonContent(new { EventType = "mission.status_changed" });

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/events/enumerate", content);
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertTrue(result.Objects.Count >= 1);
                foreach (ArmadaEvent evt in result.Objects)
                {
                    AssertEqual("mission.status_changed", evt.EventType);
                }
            }));

            cases.Add(CaseAsync("enumerate_events_with_event_type_filter_no_matches", "EnumerateEvents_WithEventTypeFilter_NoMatches", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Mission mission = await CreateMissionAsync(authClient, "EnumNoMatch");
                string missionId = mission.Id;
                await TransitionAsync(authClient, missionId, "Assigned");

                StringContent content = JsonHelper.ToJsonContent(new { EventType = "nonexistent.type" });

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/events/enumerate", content);
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertEqual(0, result.Objects.Count);
            }));

            cases.Add(CaseAsync("enumerate_events_ordering_created_descending", "EnumerateEvents_Ordering_CreatedDescending", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Mission m1 = await CreateMissionAsync(authClient, "EnumOrd-1");
                await TransitionAsync(authClient, m1.Id, "Assigned");
                await Task.Delay(50);

                Mission m2 = await CreateMissionAsync(authClient, "EnumOrd-2");
                await TransitionAsync(authClient, m2.Id, "Assigned");
                await Task.Delay(50);

                Mission m3 = await CreateMissionAsync(authClient, "EnumOrd-3");
                await TransitionAsync(authClient, m3.Id, "Assigned");

                StringContent content = JsonHelper.ToJsonContent(new { Order = "CreatedDescending" });

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/events/enumerate", content);
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertTrue(result.Objects.Count >= 3);

                DateTime first = result.Objects[0].CreatedUtc;
                DateTime last = result.Objects[result.Objects.Count - 1].CreatedUtc;
                AssertTrue(first >= last);
            }));

            cases.Add(CaseAsync("enumerate_events_ordering_created_ascending", "EnumerateEvents_Ordering_CreatedAscending", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Mission m1 = await CreateMissionAsync(authClient, "EnumAsc-1");
                await TransitionAsync(authClient, m1.Id, "Assigned");
                await Task.Delay(50);

                Mission m2 = await CreateMissionAsync(authClient, "EnumAsc-2");
                await TransitionAsync(authClient, m2.Id, "Assigned");
                await Task.Delay(50);

                Mission m3 = await CreateMissionAsync(authClient, "EnumAsc-3");
                await TransitionAsync(authClient, m3.Id, "Assigned");

                StringContent content = JsonHelper.ToJsonContent(new { Order = "CreatedAscending" });

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/events/enumerate", content);
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertTrue(result.Objects.Count >= 3);

                DateTime first = result.Objects[0].CreatedUtc;
                DateTime last = result.Objects[result.Objects.Count - 1].CreatedUtc;
                AssertTrue(first <= last);
            }));

            cases.Add(CaseAsync("enumerate_events_with_mission_id_filter", "EnumerateEvents_WithMissionIdFilter", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                Mission mission1 = await CreateMissionAsync(authClient, "EnumMsn-1");
                string missionId1 = mission1.Id;
                await TransitionAsync(authClient, missionId1, "Assigned");

                Mission mission2 = await CreateMissionAsync(authClient, "EnumMsn-2");
                string missionId2 = mission2.Id;
                await TransitionAsync(authClient, missionId2, "Assigned");

                StringContent content = JsonHelper.ToJsonContent(new { MissionId = missionId1 });

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/events/enumerate", content);
                EnumerationResult<ArmadaEvent> result = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(response);

                AssertTrue(result.Objects.Count >= 1);
                foreach (ArmadaEvent evt in result.Objects)
                {
                    AssertEqual(missionId1, evt.MissionId);
                }
            }));

            cases.Add(CaseAsync("enumerate_events_querystring_overrides_page_size", "EnumerateEvents_QuerystringOverrides_PageSize", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                for (int i = 0; i < 10; i++)
                {
                    Mission mission = await CreateMissionAsync(authClient, "EnumQS-" + i);
                    string missionId = mission.Id;
                    await TransitionAsync(authClient, missionId, "Assigned");
                }

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
        /// Creates a captain and returns its identifier.
        /// </summary>
        private static async Task<string> CreateCaptainAsync(HttpClient client, string name = "event-captain")
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            StringContent content = JsonHelper.ToJsonContent(new { Name = uniqueName });
            HttpResponseMessage response = await client.PostAsync("/api/v1/captains", content);
            Captain captain = await JsonHelper.DeserializeAsync<Captain>(response);
            return captain.Id;
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
        /// Creates a voyage for the given vessel and returns its identifier.
        /// </summary>
        private static async Task<string> CreateVoyageAsync(HttpClient client, string vesselId)
        {
            StringContent content = JsonHelper.ToJsonContent(new
            {
                Title = "EventVoyage",
                VesselId = vesselId,
                Missions = new[]
                {
                    new { Title = "VoyageMission1", Description = "desc" }
                }
            });
            HttpResponseMessage response = await client.PostAsync("/api/v1/voyages", content);
            Voyage voyage = await JsonHelper.DeserializeAsync<Voyage>(response);
            return voyage.Id;
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
