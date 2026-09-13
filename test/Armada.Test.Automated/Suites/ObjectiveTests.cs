namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
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
            string captainId = String.Empty;
            string refinementSessionId = String.Empty;
            string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-objective-follow-through-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);
            await TestGit.InitializeAsync(workingDirectory).ConfigureAwait(false);

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
                    await AssertStatusCodeAsync(HttpStatusCode.Created, createResponse).ConfigureAwait(false);

                    Objective created = await JsonHelper.DeserializeAsync<Objective>(createResponse).ConfigureAwait(false);
                    objectiveId = created.Id;
                    AssertStartsWith("obj_", objectiveId);
                    AssertEqual(ObjectiveStatusEnum.Scoped, created.Status);

                    HttpResponseMessage getResponse = await _AuthClient.GetAsync("/api/v1/objectives/" + objectiveId).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, getResponse).ConfigureAwait(false);
                    Objective loaded = await JsonHelper.DeserializeAsync<Objective>(getResponse).ConfigureAwait(false);
                    AssertEqual(objectiveId, loaded.Id);

                    HttpResponseMessage listResponse = await _AuthClient.GetAsync("/api/v1/objectives?pageSize=100").ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, listResponse).ConfigureAwait(false);
                    EnumerationResult<Objective> objectives = await JsonHelper.DeserializeAsync<EnumerationResult<Objective>>(listResponse).ConfigureAwait(false);
                    AssertTrue(objectives.Objects.Exists(current => current.Id == objectiveId), "Expected objective in list.");

                    HttpResponseMessage historyResponse = await _AuthClient.GetAsync(
                        "/api/v1/history?objectiveId=" + Uri.EscapeDataString(objectiveId) + "&pageSize=100").ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, historyResponse).ConfigureAwait(false);
                    EnumerationResult<HistoricalTimelineEntry> history = await JsonHelper.DeserializeAsync<EnumerationResult<HistoricalTimelineEntry>>(historyResponse).ConfigureAwait(false);
                    AssertTrue(history.Objects.Exists(entry => entry.SourceType == "Objective" && entry.SourceId == objectiveId), "Expected objective timeline entry.");

                    HttpResponseMessage updateResponse = await _AuthClient.PutAsync("/api/v1/objectives/" + objectiveId,
                        JsonHelper.ToJsonContent(new
                        {
                            Status = ObjectiveStatusEnum.Completed,
                            EvidenceLinks = new[] { "https://example.test/objective/rest" }
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, updateResponse).ConfigureAwait(false);
                    Objective updated = await JsonHelper.DeserializeAsync<Objective>(updateResponse).ConfigureAwait(false);
                    AssertEqual(ObjectiveStatusEnum.Completed, updated.Status);
                    AssertTrue(updated.CompletedUtc.HasValue, "Expected completion timestamp.");

                    HttpResponseMessage deleteResponse = await _AuthClient.DeleteAsync("/api/v1/objectives/" + objectiveId).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.NoContent, deleteResponse).ConfigureAwait(false);
                    objectiveId = String.Empty;

                    HttpResponseMessage deletedResponse = await _AuthClient.GetAsync("/api/v1/objectives/" + updated.Id).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.NotFound, deletedResponse).ConfigureAwait(false);
                }).ConfigureAwait(false);

                await RunTest("Objectives_CreateWithoutAuthReturns401", async () =>
                {
                    HttpResponseMessage response = await _UnauthClient.PostAsync("/api/v1/objectives",
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "Unauthorized Objective"
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.Unauthorized, response).ConfigureAwait(false);
                }).ConfigureAwait(false);

                await RunTest("BacklogAlias_CreateReadReorderAndDelete", async () =>
                {
                    HttpResponseMessage createResponse = await _AuthClient.PostAsync("/api/v1/backlog",
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "Backlog REST Coverage",
                            Description = "Exercise the backlog alias surface.",
                            Status = ObjectiveStatusEnum.Scoped,
                            Kind = ObjectiveKindEnum.Feature,
                            Priority = ObjectivePriorityEnum.P1,
                            Rank = 25,
                            BacklogState = ObjectiveBacklogStateEnum.Inbox,
                            Effort = ObjectiveEffortEnum.M,
                            TargetVersion = "0.8.0"
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.Created, createResponse).ConfigureAwait(false);

                    Objective created = await JsonHelper.DeserializeAsync<Objective>(createResponse).ConfigureAwait(false);
                    objectiveId = created.Id;
                    AssertStartsWith("obj_", objectiveId);
                    AssertEqual(ObjectiveKindEnum.Feature, created.Kind);
                    AssertEqual(ObjectivePriorityEnum.P1, created.Priority);
                    AssertEqual(25, created.Rank);
                    AssertEqual(ObjectiveBacklogStateEnum.Inbox, created.BacklogState);

                    HttpResponseMessage getBacklogResponse = await _AuthClient.GetAsync("/api/v1/backlog/" + objectiveId).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, getBacklogResponse).ConfigureAwait(false);
                    Objective loadedBacklog = await JsonHelper.DeserializeAsync<Objective>(getBacklogResponse).ConfigureAwait(false);
                    AssertEqual(objectiveId, loadedBacklog.Id);
                    AssertEqual("0.8.0", loadedBacklog.TargetVersion);

                    HttpResponseMessage objectiveCompatibilityResponse = await _AuthClient.GetAsync("/api/v1/objectives/" + objectiveId).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, objectiveCompatibilityResponse).ConfigureAwait(false);
                    Objective compatibilityLoad = await JsonHelper.DeserializeAsync<Objective>(objectiveCompatibilityResponse).ConfigureAwait(false);
                    AssertEqual(objectiveId, compatibilityLoad.Id);

                    HttpResponseMessage listResponse = await _AuthClient.GetAsync("/api/v1/backlog?pageSize=100").ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, listResponse).ConfigureAwait(false);
                    EnumerationResult<Objective> listed = await JsonHelper.DeserializeAsync<EnumerationResult<Objective>>(listResponse).ConfigureAwait(false);
                    AssertTrue(listed.Objects.Exists(current => current.Id == objectiveId), "Expected backlog item in backlog list.");

                    HttpResponseMessage sessionsResponse = await _AuthClient.GetAsync("/api/v1/backlog/" + objectiveId + "/refinement-sessions").ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, sessionsResponse).ConfigureAwait(false);
                    List<ObjectiveRefinementSession> sessions = await JsonHelper.DeserializeAsync<List<ObjectiveRefinementSession>>(sessionsResponse).ConfigureAwait(false);
                    AssertEqual(0, sessions.Count);

                    HttpResponseMessage objectivePreviewResponse = await _AuthClient.GetAsync(
                        "/api/v1/objectives/" + objectiveId + "/dispatch-preview").ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, objectivePreviewResponse).ConfigureAwait(false);
                    ObjectiveDispatchPreview objectivePreview = await JsonHelper.DeserializeAsync<ObjectiveDispatchPreview>(objectivePreviewResponse).ConfigureAwait(false);

                    HttpResponseMessage backlogPreviewResponse = await _AuthClient.GetAsync(
                        "/api/v1/backlog/" + objectiveId + "/dispatch-preview").ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, backlogPreviewResponse).ConfigureAwait(false);
                    ObjectiveDispatchPreview backlogPreview = await JsonHelper.DeserializeAsync<ObjectiveDispatchPreview>(backlogPreviewResponse).ConfigureAwait(false);
                    AssertEqual(objectiveId, objectivePreview.ObjectiveId);
                    AssertEqual(objectivePreview.ObjectiveId, backlogPreview.ObjectiveId);
                    AssertEqual(objectivePreview.IsReady, backlogPreview.IsReady);
                    AssertTrue(objectivePreview.Issues.Exists(issue => issue.Code == "brief_method_missing"), "Expected the preview to report the missing method.");
                    AssertTrue(objectivePreview.Issues.Exists(issue => issue.Code == "target_vessel_count"), "Expected the preview to report the missing target vessel.");

                    HttpResponseMessage reorderResponse = await _AuthClient.PostAsync("/api/v1/backlog/reorder",
                        JsonHelper.ToJsonContent(new
                        {
                            Items = new[]
                            {
                                new
                                {
                                    ObjectiveId = objectiveId,
                                    Rank = 5
                                }
                            }
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, reorderResponse).ConfigureAwait(false);
                    List<Objective> reordered = await JsonHelper.DeserializeAsync<List<Objective>>(reorderResponse).ConfigureAwait(false);
                    AssertEqual(1, reordered.Count);
                    AssertEqual(5, reordered[0].Rank);

                    HttpResponseMessage deleteResponse = await _AuthClient.DeleteAsync("/api/v1/backlog/" + objectiveId).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.NoContent, deleteResponse).ConfigureAwait(false);
                    objectiveId = String.Empty;
                }).ConfigureAwait(false);

                await RunTest("BacklogRefinementRoutes_RecordRuntimeFailureSummarizeApplyAndDelete", async () =>
                {
                    HttpResponseMessage createResponse = await _AuthClient.PostAsync("/api/v1/backlog",
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "Backlog refinement REST coverage",
                            Description = "Exercise captain-backed backlog refinement routes.",
                            Status = ObjectiveStatusEnum.Draft,
                            Kind = ObjectiveKindEnum.Feature,
                            BacklogState = ObjectiveBacklogStateEnum.Inbox
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.Created, createResponse).ConfigureAwait(false);
                    Objective created = await JsonHelper.DeserializeAsync<Objective>(createResponse).ConfigureAwait(false);
                    objectiveId = created.Id;

                    Captain captain = await CreateCaptainAsync("objective-refinement-rest").ConfigureAwait(false);
                    captainId = captain.Id;

                    HttpResponseMessage createSessionResponse = await _AuthClient.PostAsync(
                        "/api/v1/backlog/" + objectiveId + "/refinement-sessions",
                        JsonHelper.ToJsonContent(new
                        {
                            CaptainId = captainId,
                            Title = "Backlog refinement REST coverage session"
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.Created, createSessionResponse).ConfigureAwait(false);
                    ObjectiveRefinementSessionDetail createdSession = await JsonHelper.DeserializeAsync<ObjectiveRefinementSessionDetail>(createSessionResponse).ConfigureAwait(false);
                    refinementSessionId = createdSession.Session.Id;
                    AssertEqual(objectiveId, createdSession.Session.ObjectiveId);
                    AssertEqual(captainId, createdSession.Session.CaptainId);
                    AssertEqual(0, createdSession.Messages.Count);

                    await SetCaptainRuntimeAsync(captainId, "Custom").ConfigureAwait(false);

                    HttpResponseMessage sendResponse = await _AuthClient.PostAsync(
                        "/api/v1/objective-refinement-sessions/" + refinementSessionId + "/messages",
                        JsonHelper.ToJsonContent(new
                        {
                            Content = "Clarify rollout constraints and acceptance criteria for the backlog item."
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, sendResponse).ConfigureAwait(false);

                    ObjectiveRefinementSessionDetail activeDetail = await WaitForSessionDetailAsync(
                        refinementSessionId,
                        detail => detail.Session.Status == ObjectiveRefinementSessionStatusEnum.Active
                            && detail.Messages.Exists(message => message.Role == "Assistant" && !String.IsNullOrWhiteSpace(message.Content))).ConfigureAwait(false);

                    ObjectiveRefinementMessage assistantMessage = activeDetail.Messages.Find(message => message.Role == "Assistant")
                        ?? throw new Exception("Expected assistant refinement transcript message");
                    AssertEqual(2, activeDetail.Messages.Count);
                    AssertContains("Refinement response failed", assistantMessage.Content);

                    HttpResponseMessage summarizeResponse = await _AuthClient.PostAsync(
                        "/api/v1/objective-refinement-sessions/" + refinementSessionId + "/summarize",
                        JsonHelper.ToJsonContent(new
                        {
                            MessageId = assistantMessage.Id
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, summarizeResponse).ConfigureAwait(false);
                    ObjectiveRefinementSummaryResponse summary = await JsonHelper.DeserializeAsync<ObjectiveRefinementSummaryResponse>(summarizeResponse).ConfigureAwait(false);
                    AssertEqual("assistant-fallback", summary.Method);
                    AssertEqual(refinementSessionId, summary.SessionId);
                    AssertEqual(assistantMessage.Id, summary.MessageId);

                    HttpResponseMessage applyResponse = await _AuthClient.PostAsync(
                        "/api/v1/objective-refinement-sessions/" + refinementSessionId + "/apply",
                        JsonHelper.ToJsonContent(new
                        {
                            MessageId = assistantMessage.Id,
                            MarkMessageSelected = true,
                            PromoteBacklogState = true
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, applyResponse).ConfigureAwait(false);
                    ObjectiveRefinementApplyResponse applied = await JsonHelper.DeserializeAsync<ObjectiveRefinementApplyResponse>(applyResponse).ConfigureAwait(false);
                    AssertEqual(ObjectiveStatusEnum.Scoped, applied.Objective.Status);
                    AssertEqual(ObjectiveBacklogStateEnum.Triaged, applied.Objective.BacklogState);
                    AssertTrue(applied.Objective.RefinementSessionIds.Contains(refinementSessionId), "Expected refinement session link after apply.");
                    AssertContains("Refinement response failed", applied.Objective.RefinementSummary ?? String.Empty);

                    ObjectiveRefinementSessionDetail selectedDetail = await WaitForSessionDetailAsync(
                        refinementSessionId,
                        detail => detail.Messages.Exists(message => message.Id == assistantMessage.Id && message.IsSelected)).ConfigureAwait(false);
                    AssertTrue(selectedDetail.Messages.Exists(message => message.Id == assistantMessage.Id && message.IsSelected), "Expected selected refinement message.");

                    HttpResponseMessage deleteSessionResponse = await _AuthClient.DeleteAsync("/api/v1/objective-refinement-sessions/" + refinementSessionId).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.NoContent, deleteSessionResponse).ConfigureAwait(false);
                    refinementSessionId = String.Empty;

                    HttpResponseMessage deletedSessionResponse = await _AuthClient.GetAsync("/api/v1/objective-refinement-sessions/" + createdSession.Session.Id).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.NotFound, deletedSessionResponse).ConfigureAwait(false);

                    HttpResponseMessage objectiveResponse = await _AuthClient.GetAsync("/api/v1/backlog/" + objectiveId).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, objectiveResponse).ConfigureAwait(false);
                    Objective updatedObjective = await JsonHelper.DeserializeAsync<Objective>(objectiveResponse).ConfigureAwait(false);
                    AssertFalse(updatedObjective.RefinementSessionIds.Contains(createdSession.Session.Id), "Expected refinement session unlink after delete.");

                    HttpResponseMessage deleteObjectiveResponse = await _AuthClient.DeleteAsync("/api/v1/backlog/" + objectiveId).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.NoContent, deleteObjectiveResponse).ConfigureAwait(false);
                    objectiveId = String.Empty;

                    HttpResponseMessage deleteCaptainResponse = await _AuthClient.DeleteAsync("/api/v1/captains/" + captainId).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.NoContent, deleteCaptainResponse).ConfigureAwait(false);
                    captainId = String.Empty;
                }).ConfigureAwait(false);

                await RunTest("Objectives_VoyageAndReleaseCreationLinkBackToObjective", async () =>
                {
                    HttpResponseMessage vesselResponse = await _AuthClient.PostAsync("/api/v1/vessels",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Objective Follow Through Vessel",
                            RepoUrl = new Uri(workingDirectory + Path.DirectorySeparatorChar).AbsoluteUri,
                            LocalPath = workingDirectory,
                            WorkingDirectory = workingDirectory,
                            DefaultBranch = "main"
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.Created, vesselResponse).ConfigureAwait(false);
                    Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(vesselResponse).ConfigureAwait(false);
                    vesselId = vessel.Id;

                    HttpResponseMessage profileResponse = await _AuthClient.PostAsync("/api/v1/workflow-profiles",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Objective linkage checks", Scope = "Vessel", VesselId = vesselId,
                            IsDefault = true, BuildCommand = "echo fixture", UnitTestCommand = "echo fixture"
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.Created, profileResponse).ConfigureAwait(false);

                    HttpResponseMessage createObjectiveResponse = await _AuthClient.PostAsync("/api/v1/objectives",
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "Objective Follow Through",
                            Description = "Verify local objective, voyage and release linkage.",
                            AcceptanceCriteria = new[] { "Linked voyage and release IDs are retained." },
                            RefinementSummary = "Create isolated local records and read their links through the API.",
                            AutoDispatchEnabled = false,
                            Status = ObjectiveStatusEnum.Scoped,
                            VesselIds = new[] { vesselId }
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.Created, createObjectiveResponse).ConfigureAwait(false);
                    Objective createdObjective = await JsonHelper.DeserializeAsync<Objective>(createObjectiveResponse).ConfigureAwait(false);
                    objectiveId = createdObjective.Id;

                    HttpResponseMessage fixtureCaptainResponse = await _AuthClient.PostAsync("/api/v1/captains",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Linkage fixture captain", Runtime = "Custom", Model = String.Empty,
                            State = "Working", AllowedPersonas = "[\"Worker\"]"
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.Created, fixtureCaptainResponse).ConfigureAwait(false);
                    Captain fixtureCaptain = await JsonHelper.DeserializeAsync<Captain>(fixtureCaptainResponse).ConfigureAwait(false);
                    captainId = fixtureCaptain.Id;

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
                    await AssertStatusCodeAsync(HttpStatusCode.Created, createVoyageResponse).ConfigureAwait(false);
                    Voyage voyage = await JsonHelper.DeserializeAsync<Voyage>(createVoyageResponse).ConfigureAwait(false);
                    voyageId = voyage.Id;

                    HttpResponseMessage createReleaseResponse = await _AuthClient.PostAsync("/api/v1/releases",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            Title = "Objective Draft Release",
                            ObjectiveIds = new[] { objectiveId }
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.Created, createReleaseResponse).ConfigureAwait(false);
                    Release release = await JsonHelper.DeserializeAsync<Release>(createReleaseResponse).ConfigureAwait(false);
                    releaseId = release.Id;

                    HttpResponseMessage objectiveResponse = await _AuthClient.GetAsync("/api/v1/objectives/" + objectiveId).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, objectiveResponse).ConfigureAwait(false);
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
                if (!String.IsNullOrWhiteSpace(refinementSessionId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/objective-refinement-sessions/" + refinementSessionId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(voyageId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/voyages/" + voyageId).ConfigureAwait(false); } catch { }
                    try { await _AuthClient.DeleteAsync("/api/v1/voyages/" + voyageId + "/purge").ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(captainId))
                {
                    HttpResponseMessage stopResponse = await _AuthClient.PostAsync("/api/v1/captains/" + captainId + "/stop", null).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, stopResponse).ConfigureAwait(false);
                    HttpResponseMessage deleteResponse = await _AuthClient.DeleteAsync("/api/v1/captains/" + captainId).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.NoContent, deleteResponse).ConfigureAwait(false);
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

        private async Task<Captain> CreateCaptainAsync(string prefix, string runtime = "ClaudeCode")
        {
            HttpResponseMessage response = await _AuthClient.PostAsync(
                "/api/v1/captains",
                JsonHelper.ToJsonContent(new
                {
                    Name = prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    Runtime = runtime,
                    Model = String.Empty
                })).ConfigureAwait(false);
            await AssertStatusCodeAsync(HttpStatusCode.Created, response).ConfigureAwait(false);
            return await JsonHelper.DeserializeAsync<Captain>(response).ConfigureAwait(false);
        }

        private async Task SetCaptainRuntimeAsync(string captainId, string runtime)
        {
            HttpResponseMessage getResponse = await _AuthClient.GetAsync("/api/v1/captains/" + captainId).ConfigureAwait(false);
            await AssertStatusCodeAsync(HttpStatusCode.OK, getResponse).ConfigureAwait(false);
            Captain captain = await JsonHelper.DeserializeAsync<Captain>(getResponse).ConfigureAwait(false);

            HttpResponseMessage updateResponse = await _AuthClient.PutAsync(
                "/api/v1/captains/" + captainId,
                JsonHelper.ToJsonContent(new
                {
                    Name = captain.Name,
                    Runtime = runtime,
                    Model = String.Empty
                })).ConfigureAwait(false);
            await AssertStatusCodeAsync(HttpStatusCode.OK, updateResponse).ConfigureAwait(false);
        }

        private async Task<ObjectiveRefinementSessionDetail> WaitForSessionDetailAsync(
            string sessionId,
            Func<ObjectiveRefinementSessionDetail, bool> predicate,
            int timeoutMs = 15000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                HttpResponseMessage response = await _AuthClient.GetAsync("/api/v1/objective-refinement-sessions/" + sessionId).ConfigureAwait(false);
                await AssertStatusCodeAsync(HttpStatusCode.OK, response).ConfigureAwait(false);
                ObjectiveRefinementSessionDetail detail = await JsonHelper.DeserializeAsync<ObjectiveRefinementSessionDetail>(response).ConfigureAwait(false);
                if (predicate(detail))
                    return detail;

                await Task.Delay(100).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for refinement session " + sessionId);
        }
    }
}
