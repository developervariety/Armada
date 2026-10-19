namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
    using Armada.Test.Automated;
    using Armada.Test.Common;

    public class VoyageTests : TestSuite
    {
        #region Public-Members

        public override string Name => "Voyages";

        #endregion

        #region Private-Members

        private HttpClient _AuthClient;
        private HttpClient _UnauthClient;
        private readonly ArmadaServer _Server;
        private List<string> _CreatedVoyageIds = new List<string>();
        private List<string> _CreatedCaptainIds = new List<string>();
        private List<string> _CreatedMissionIds = new List<string>();
        private List<string> _CreatedVesselIds = new List<string>();
        private List<string> _CreatedFleetIds = new List<string>();

        #endregion

        #region Constructors-and-Factories

        public VoyageTests(HttpClient authClient, HttpClient unauthClient, ArmadaServer server)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
            _Server = server ?? throw new ArgumentNullException(nameof(server));
        }

        #endregion

        #region Public-Methods

        protected override async Task RunTestsAsync()
        {
            await RunTest("MissionSummary_CountsAllMissionsAcrossVesselPages", async () =>
            {
                PrerequisiteResult prereqs = await CreatePrerequisitesAsync();
                try
                {
                    string secondVessel = await CreateVesselAsync(prereqs.FleetId, "SummarySecond");
                    Voyage voyage = await CreateVoyageAsync(prereqs.VesselId, "Summary voyage", missionCount: 2);
                    using (StringContent body = JsonHelper.ToJsonContent(new
                    {
                        Title = "Summary later vessel", VoyageId = voyage.Id, VesselId = secondVessel,
                        Description = "summary-payload-marker-" + new string('x', 100000)
                    }))
                    using (HttpResponseMessage created = await _AuthClient.PostAsync("/api/v1/missions", body))
                        AssertEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());

                    HashSet<string> vessels = new HashSet<string>(StringComparer.Ordinal);
                    for (int page = 1; page <= 3; page++)
                    {
                        using (HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/voyages/" + voyage.Id + "/mission-summary?pageSize=1&pageNumber=" + page))
                        {
                            AssertEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                            string json = await response.Content.ReadAsStringAsync();
                            VoyageMissionSummary summary = JsonHelper.Deserialize<VoyageMissionSummary>(json);
                            AssertEqual(3L, summary.StatusCounts.Values.Sum());
                            AssertEqual(2L, summary.Vessels.TotalRecords);
                            AssertEqual(2, summary.Vessels.TotalPages);
                            AssertEqual(page <= 2 ? 1 : 0, summary.Vessels.Objects.Count);
                            foreach (string id in summary.Vessels.Objects) AssertTrue(vessels.Add(id), "No repeated vessel across pages");
                            AssertFalse(json.Contains("summary-payload-marker"), "No mission description in summary");
                        }
                    }
                    AssertTrue(vessels.SetEquals(new[] { prereqs.VesselId, secondVessel }));
                }
                finally { await CleanupAsync(); }
            });

            await RunTest("MissionSummary_InvalidPages_Return400", async () =>
            {
                foreach (string query in new[] { "pageNumber=0", "pageNumber=-1", "pageNumber=no", "pageNumber=2147483648", "pageSize=0", "pageSize=101", "pageSize=no", "pageSize=" })
                {
                    using (HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/voyages/absent/mission-summary?" + query))
                        AssertEqual(HttpStatusCode.BadRequest, response.StatusCode, query);
                }
                using (HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/voyages/absent/mission-summary"))
                    AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            });

            await RunTest("MissionSummary_Unauthenticated_Returns401", async () =>
            {
                using (HttpResponseMessage response = await _UnauthClient.GetAsync("/api/v1/voyages/absent/mission-summary"))
                    AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/voyages/absent/mission-summary"))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "invalid-summary-token");
                    using (HttpResponseMessage response = await _UnauthClient.SendAsync(request))
                        AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                }
            });

            #region Create-Voyage-Tests

            await RunTest("CreateVoyage_BareVoyageWithoutVesselId_Returns201", async () =>
            {
                StringContent content = JsonHelper.ToJsonContent(new { Title = "Bare Voyage", Description = "No vessel" });

                HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/voyages", content);
                AssertEqual(HttpStatusCode.Created, resp.StatusCode);

                Voyage voyage = await JsonHelper.DeserializeAsync<Voyage>(resp);
                _CreatedVoyageIds.Add(voyage.Id);
                AssertStartsWith("vyg_", voyage.Id);
                AssertEqual("Open", voyage.Status.ToString());

                HttpResponseMessage eventsResp = await _AuthClient.GetAsync("/api/v1/events?type=voyage.dispatched&voyageId=" + voyage.Id);
                AssertEqual(HttpStatusCode.OK, eventsResp.StatusCode);
                EnumerationResult<ArmadaEvent> events = await JsonHelper.DeserializeAsync<EnumerationResult<ArmadaEvent>>(eventsResp);
                AssertEqual(1, events.Objects.Count, "a voyage created without missions is announced once");
                AssertEqual(voyage.Id, events.Objects[0].VoyageId);
            });

            #endregion

            #region Cancel-Voyage-Tests

            await RunTest("CancelVoyage_InProgressMission_IsCancelled", async () =>
            {
                PrerequisiteResult prereqs = await CreatePrerequisitesAsync();
                Voyage created = await CreateVoyageAsync(prereqs.VesselId, "Cancel Running Voyage", missionCount: 1);

                HttpResponseMessage detailResp = await _AuthClient.GetAsync("/api/v1/voyages/" + created.Id);
                VoyageDetailResponse before = await JsonHelper.DeserializeAsync<VoyageDetailResponse>(detailResp);
                string missionId = before.Missions![0].Id;
                // Walk the mission forward to InProgress from wherever dispatch left it.
                foreach (MissionStatusEnum from in new[] { MissionStatusEnum.Pending, MissionStatusEnum.Assigned })
                {
                    Mission current = await JsonHelper.DeserializeAsync<Mission>(await _AuthClient.GetAsync("/api/v1/missions/" + missionId));
                    if (current.Status != from) continue;
                    string next = from == MissionStatusEnum.Pending ? "Assigned" : "InProgress";
                    HttpResponseMessage transition = await _AuthClient.PutAsync("/api/v1/missions/" + missionId + "/status",
                        JsonHelper.ToJsonContent(new { Status = next }));
                    AssertEqual(HttpStatusCode.OK, transition.StatusCode, await transition.Content.ReadAsStringAsync());
                }
                Mission running = await JsonHelper.DeserializeAsync<Mission>(await _AuthClient.GetAsync("/api/v1/missions/" + missionId));
                AssertEqual(MissionStatusEnum.InProgress, running.Status, "The mission is running before the cancel");

                HttpResponseMessage cancel = await _AuthClient.DeleteAsync("/api/v1/voyages/" + created.Id);
                AssertEqual(HttpStatusCode.OK, cancel.StatusCode);
                CancelVoyageResponse cancelResp = await JsonHelper.DeserializeAsync<CancelVoyageResponse>(cancel);
                AssertEqual(1, cancelResp.CancelledMissions, "The running mission is counted as cancelled");

                Mission stored = await JsonHelper.DeserializeAsync<Mission>(await _AuthClient.GetAsync("/api/v1/missions/" + missionId));
                AssertEqual(MissionStatusEnum.Cancelled, stored.Status, "Cancelling a voyage cancels its InProgress mission");
            });

            #endregion

            #region Purge-Voyage-Tests

            await RunTest("PurgeVoyage_MissionsAlsoDeleted", async () =>
            {
                PrerequisiteResult prereqs = await CreatePrerequisitesAsync();
                string vesselId = prereqs.VesselId;

                Voyage created = await CreateVoyageAsync(vesselId, "Purge Missions Gone", missionCount: 2);
                string voyageId = created.Id;

                // Cancel first — purge is blocked on Open/InProgress voyages
                await _AuthClient.DeleteAsync("/api/v1/voyages/" + voyageId);

                HttpResponseMessage getBeforeResp = await _AuthClient.GetAsync("/api/v1/voyages/" + voyageId);
                VoyageDetailResponse detailBefore = await JsonHelper.DeserializeAsync<VoyageDetailResponse>(getBeforeResp);
                List<string> missionIds = detailBefore.Missions!.Select(m => m.Id).ToList();

                AssertTrue(missionIds.Count > 0, "Cancelled voyage must retain its missions before purge");

                HttpResponseMessage purgeResponse = await _AuthClient.DeleteAsync("/api/v1/voyages/" + voyageId + "/purge");
                AssertEqual(HttpStatusCode.OK, purgeResponse.StatusCode, await purgeResponse.Content.ReadAsStringAsync());
                _CreatedVoyageIds.Remove(voyageId);

                foreach (string missionId in missionIds)
                {
                    HttpResponseMessage missionResp = await _AuthClient.GetAsync("/api/v1/missions/" + missionId);
                    ArmadaErrorResponse error = await JsonHelper.DeserializeAsync<ArmadaErrorResponse>(missionResp);
                    AssertTrue(error.Error != null || error.Message != null, "Purged mission remains readable: " + missionResp.StatusCode);
                }
            });

            #endregion

            #region Cleanup

            await CleanupAsync();

            #endregion
        }

        #endregion

        #region Private-Methods

        private async Task<string> CreateFleetAsync(string name = "VoyageTestFleet")
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            StringContent content = JsonHelper.ToJsonContent(new { Name = uniqueName });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/fleets", content);
            AssertEqual(HttpStatusCode.Created, resp.StatusCode);
            Fleet fleet = await JsonHelper.DeserializeAsync<Fleet>(resp);
            _CreatedFleetIds.Add(fleet.Id);
            return fleet.Id;
        }

        private async Task<string> CreateVesselAsync(string fleetId, string name = "VoyageTestVessel")
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            StringContent content = JsonHelper.ToJsonContent(new { Name = uniqueName, RepoUrl = TestRepoHelper.GetLocalBareRepoUrl(), FleetId = fleetId });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/vessels", content);
            AssertEqual(HttpStatusCode.Created, resp.StatusCode);
            Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(resp);
            _CreatedVesselIds.Add(vessel.Id);
            return vessel.Id;
        }

        private async Task<Voyage> CreateVoyageAsync(string vesselId, string title, int missionCount = 1, string? description = null)
        {
            object[] missions = Enumerable.Range(1, missionCount)
                .Select(i => (object)new { Title = "Mission " + i, Description = "Description for mission " + i })
                .ToArray();
            object body = new { Title = title, Description = description ?? ("Description for " + title), VesselId = vesselId, Missions = missions };
            StringContent content = JsonHelper.ToJsonContent(body);
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/voyages", content);
            AssertEqual(HttpStatusCode.Created, resp.StatusCode, await resp.Content.ReadAsStringAsync());
            Voyage voyage = await JsonHelper.DeserializeAsync<Voyage>(resp);
            _CreatedVoyageIds.Add(voyage.Id);
            return voyage;
        }

        private async Task<PrerequisiteResult> CreatePrerequisitesAsync()
        {
            // Each case owns its voyage corpus; prior cases must not consume its admission slots.
            await CleanupAsync();
            string fleetId = await CreateFleetAsync();
            string vesselId = await CreateVesselAsync(fleetId);
            Captain dispatchCaptain = await ServerCaptainFixtures.CreateEligibleIdleCaptainAsync(
                _Server, Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId, "Worker").ConfigureAwait(false);
            _CreatedCaptainIds.Add(dispatchCaptain.Id);
            return new PrerequisiteResult(fleetId, vesselId);
        }

        private async Task CleanupAsync()
        {
            foreach (string voyageId in _CreatedVoyageIds)
            {
                await _AuthClient.DeleteAsync("/api/v1/voyages/" + voyageId);
                try { await _AuthClient.DeleteAsync("/api/v1/voyages/" + voyageId + "/purge"); } catch { }
            }

            foreach (string captainId in _CreatedCaptainIds)
            {
                await ServerCaptainFixtures.DeleteFixtureCaptainAsync(_AuthClient, captainId).ConfigureAwait(false);
            }

            foreach (string vesselId in _CreatedVesselIds)
            {
                try { await _AuthClient.DeleteAsync("/api/v1/vessels/" + vesselId); } catch { }
            }

            foreach (string fleetId in _CreatedFleetIds)
            {
                try { await _AuthClient.DeleteAsync("/api/v1/fleets/" + fleetId); } catch { }
            }
            _CreatedVoyageIds.Clear();
            _CreatedCaptainIds.Clear();
            _CreatedVesselIds.Clear();
            _CreatedFleetIds.Clear();
        }

        #endregion
    }
}
