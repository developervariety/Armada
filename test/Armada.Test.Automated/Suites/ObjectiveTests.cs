namespace Armada.Test.Automated.Suites
{
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// REST-level coverage for internal-first objective capture and history linkage.
    /// </summary>
    public class ObjectiveTests : TestSuite
    {
        private readonly HttpClient _AuthClient;
        private readonly HttpClient _UnauthClient;

        /// <inheritdoc />
        public override string Name => "Objectives";

        /// <summary>
        /// Instantiate the suite.
        /// </summary>
        public ObjectiveTests(HttpClient authClient, HttpClient unauthClient)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            string objectiveId = String.Empty;
            string vesselId = String.Empty;
            string voyageId = String.Empty;
            string releaseId = String.Empty;
            string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-objective-follow-through-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);

            try
            {
                await RunTest("Objectives_CreateListReadUpdateDeleteAndHistoryFilter", async () =>
                {
                    HttpResponseMessage createResponse = await _AuthClient.PostAsync("/api/v1/objectives",
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "Objective REST Coverage",
                            Description = "Track internal-first scoped work.",
                            Status = ObjectiveStatusEnum.Scoped,
                            Owner = "qa",
                            Tags = new[] { "history", "objective" },
                            AcceptanceCriteria = new[] { "REST CRUD", "History linkage" }
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, createResponse.StatusCode);

                    Objective created = await JsonHelper.DeserializeAsync<Objective>(createResponse).ConfigureAwait(false);
                    objectiveId = created.Id;
                    AssertStartsWith("obj_", objectiveId);
                    AssertEqual(ObjectiveStatusEnum.Scoped, created.Status);

                    HttpResponseMessage getResponse = await _AuthClient.GetAsync("/api/v1/objectives/" + objectiveId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, getResponse.StatusCode);
                    Objective loaded = await JsonHelper.DeserializeAsync<Objective>(getResponse).ConfigureAwait(false);
                    AssertEqual(objectiveId, loaded.Id);

                    HttpResponseMessage listResponse = await _AuthClient.GetAsync("/api/v1/objectives?pageSize=100").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, listResponse.StatusCode);
                    EnumerationResult<Objective> objectives = await JsonHelper.DeserializeAsync<EnumerationResult<Objective>>(listResponse).ConfigureAwait(false);
                    AssertTrue(objectives.Objects.Exists(current => current.Id == objectiveId), "Expected objective in list.");

                    HttpResponseMessage historyResponse = await _AuthClient.GetAsync(
                        "/api/v1/history?objectiveId=" + Uri.EscapeDataString(objectiveId) + "&pageSize=100").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, historyResponse.StatusCode);
                    EnumerationResult<HistoricalTimelineEntry> history = await JsonHelper.DeserializeAsync<EnumerationResult<HistoricalTimelineEntry>>(historyResponse).ConfigureAwait(false);
                    AssertTrue(history.Objects.Exists(entry => entry.SourceType == "Objective" && entry.SourceId == objectiveId), "Expected objective timeline entry.");

                    HttpResponseMessage updateResponse = await _AuthClient.PutAsync("/api/v1/objectives/" + objectiveId,
                        JsonHelper.ToJsonContent(new
                        {
                            Status = ObjectiveStatusEnum.Completed,
                            EvidenceLinks = new[] { "https://example.test/objective/rest" }
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, updateResponse.StatusCode);
                    Objective updated = await JsonHelper.DeserializeAsync<Objective>(updateResponse).ConfigureAwait(false);
                    AssertEqual(ObjectiveStatusEnum.Completed, updated.Status);
                    AssertTrue(updated.CompletedUtc.HasValue, "Expected completion timestamp.");

                    HttpResponseMessage deleteResponse = await _AuthClient.DeleteAsync("/api/v1/objectives/" + objectiveId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);
                    objectiveId = String.Empty;

                    HttpResponseMessage deletedResponse = await _AuthClient.GetAsync("/api/v1/objectives/" + updated.Id).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NotFound, deletedResponse.StatusCode);
                }).ConfigureAwait(false);

                await RunTest("Objectives_CreateWithoutAuthReturns401", async () =>
                {
                    HttpResponseMessage response = await _UnauthClient.PostAsync("/api/v1/objectives",
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "Unauthorized Objective"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                }).ConfigureAwait(false);

                await RunTest("Objectives_VoyageAndReleaseCreationLinkBackToObjective", async () =>
                {
                    HttpResponseMessage vesselResponse = await _AuthClient.PostAsync("/api/v1/vessels",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Objective Follow Through Vessel",
                            RepoUrl = "file:///tmp/objective-follow-through.git",
                            LocalPath = workingDirectory,
                            WorkingDirectory = workingDirectory,
                            DefaultBranch = "main"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, vesselResponse.StatusCode);
                    Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(vesselResponse).ConfigureAwait(false);
                    vesselId = vessel.Id;

                    HttpResponseMessage createObjectiveResponse = await _AuthClient.PostAsync("/api/v1/objectives",
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "Objective Follow Through",
                            Status = ObjectiveStatusEnum.Scoped,
                            VesselIds = new[] { vesselId }
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, createObjectiveResponse.StatusCode);
                    Objective createdObjective = await JsonHelper.DeserializeAsync<Objective>(createObjectiveResponse).ConfigureAwait(false);
                    objectiveId = createdObjective.Id;

                    HttpResponseMessage createVoyageResponse = await _AuthClient.PostAsync("/api/v1/voyages",
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "Objective Voyage",
                            Description = "Objective follow-through voyage",
                            VesselId = vesselId,
                            ObjectiveId = objectiveId,
                            Missions = new[]
                            {
                                new
                                {
                                    Title = "Objective Mission",
                                    Description = "Implement follow-through"
                                }
                            }
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, createVoyageResponse.StatusCode);
                    Voyage voyage = await JsonHelper.DeserializeAsync<Voyage>(createVoyageResponse).ConfigureAwait(false);
                    voyageId = voyage.Id;

                    HttpResponseMessage createReleaseResponse = await _AuthClient.PostAsync("/api/v1/releases",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            Title = "Objective Draft Release",
                            ObjectiveIds = new[] { objectiveId }
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, createReleaseResponse.StatusCode);
                    Release release = await JsonHelper.DeserializeAsync<Release>(createReleaseResponse).ConfigureAwait(false);
                    releaseId = release.Id;

                    HttpResponseMessage objectiveResponse = await _AuthClient.GetAsync("/api/v1/objectives/" + objectiveId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, objectiveResponse.StatusCode);
                    Objective linkedObjective = await JsonHelper.DeserializeAsync<Objective>(objectiveResponse).ConfigureAwait(false);
                    AssertTrue(linkedObjective.VoyageIds.Contains(voyageId), "Expected voyage link on objective.");
                    AssertTrue(linkedObjective.ReleaseIds.Contains(releaseId), "Expected release link on objective.");
                    AssertEqual(ObjectiveStatusEnum.Released, linkedObjective.Status);
                }).ConfigureAwait(false);
            }
            finally
            {
                if (!String.IsNullOrWhiteSpace(objectiveId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/objectives/" + objectiveId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(releaseId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/releases/" + releaseId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(voyageId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/voyages/" + voyageId).ConfigureAwait(false); } catch { }
                    try { await _AuthClient.DeleteAsync("/api/v1/voyages/" + voyageId + "/purge").ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(vesselId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/vessels/" + vesselId).ConfigureAwait(false); } catch { }
                }
                try
                {
                    if (Directory.Exists(workingDirectory))
                        Directory.Delete(workingDirectory, true);
                }
                catch
                {
                }
            }
        }
    }
}
