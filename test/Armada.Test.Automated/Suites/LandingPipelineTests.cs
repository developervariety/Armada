namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Reflection;
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

        #endregion

        #region Constructors-and-Factories

        public LandingPipelineTests(HttpClient authClient, HttpClient unauthClient, ArmadaServer server)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
            _Server = server ?? throw new ArgumentNullException(nameof(server));
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
                for (int index = 0; index < 101; index++)
                {
                    await ImportCheckAsync(checkVessel.Id!, missionId, "Passed", "isolated-check-" + index);
                }
                await ImportCheckAsync(checkVessel.Id!, missionId, "Failed", "isolated-failed-check");

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
                for (int index = 0; index < 101; index++)
                {
                    await ImportCheckAsync(checkVessel.Id!, missionId, "Passed", "isolated-check-" + index);
                }
                await ImportCheckAsync(checkVessel.Id!, missionId, "Pending", "isolated-pending-check");

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

        private async Task ImportCheckAsync(string vesselId, string missionId, string status, string command)
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

        #endregion
    }
}
