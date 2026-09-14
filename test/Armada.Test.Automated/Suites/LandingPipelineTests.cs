namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.WebSockets;
    using System.Reflection;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Server;
    using Armada.Test.Common;

    /// <summary>
    /// Automated tests for the landing pipeline exercised through the REST API
    /// against a real running ArmadaServer. These complement the unit-level
    /// LandingPipelineTests by verifying the actual orchestration paths in
    /// ArmadaServer.HandleMissionCompleteAsync and related methods.
    /// </summary>
    public class LandingPipelineTests : TestSuite
    {
        #region Public-Members

        public override string Name => "Landing Pipeline (Automated)";

        #endregion

        #region Private-Members

        private HttpClient _AuthClient;
        private HttpClient _UnauthClient;
        private ArmadaServer _Server;
        private HttpClient _McpClient;
        private int _RestPort;
        private string _ApiKey;

        #endregion

        #region Constructors-and-Factories

        public LandingPipelineTests(HttpClient authClient, HttpClient unauthClient, ArmadaServer server, HttpClient mcpClient, int restPort, string apiKey)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
            _Server = server ?? throw new ArgumentNullException(nameof(server));
            _McpClient = mcpClient ?? throw new ArgumentNullException(nameof(mcpClient));
            _RestPort = restPort;
            _ApiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        }

        #endregion

        #region Public-Methods

        protected override async Task RunTestsAsync()
        {
            // === PullRequestOpen Status Transitions ===

            await RunTest("PullRequestOpen_TransitionsFromWorkProduced", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("PR transition test", "WorkProduced");

                // Transition to PullRequestOpen
                HttpResponseMessage resp = await TransitionAsync(missionId, "PullRequestOpen");
                AssertStatusCode(HttpStatusCode.OK, resp);

                Mission mission = await GetMissionAsync(missionId);
                AssertEqual("PullRequestOpen", mission.Status.ToString());
            });

            await RunTest("PullRequestOpen_RejectsCompleteWithoutLandingProof", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("PR to Complete", "WorkProduced");
                await TransitionAsync(missionId, "PullRequestOpen");

                HttpResponseMessage resp = await TransitionAsync(missionId, "Complete");
                AssertStatusCode(HttpStatusCode.Conflict, resp);

                Mission mission = await GetMissionAsync(missionId);
                AssertEqual("PullRequestOpen", mission.Status.ToString());
            });

            await RunTest("PullRequestOpen_TransitionsToLandingFailed", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("PR to LandingFailed", "WorkProduced");
                await TransitionAsync(missionId, "PullRequestOpen");

                HttpResponseMessage resp = await TransitionAsync(missionId, "LandingFailed");
                AssertStatusCode(HttpStatusCode.OK, resp);

                Mission mission = await GetMissionAsync(missionId);
                AssertEqual("LandingFailed", mission.Status.ToString());
            });

            await RunTest("PullRequestOpen_TransitionsToCancelled", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("PR to Cancelled", "WorkProduced");
                await TransitionAsync(missionId, "PullRequestOpen");

                HttpResponseMessage resp = await TransitionAsync(missionId, "Cancelled");
                AssertStatusCode(HttpStatusCode.OK, resp);

                Mission mission = await GetMissionAsync(missionId);
                AssertEqual("Cancelled", mission.Status.ToString());
            });

            await RunTest("PullRequestOpen_RejectsInvalidTransitionToInProgress", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("PR invalid transition", "WorkProduced");
                await TransitionAsync(missionId, "PullRequestOpen");

                HttpResponseMessage resp = await TransitionAsync(missionId, "InProgress");
                string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                ArmadaErrorResponse errorResp = JsonHelper.Deserialize<ArmadaErrorResponse>(respBody);

                // Server returns 200 with an Error property in the body for invalid transitions
                AssertTrue(errorResp.Error != null || (errorResp.Message != null && errorResp.Message.Contains("Invalid transition")),
                    "Expected error response for invalid transition PullRequestOpen -> InProgress");

                // Mission should still be PullRequestOpen
                Mission mission = await GetMissionAsync(missionId);
                AssertEqual("PullRequestOpen", mission.Status.ToString());
            });

            // === Manual Complete Without Dock ===

            await RunTest("ManualComplete_NoDock_RejectsUnlandedCode", async () =>
            {
                // Create a mission and advance to WorkProduced (no dock since no vessel assignment)
                string missionId = await CreateAndAdvanceMissionAsync("Manual complete audit", "WorkProduced");

                // Manually transition to Complete (no dock exists for this code mission)
                HttpResponseMessage resp = await TransitionAsync(missionId, "Complete");
                AssertStatusCode(HttpStatusCode.Conflict, resp);

                Mission mission = await GetMissionAsync(missionId);
                AssertEqual("WorkProduced", mission.Status.ToString());
            });

            await RunTest("ManualComplete_ReviewStillRequiresApproval", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("Manual review completion", "InProgress");
                HttpResponseMessage testing = await TransitionAsync(missionId, "Testing");
                AssertStatusCode(HttpStatusCode.OK, testing);
                HttpResponseMessage review = await TransitionAsync(missionId, "Review");
                AssertStatusCode(HttpStatusCode.OK, review);

                HttpResponseMessage complete = await TransitionAsync(missionId, "Complete");
                AssertStatusCode(HttpStatusCode.Conflict, complete);

                Mission mission = await GetMissionAsync(missionId);
                AssertEqual("Review", mission.Status.ToString());
            });

            await RunTest("ManualComplete_FailedCheckIsBlockedAtRest", async () =>
            {
                Mission mission = await CreateMissionAsync("Manual failed check completion");
                string missionId = mission.Id!;
                await TransitionAsync(missionId, "Assigned");
                await TransitionAsync(missionId, "InProgress");
                await TransitionAsync(missionId, "WorkProduced");
                Vessel checkVessel = await CreateVesselWithLandingModeAsync("Manual-check-vessel", "MergeQueue");
                CheckRun blockingCheck = await ImportCheckAsync(checkVessel.Id!, missionId, "Failed", "isolated-failed-check");
                for (int index = 0; index < 101; index++)
                {
                    await ImportCheckAsync(checkVessel.Id!, missionId, "Passed", "isolated-check-" + index);
                }
                await AssertCheckIsOnSecondPageAsync(missionId, blockingCheck.Id);

                HttpResponseMessage complete = await TransitionAsync(missionId, "Complete");
                AssertStatusCode(HttpStatusCode.Conflict, complete);
                Mission persisted = await GetMissionAsync(missionId);
                AssertEqual("WorkProduced", persisted.Status.ToString());
            });

            await RunTest("ManualComplete_PendingCheckIsBlockedAtRest", async () =>
            {
                Mission mission = await CreateMissionAsync("Manual pending check completion");
                string missionId = mission.Id!;
                await TransitionAsync(missionId, "Assigned");
                await TransitionAsync(missionId, "InProgress");
                await TransitionAsync(missionId, "WorkProduced");
                Vessel checkVessel = await CreateVesselWithLandingModeAsync("Manual-pending-vessel", "MergeQueue");
                CheckRun blockingCheck = await ImportCheckAsync(checkVessel.Id!, missionId, "Pending", "isolated-pending-check");
                for (int index = 0; index < 101; index++)
                {
                    await ImportCheckAsync(checkVessel.Id!, missionId, "Passed", "isolated-check-" + index);
                }
                await AssertCheckIsOnSecondPageAsync(missionId, blockingCheck.Id);

                HttpResponseMessage complete = await TransitionAsync(missionId, "Complete");
                AssertStatusCode(HttpStatusCode.Conflict, complete);
                Mission persisted = await GetMissionAsync(missionId);
                AssertEqual("WorkProduced", persisted.Status.ToString());
            });

            await RunTest("ManualComplete_ActiveProcessReturnsConflictWithoutMutationAtRest", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N");
                string captainId = "cpt_manual_active_" + suffix;
                string missionId = "msn_manual_active_" + suffix;
                int processId = Process.GetCurrentProcess().Id;

                HttpResponseMessage captainResponse = await _AuthClient.PostAsync("/api/v1/captains",
                    JsonHelper.ToJsonContent(new
                    {
                        Id = captainId,
                        Name = "manual active captain " + suffix,
                        Runtime = "ClaudeCode",
                        State = "Working",
                        CurrentMissionId = missionId,
                        ProcessId = processId
                    })).ConfigureAwait(false);
                AssertStatusCode(HttpStatusCode.Created, captainResponse);

                HttpResponseMessage missionResponse = await _AuthClient.PostAsync("/api/v1/missions",
                    JsonHelper.ToJsonContent(new
                    {
                        Id = missionId,
                        Title = "manual active process mission " + suffix,
                        CaptainId = captainId,
                        ProcessId = processId,
                        Mode = "Implementation"
                    })).ConfigureAwait(false);
                AssertStatusCode(HttpStatusCode.Created, missionResponse);
                RegisterActiveProcessForRestProof(processId, captainId, missionId);
                try
                {
                    await TransitionAsync(missionId, "Assigned");
                    await TransitionAsync(missionId, "InProgress");
                    await TransitionAsync(missionId, "WorkProduced");
                    HttpResponseMessage complete = await TransitionAsync(missionId, "Complete");
                    AssertStatusCode(HttpStatusCode.Conflict, complete);
                    Mission persisted = await GetMissionAsync(missionId);
                    AssertEqual("WorkProduced", persisted.Status.ToString(),
                        "an active process must prevent status mutation");
                }
                finally
                {
                    UnregisterActiveProcessForRestProof(processId);
                }
            });

            await RunTest("ManualComplete_UnverifiableProcessReturnsConflictWithoutMutationAtRest", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N");
                string captainId = "cpt_manual_unknown_" + suffix;
                string missionId = "msn_manual_unknown_" + suffix;
                int processId = Process.GetCurrentProcess().Id;

                HttpResponseMessage captainResponse = await _AuthClient.PostAsync("/api/v1/captains",
                    JsonHelper.ToJsonContent(new
                    {
                        Id = captainId,
                        Name = "manual unverifiable captain " + suffix,
                        Runtime = "Custom",
                        State = "Working",
                        CurrentMissionId = missionId,
                        ProcessId = processId
                    })).ConfigureAwait(false);
                AssertStatusCode(HttpStatusCode.Created, captainResponse);
                HttpResponseMessage missionResponse = await _AuthClient.PostAsync("/api/v1/missions",
                    JsonHelper.ToJsonContent(new
                    {
                        Id = missionId,
                        Title = "manual unverifiable process mission " + suffix,
                        CaptainId = captainId,
                        ProcessId = processId,
                        Mode = "Implementation"
                    })).ConfigureAwait(false);
                AssertStatusCode(HttpStatusCode.Created, missionResponse);
                RegisterActiveProcessForRestProof(processId, captainId, missionId);
                try
                {
                    await TransitionAsync(missionId, "Assigned");
                    await TransitionAsync(missionId, "InProgress");
                    await TransitionAsync(missionId, "WorkProduced");
                    HttpResponseMessage complete = await TransitionAsync(missionId, "Complete");
                    AssertStatusCode(HttpStatusCode.Conflict, complete);
                    string body = await complete.Content.ReadAsStringAsync().ConfigureAwait(false);
                    AssertContains("manual_completion_process_liveness_unknown", body,
                        "unverifiable liveness has a stable fail-closed reason");
                    Mission persisted = await GetMissionAsync(missionId);
                    AssertEqual("WorkProduced", persisted.Status.ToString(),
                        "an unverifiable process must prevent status mutation");
                }
                finally
                {
                    UnregisterActiveProcessForRestProof(processId);
                }
            });

            await RunTest("ManualComplete_IntermediateStageUsesSharedHandoffAtRest", async () =>
            {
                string suffix = Guid.NewGuid().ToString("N");
                string captainId = "cpt_manual_" + suffix;
                string workerId = "msn_manual_worker_" + suffix;
                string judgeId = "msn_manual_judge_" + suffix;
                string voyageId = "voy_manual_" + suffix;
                string workerCommitHash = TestRepoHelper.GetLocalBareRepoHeadCommit();

                HttpResponseMessage voyageResponse = await _AuthClient.PostAsync("/api/v1/voyages",
                    JsonHelper.ToJsonContent(new
                    {
                        Id = voyageId,
                        Title = "manual handoff voyage " + suffix,
                        Missions = Array.Empty<object>()
                    })).ConfigureAwait(false);
                AssertStatusCode(HttpStatusCode.Created, voyageResponse);
                Voyage voyage = await JsonHelper.DeserializeAsync<Voyage>(voyageResponse).ConfigureAwait(false);
                voyageId = voyage.Id!;

                HttpResponseMessage captainResponse = await _AuthClient.PostAsync("/api/v1/captains",
                    JsonHelper.ToJsonContent(new
                    {
                        Id = captainId,
                        Name = "manual handoff captain " + suffix,
                        Runtime = "ClaudeCode",
                        State = "Working",
                        CurrentMissionId = workerId
                    })).ConfigureAwait(false);
                AssertStatusCode(HttpStatusCode.Created, captainResponse);

                HttpResponseMessage workerResponse = await _AuthClient.PostAsync("/api/v1/missions",
                    JsonHelper.ToJsonContent(new
                    {
                        Id = workerId,
                        Title = "manual intermediate worker " + suffix,
                        VoyageId = voyageId,
                        CaptainId = captainId,
                        BranchName = "main",
                        CommitHash = workerCommitHash,
                        Mode = "Implementation"
                    })).ConfigureAwait(false);
                string workerBody = await workerResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                AssertTrue(workerResponse.IsSuccessStatusCode,
                    "worker creation response " + (int)workerResponse.StatusCode + ": " + workerBody);
                HttpResponseMessage judgeResponse = await _AuthClient.PostAsync("/api/v1/missions",
                    JsonHelper.ToJsonContent(new
                    {
                        Id = judgeId,
                        Title = "manual downstream judge " + suffix,
                        VoyageId = voyageId,
                        DependsOnMissionId = workerId,
                        Persona = "Judge",
                        Mode = "Implementation"
                    })).ConfigureAwait(false);
                string judgeBody = await judgeResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                AssertTrue(judgeResponse.IsSuccessStatusCode,
                    "downstream creation response " + (int)judgeResponse.StatusCode + ": " + judgeBody);

                Vessel activeDockVessel = await CreateVesselWithLandingModeAsync("Manual-active-dock", "MergeQueue");
                await AttachActiveDockAsync(workerId, captainId, activeDockVessel);
                IAdmiralService admiral = ReadServerAdmiral();
                Func<Mission, Dock, Task>? originalCompletion = admiral.OnMissionComplete;
                int landingCallbackCalls = 0;
                admiral.OnMissionComplete = (_, _) =>
                {
                    landingCallbackCalls++;
                    return Task.CompletedTask;
                };

                Vessel armedVessel = await CreateVesselWithLandingModeAsync("Manual-armed-voyage-check", "MergeQueue");
                HttpResponseMessage armedCheckResponse = await _AuthClient.PostAsync(
                    "/api/v1/check-runs/import",
                    JsonHelper.ToJsonContent(new
                    {
                        VesselId = armedVessel.Id,
                        VoyageId = voyageId,
                        Type = "Build",
                        Status = "Pending",
                        ProviderName = "manual-test",
                        ExternalId = Guid.NewGuid().ToString("N"),
                        Command = "echo",
                        CommitHash = workerCommitHash,
                        Label = "Build (armed at dispatch)"
                    })).ConfigureAwait(false);
                AssertStatusCode(HttpStatusCode.Created, armedCheckResponse);

                try
                {
                    await TransitionAsync(workerId, "Assigned");
                    await TransitionAsync(workerId, "InProgress");
                    HttpResponseMessage complete = await TransitionAsync(workerId, "Complete");
                    AssertStatusCode(HttpStatusCode.OK, complete);
                    Mission persisted = await GetMissionAsync(workerId);
                    AssertEqual("WorkProduced", persisted.Status.ToString(), "intermediate handoff remains nonterminal");
                    AssertFalse(String.IsNullOrEmpty(persisted.DockId), "active dock is retained on the handoff record");
                    Mission downstream = await GetMissionAsync(judgeId);
                    AssertContains("<!-- ARMADA:HANDOFF:" + workerId + " -->", downstream.Description,
                        "shared handoff writes downstream evidence; status=" + downstream.Status
                        + ", assignment=" + downstream.AssignmentState + ", description=" + downstream.Description);
                    AssertEqual("main", downstream.BranchName,
                        "shared handoff propagates the produced branch");
                }
                finally
                {
                    admiral.OnMissionComplete = originalCompletion;
                }
                AssertEqual(0, landingCallbackCalls, "intermediate handoff must not invoke landing callback");

                EnumerationResult<ArmadaEvent> events = await GetTypedAsync<EnumerationResult<ArmadaEvent>>(
                    "/api/v1/events?type=mission.status_changed&missionId=" + workerId);
                AssertTrue(events.Objects.Any(evt => (evt.Message ?? String.Empty).Contains("WorkProduced", StringComparison.Ordinal)),
                    "REST status event reports the persisted handoff status");
                AssertFalse(events.Objects.Any(evt => (evt.Message ?? String.Empty).Contains("transitioned to Complete", StringComparison.Ordinal)),
                    "REST status event does not report requested Complete for an intermediate stage");
            });

            // === Manual Complete Across Entry Points ===

            await RunTest("ManualComplete_WebSocket_NoCommitRefusedWithReason", async () =>
            {
                string missionId = await CreateInProgressCompletionFixtureAsync("ws no-commit", CompletionFixture.NoCommit);
                CompletionDecision decision = await CompleteViaWebSocketAsync(missionId);
                AssertFalse(decision.Allowed, "WebSocket completion without a commit is refused: " + decision.Detail);
                AssertEqual("manual_completion_ancestry_unavailable", decision.Reason, decision.Detail);
                await AssertCompletionRefusedStateAsync(missionId, MissionStatusEnum.InProgress);
            });

            await RunTest("ManualComplete_Mcp_NoCommitRefusedWithReason", async () =>
            {
                string missionId = await CreateInProgressCompletionFixtureAsync("mcp no-commit", CompletionFixture.NoCommit);
                CompletionDecision decision = await CompleteViaMcpAsync(missionId);
                AssertFalse(decision.Allowed, "MCP completion without a commit is refused: " + decision.Detail);
                AssertEqual("manual_completion_ancestry_unavailable", decision.Reason, decision.Detail);
                await AssertCompletionRefusedStateAsync(missionId, MissionStatusEnum.InProgress);
            });

            await RunTest("ManualComplete_WebSocket_LandedCommitCompletes", async () =>
            {
                string missionId = await CreateInProgressCompletionFixtureAsync("ws landed", CompletionFixture.Landed);
                CompletionDecision decision = await CompleteViaWebSocketAsync(missionId);
                AssertTrue(decision.Allowed, "WebSocket completion with a landed commit succeeds: " + decision.Detail);
                Mission stored = await GetMissionAsync(missionId);
                AssertEqual(MissionStatusEnum.Complete, stored.Status);
                AssertNotNull(stored.CompletedUtc, "completion stamps CompletedUtc");
            });

            await RunTest("ManualComplete_Mcp_LandedCommitCompletes", async () =>
            {
                string missionId = await CreateInProgressCompletionFixtureAsync("mcp landed", CompletionFixture.Landed);
                CompletionDecision decision = await CompleteViaMcpAsync(missionId);
                AssertTrue(decision.Allowed, "MCP completion with a landed commit succeeds: " + decision.Detail);
                Mission stored = await GetMissionAsync(missionId);
                AssertEqual(MissionStatusEnum.Complete, stored.Status);
                AssertNotNull(stored.CompletedUtc, "completion stamps CompletedUtc");
            });

            await RunTest("ManualComplete_RestWebSocketAndMcpReachIdenticalDecisions", async () =>
            {
                // Each fixture is built three times, once per entry point, so every surface
                // decides the same stored state. The expected outcome is asserted as well, so
                // three surfaces agreeing on a wrong answer still fails.
                (CompletionFixture Fixture, bool Allowed, string? Reason, MissionStatusEnum Status)[] scenarios = new[]
                {
                    (CompletionFixture.NoCommit, false, (string?)"manual_completion_ancestry_unavailable", MissionStatusEnum.InProgress),
                    (CompletionFixture.LandedInReview, false, (string?)"manual_completion_review_required", MissionStatusEnum.Review),
                    (CompletionFixture.Landed, true, (string?)null, MissionStatusEnum.Complete)
                };

                foreach ((CompletionFixture fixture, bool allowed, string? reason, MissionStatusEnum status) in scenarios)
                {
                    string restId = await CreateInProgressCompletionFixtureAsync("rest " + fixture, fixture);
                    string wsId = await CreateInProgressCompletionFixtureAsync("ws " + fixture, fixture);
                    string mcpId = await CreateInProgressCompletionFixtureAsync("mcp " + fixture, fixture);

                    CompletionDecision rest = await CompleteViaRestAsync(restId);
                    CompletionDecision ws = await CompleteViaWebSocketAsync(wsId);
                    CompletionDecision mcp = await CompleteViaMcpAsync(mcpId);

                    foreach ((string surface, CompletionDecision decision, string id) in new[] { ("REST", rest, restId), ("WebSocket", ws, wsId), ("MCP", mcp, mcpId) })
                    {
                        AssertEqual(allowed, decision.Allowed, surface + " decision for " + fixture + ": " + decision.Detail);
                        AssertEqual(reason, decision.Reason, surface + " reason for " + fixture + ": " + decision.Detail);
                        Mission stored = await GetMissionAsync(id);
                        AssertEqual(status, stored.Status, surface + " stored status for " + fixture);
                    }
                }
            });

            // === MergeQueue Auto-Enqueue ===

            await RunTest("MergeQueue_VesselLandingMode_CreatesEntry", async () =>
            {
                // Create a vessel with LandingMode = MergeQueue
                Vessel vessel = await CreateVesselWithLandingModeAsync("MQ-Vessel", "MergeQueue");
                string vesselId = vessel.Id!;

                // Verify vessel LandingMode was persisted
                Vessel readVessel = await GetTypedAsync<Vessel>("/api/v1/vessels/" + vesselId);
                AssertTrue(readVessel.LandingMode != null, "Vessel should have LandingMode property");
                AssertEqual("MergeQueue", readVessel.LandingMode.ToString());
            });

            // === Event Emission Correctness ===

            await RunTest("StatusChanged_EventEmitted_ForEachTransition", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("Event emission test", "InProgress");

                // Transition to WorkProduced
                await TransitionAsync(missionId, "WorkProduced");

                // Check that a status_changed event was emitted
                EnumerationResult<ArmadaEvent> events = await GetTypedAsync<EnumerationResult<ArmadaEvent>>("/api/v1/events?type=mission.status_changed&missionId=" + missionId);
                bool found = false;
                foreach (ArmadaEvent evt in events.Objects ?? new List<ArmadaEvent>())
                {
                    string? msg = evt.Message;
                    if (msg != null && msg.Contains("WorkProduced"))
                    {
                        found = true;
                        break;
                    }
                }
                AssertTrue(found, "Expected mission.status_changed event mentioning WorkProduced for mission " + missionId);
            });

            await RunTest("LandingFailed_TransitionsBackToWorkProduced", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("LandingFailed retry", "WorkProduced");
                await TransitionAsync(missionId, "LandingFailed");

                // LandingFailed -> WorkProduced (retry)
                HttpResponseMessage resp = await TransitionAsync(missionId, "WorkProduced");
                AssertStatusCode(HttpStatusCode.OK, resp);

                Mission mission = await GetMissionAsync(missionId);
                AssertEqual("WorkProduced", mission.Status.ToString());
            });

            await RunTest("LandingFailed_TransitionsToFailed", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("LandingFailed to Failed", "WorkProduced");
                await TransitionAsync(missionId, "LandingFailed");

                HttpResponseMessage resp = await TransitionAsync(missionId, "Failed");
                AssertStatusCode(HttpStatusCode.OK, resp);

                Mission mission = await GetMissionAsync(missionId);
                AssertEqual("Failed", mission.Status.ToString());
            });

            await RunTest("LandingFailed_TransitionsToCancelled", async () =>
            {
                string missionId = await CreateAndAdvanceMissionAsync("LandingFailed to Cancelled", "WorkProduced");
                await TransitionAsync(missionId, "LandingFailed");

                HttpResponseMessage resp = await TransitionAsync(missionId, "Cancelled");
                AssertStatusCode(HttpStatusCode.OK, resp);

                Mission mission = await GetMissionAsync(missionId);
                AssertEqual("Cancelled", mission.Status.ToString());
            });
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Create a mission (no vessel) and advance it through the status chain to the target status.
        /// Chain: Pending -> Assigned -> InProgress -> WorkProduced -> PullRequestOpen
        /// </summary>
        private async Task<string> CreateAndAdvanceMissionAsync(string title, string targetStatus)
        {
            Mission mission = await CreateMissionAsync(title);
            string missionId = mission.Id!;

            string[] chain = new[] { "Assigned", "InProgress", "WorkProduced", "PullRequestOpen" };
            foreach (string status in chain)
            {
                HttpResponseMessage resp = await TransitionAsync(missionId, status);
                resp.EnsureSuccessStatusCode();
                if (status == targetStatus) break;
            }

            return missionId;
        }

        private async Task<Mission> CreateMissionAsync(string title)
        {
            StringContent content = JsonHelper.ToJsonContent(new { Title = title });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/missions", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            // When mission stays Pending (no captain available), the API returns
            // { "Mission": {...}, "Warning": "..." } instead of the mission directly.
            MissionCreateResponse wrapper = JsonHelper.Deserialize<MissionCreateResponse>(body);
            Mission mission = wrapper.Mission ?? JsonHelper.Deserialize<Mission>(body);

            return mission;
        }

        private async Task<CheckRun> ImportCheckAsync(string vesselId, string missionId, string status, string command)
        {
            HttpResponseMessage response = await _AuthClient.PostAsync("/api/v1/check-runs/import",
                JsonHelper.ToJsonContent(new
                {
                    VesselId = vesselId,
                    MissionId = missionId,
                    Type = "Build",
                    Status = status,
                    ProviderName = "manual-test",
                    ExternalId = Guid.NewGuid().ToString("N"),
                    Command = command
                })).ConfigureAwait(false);
            AssertStatusCode(HttpStatusCode.Created, response);
            return await JsonHelper.DeserializeAsync<CheckRun>(response).ConfigureAwait(false);
        }

        private async Task AssertCheckIsOnSecondPageAsync(string missionId, string checkId)
        {
            EnumerationResult<CheckRun> firstPage = await GetTypedAsync<EnumerationResult<CheckRun>>(
                "/api/v1/check-runs/enumerate",
                new { MissionId = missionId, PageNumber = 1, PageSize = 100 }).ConfigureAwait(false);
            AssertFalse((firstPage.Objects ?? new List<CheckRun>()).Any(check => check.Id == checkId),
                "blocking Check is not on the first page");

            EnumerationResult<CheckRun> secondPage = await GetTypedAsync<EnumerationResult<CheckRun>>(
                "/api/v1/check-runs/enumerate",
                new { MissionId = missionId, PageNumber = 2, PageSize = 100 }).ConfigureAwait(false);
            AssertTrue((secondPage.Objects ?? new List<CheckRun>()).Any(check => check.Id == checkId),
                "blocking Check is present on the second page");
        }

        private async Task<Vessel> CreateVesselWithLandingModeAsync(string name, string landingMode)
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string repoUrl = TestRepoHelper.GetLocalBareRepoUrl();
            StringContent content = JsonHelper.ToJsonContent(new { Name = uniqueName, RepoUrl = repoUrl, LandingMode = landingMode });
            HttpResponseMessage resp = await _AuthClient.PostAsync("/api/v1/vessels", content).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await JsonHelper.DeserializeAsync<Vessel>(resp);
        }

        private async Task<HttpResponseMessage> TransitionAsync(string missionId, string status)
        {
            StringContent content = JsonHelper.ToJsonContent(new { Status = status });
            return await _AuthClient.PutAsync("/api/v1/missions/" + missionId + "/status", content).ConfigureAwait(false);
        }

        private async Task<Mission> GetMissionAsync(string missionId)
        {
            return await GetTypedAsync<Mission>("/api/v1/missions/" + missionId).ConfigureAwait(false);
        }

        private async Task AttachActiveDockAsync(string missionId, string captainId, Vessel vessel)
        {
            DatabaseDriver database = ReadServerDatabase();
            Captain captain = await GetTypedAsync<Captain>("/api/v1/captains/" + captainId).ConfigureAwait(false);
            Dock dock = await database.Docks.CreateAsync(new Dock(vessel.Id!)
            {
                TenantId = captain.TenantId,
                UserId = captain.UserId,
                CaptainId = captainId,
                Active = true
            }).ConfigureAwait(false);
            Mission mission = await database.Missions.ReadAsync(missionId).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Active dock fixture mission was not found.");
            mission.DockId = dock.Id;
            await database.Missions.UpdateAsync(mission).ConfigureAwait(false);
        }

        private enum CompletionFixture
        {
            NoCommit,
            Landed,
            LandedInReview
        }

        private sealed class CompletionDecision
        {
            public bool Allowed { get; set; }
            public string? Reason { get; set; }
            public string Detail { get; set; } = String.Empty;
        }

        /// <summary>
        /// Build an Implementation mission in InProgress (or Review) for a manual Complete request.
        /// </summary>
        /// <remarks>
        /// The mission is created without a vessel so no idle captain can claim it. A landed fixture
        /// then records a vessel backed by its own repository and that repository's main commit
        /// directly in the store, so the completion gate proves real ancestry.
        /// </remarks>
        private async Task<string> CreateInProgressCompletionFixtureAsync(string title, CompletionFixture fixture)
        {
            Mission mission = await CreateMissionAsync("manual entry point " + title);
            string missionId = mission.Id!;

            if (fixture != CompletionFixture.NoCommit)
            {
                DedicatedBareRepo repo = TestRepoHelper.CreateDedicatedBareRepo();
                HttpResponseMessage vesselResponse = await _AuthClient.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                {
                    Name = "manual-entry-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    RepoUrl = repo.Url,
                    LocalPath = repo.Path,
                    DefaultBranch = "main"
                })).ConfigureAwait(false);
                AssertStatusCode(HttpStatusCode.Created, vesselResponse);
                Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(vesselResponse).ConfigureAwait(false);

                DatabaseDriver database = ReadServerDatabase();
                Mission stored = await database.Missions.ReadAsync(missionId).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Completion fixture mission was not found.");
                stored.VesselId = vessel.Id;
                stored.CommitHash = repo.HeadCommit;
                await database.Missions.UpdateAsync(stored).ConfigureAwait(false);
            }

            string[] chain = fixture == CompletionFixture.LandedInReview
                ? new[] { "Assigned", "InProgress", "Review" }
                : new[] { "Assigned", "InProgress" };
            foreach (string status in chain)
            {
                HttpResponseMessage resp = await TransitionAsync(missionId, status).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                AssertStatusCode(HttpStatusCode.OK, resp, "fixture transition to " + status + ": " + body);
            }

            return missionId;
        }

        private async Task<CompletionDecision> CompleteViaRestAsync(string missionId)
        {
            HttpResponseMessage resp = await TransitionAsync(missionId, "Complete").ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            bool allowed = resp.StatusCode == HttpStatusCode.OK
                && JsonHelper.Deserialize<Mission>(body).Status == MissionStatusEnum.Complete;
            return new CompletionDecision { Allowed = allowed, Reason = ReadCompletionReason(body), Detail = (int)resp.StatusCode + " " + body };
        }

        private async Task<CompletionDecision> CompleteViaWebSocketAsync(string missionId)
        {
            using ClientWebSocket ws = new ClientWebSocket();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ws.ConnectAsync(new Uri("ws://localhost:" + _RestPort + "/ws"), cts.Token).ConfigureAwait(false);

            await SendWebSocketJsonAsync(ws, new { Route = "authenticate", apiKey = _ApiKey }, cts.Token).ConfigureAwait(false);
            JsonElement auth = await ReceiveWebSocketFrameAsync(ws, frame => true, cts.Token).ConfigureAwait(false);
            if (auth.GetProperty("type").GetString() != "auth.result")
                throw new InvalidOperationException("WebSocket authentication failed: " + auth.GetRawText());

            await SendWebSocketJsonAsync(ws, new { Route = "command", action = "transition_mission_status", id = missionId, status = "Complete" }, cts.Token).ConfigureAwait(false);
            JsonElement reply = await ReceiveWebSocketFrameAsync(ws, frame =>
                frame.TryGetProperty("type", out JsonElement type)
                && (type.GetString() == "command.result" || type.GetString() == "command.error"), cts.Token).ConfigureAwait(false);

            string raw = reply.GetRawText();
            bool allowed = reply.GetProperty("type").GetString() == "command.result"
                && JsonHelper.Deserialize<Mission>(reply.GetProperty("data").GetRawText()).Status == MissionStatusEnum.Complete;
            return new CompletionDecision { Allowed = allowed, Reason = ReadCompletionReason(raw), Detail = raw };
        }

        private async Task<CompletionDecision> CompleteViaMcpAsync(string missionId)
        {
            object request = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new { name = "armada_transition_mission_status", arguments = new { missionId = missionId, status = "Complete" } }
            };
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp");
            httpRequest.Content = JsonHelper.ToJsonContent(request);
            httpRequest.Headers.Add("Accept", "application/json, text/event-stream");
            HttpResponseMessage response = await _McpClient.SendAsync(httpRequest).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            string json = body;
            if (String.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                json = body.Split('\n').First(line => line.StartsWith("data:", StringComparison.Ordinal)).Substring(5).Trim();
            }

            JsonElement envelope = JsonSerializer.Deserialize<JsonElement>(json);
            if (!envelope.TryGetProperty("result", out JsonElement result))
                throw new InvalidOperationException("MCP transition returned no result: " + json);
            string text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? String.Empty;
            JsonElement payload = JsonSerializer.Deserialize<JsonElement>(text);
            bool allowed = payload.ValueKind == JsonValueKind.Object
                && !payload.TryGetProperty("Error", out _)
                && payload.TryGetProperty("Status", out JsonElement statusElement)
                && String.Equals(statusElement.ToString(), "Complete", StringComparison.Ordinal);
            return new CompletionDecision { Allowed = allowed, Reason = ReadCompletionReason(text), Detail = text };
        }

        private static string? ReadCompletionReason(string text)
        {
            Match match = Regex.Match(text ?? String.Empty, "manual_completion_[a-z_]+");
            return match.Success ? match.Value : null;
        }

        private async Task AssertCompletionRefusedStateAsync(string missionId, MissionStatusEnum unchangedStatus)
        {
            Mission stored = await GetMissionAsync(missionId);
            AssertEqual(unchangedStatus, stored.Status, "refused completion leaves the status unchanged");
            AssertTrue(stored.CompletedUtc == null, "refused completion does not stamp CompletedUtc");
        }

        private static async Task SendWebSocketJsonAsync(ClientWebSocket ws, object message, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonHelper.Serialize(message));
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        }

        private static async Task<JsonElement> ReceiveWebSocketFrameAsync(ClientWebSocket ws, Func<JsonElement, bool> accept, CancellationToken token)
        {
            byte[] buffer = new byte[1048576];
            while (true)
            {
                int count = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new InvalidOperationException("WebSocket closed before the expected frame.");
                    count += result.Count;
                }
                while (!result.EndOfMessage);

                using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, count));
                JsonElement frame = doc.RootElement.Clone();
                if (accept(frame)) return frame;
            }
        }

        private DatabaseDriver ReadServerDatabase()
        {
            return (DatabaseDriver)typeof(ArmadaServer)
                .GetField("_Database", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_Server)!;
        }

        private IAdmiralService ReadServerAdmiral()
        {
            return (IAdmiralService)typeof(ArmadaServer)
                .GetField("_Admiral", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_Server)!;
        }

        private void RegisterActiveProcessForRestProof(int processId, string captainId, string missionId)
        {
            object lifecycle = typeof(ArmadaServer)
                .GetField("_AgentLifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_Server)!;
            Dictionary<int, string> processToCaptain = (Dictionary<int, string>)lifecycle.GetType()
                .GetField("_ProcessToCaptain", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(lifecycle)!;
            Dictionary<int, string> processToMission = (Dictionary<int, string>)lifecycle.GetType()
                .GetField("_ProcessToMission", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(lifecycle)!;
            lock (processToCaptain)
            {
                processToCaptain[processId] = captainId;
                processToMission[processId] = missionId;
            }
        }

        private void UnregisterActiveProcessForRestProof(int processId)
        {
            object lifecycle = typeof(ArmadaServer)
                .GetField("_AgentLifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_Server)!;
            Dictionary<int, string> processToCaptain = (Dictionary<int, string>)lifecycle.GetType()
                .GetField("_ProcessToCaptain", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(lifecycle)!;
            Dictionary<int, string> processToMission = (Dictionary<int, string>)lifecycle.GetType()
                .GetField("_ProcessToMission", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(lifecycle)!;
            lock (processToCaptain)
            {
                processToCaptain.Remove(processId);
                processToMission.Remove(processId);
            }
        }

        private async Task<T> GetTypedAsync<T>(string path)
        {
            HttpResponseMessage resp = await _AuthClient.GetAsync(path).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException("GET " + path + " returned " + (int)resp.StatusCode + ": " + body);
            return JsonHelper.Deserialize<T>(body);
        }

        private async Task<T> GetTypedAsync<T>(string path, object body)
        {
            HttpResponseMessage resp = await _AuthClient.PostAsync(path, JsonHelper.ToJsonContent(body)).ConfigureAwait(false);
            string responseBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException("POST " + path + " returned " + (int)resp.StatusCode + ": " + responseBody);
            return JsonHelper.Deserialize<T>(responseBody);
        }

        #endregion
    }
}
