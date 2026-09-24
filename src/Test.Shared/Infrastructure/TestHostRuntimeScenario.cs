namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// End-to-end check, shared by every test host, that a dispatched mission starts the non-launching test
    /// runtime and no agent process, and that stopping its captain ends that start with the stopped exit code so
    /// the captain can be deleted. A new idle captain takes the oldest Pending mission, so a host runs this
    /// before any suite leaves open work. Failures throw with the observed state.
    /// </summary>
    public static class TestHostRuntimeScenario
    {
        #region Private-Members

        private static readonly TimeSpan _AssignTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan _ReleaseTimeout = TimeSpan.FromSeconds(30);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Dispatch a one-mission voyage to a new ClaudeCode captain and verify the start, the stop and the cleanup.
        /// </summary>
        /// <param name="client">Authenticated client for the Admiral under test.</param>
        /// <returns>Task.</returns>
        public static async Task RunDispatchedMissionAsync(HttpClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);

            Fleet fleet = await PostAsync<Fleet>(client, "/api/v1/fleets", new { Name = "TestRuntimeFleet-" + suffix }).ConfigureAwait(false);
            Vessel vessel = await PostAsync<Vessel>(client, "/api/v1/vessels",
                new { Name = "TestRuntimeVessel-" + suffix, RepoUrl = TestRepoHelper.GetLocalBareRepoUrl(), FleetId = fleet.Id }).ConfigureAwait(false);
            Captain captain = await PostAsync<Captain>(client, "/api/v1/captains",
                new { Name = "test-runtime-captain-" + suffix, Runtime = "ClaudeCode" }).ConfigureAwait(false);
            Voyage voyage = await PostAsync<Voyage>(client, "/api/v1/voyages", new
            {
                Title = "Test runtime voyage " + suffix,
                Description = "Verifies the test host starts no agent process",
                VesselId = vessel.Id,
                Missions = new object[] { new { Title = "Test runtime mission", Description = "No process is started" } }
            }).ConfigureAwait(false);

            Mission? started = null;
            try
            {
                started = await WaitForStartedMissionAsync(client, voyage.Id).ConfigureAwait(false);
                int processId = started.ProcessId!.Value;

                TestProcessLaunch? launch = TestProcessLaunchLog.All().LastOrDefault(l => l.ProcessId == processId);
                if (launch == null)
                    throw new InvalidOperationException("Mission " + started.Id + " reports process " + processId + ", which no test runtime started.");
                if (launch.LaunchedProcess)
                    throw new InvalidOperationException("Mission " + started.Id + " started an agent process: " + launch);
                if (launch.RuntimeType != AgentRuntimeEnum.ClaudeCode)
                    throw new InvalidOperationException("Expected a ClaudeCode start, recorded " + launch);
                if (!NonLaunchingAgentRuntime.IsRunning(processId))
                    throw new InvalidOperationException("The test runtime start " + processId + " is not running while its mission is active.");
            }
            finally
            {
                // Cancelling a voyage cancels its active missions and recalls the captains working them. A mission
                // the voyage cancel left active is cancelled on its own; a finished mission keeps its outcome and
                // refuses a cancel, so it is already cleaned up. The captain is stopped either way.
                HttpResponseMessage cancel = await client.DeleteAsync("/api/v1/voyages/" + voyage.Id).ConfigureAwait(false);
                if (cancel.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException("Cancelling voyage " + voyage.Id + " returned " + (int)cancel.StatusCode + ".");
                if (started != null)
                {
                    Mission current = await GetAsync<Mission>(client, "/api/v1/missions/" + started.Id).ConfigureAwait(false);
                    if (MissionStateMachine.IsValidTransition(current.Status, MissionStatusEnum.Cancelled))
                        await DeleteAsync(client, "/api/v1/missions/" + started.Id).ConfigureAwait(false);
                    await PostAsync<object>(client, "/api/v1/captains/" + started.CaptainId + "/stop", new { }).ConfigureAwait(false);
                }
            }

            if (started == null)
                throw new InvalidOperationException("No mission of voyage " + voyage.Id + " started.");

            int stoppedProcessId = started.ProcessId!.Value;
            string holderId = started.CaptainId!;
            await WaitForCaptainReleasedAsync(client, holderId).ConfigureAwait(false);
            if (!String.Equals(holderId, captain.Id, StringComparison.Ordinal))
                await WaitForCaptainReleasedAsync(client, captain.Id).ConfigureAwait(false);

            if (NonLaunchingAgentRuntime.IsRunning(stoppedProcessId))
                throw new InvalidOperationException("Cancelling the voyage left test runtime start " + stoppedProcessId + " running.");
            TestProcessLaunch stopped = TestProcessLaunchLog.All().Last(l => l.ProcessId == stoppedProcessId);
            if (stopped.ExitCode != NonLaunchingAgentRuntime.StoppedExitCode)
                throw new InvalidOperationException("Expected the cancelled start to report exit " + NonLaunchingAgentRuntime.StoppedExitCode + ", recorded " + stopped);

            await DeleteAsync(client, "/api/v1/captains/" + captain.Id).ConfigureAwait(false);
            await DeleteAsync(client, "/api/v1/vessels/" + vessel.Id).ConfigureAwait(false);
            await DeleteAsync(client, "/api/v1/fleets/" + fleet.Id).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private static async Task<Mission> WaitForStartedMissionAsync(HttpClient client, string voyageId)
        {
            DateTime deadline = DateTime.UtcNow + _AssignTimeout;
            List<Mission> last = new List<Mission>();
            while (DateTime.UtcNow < deadline)
            {
                HttpResponseMessage response = await client.GetAsync("/api/v1/voyages/" + voyageId).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                VoyageDetailResponse detail = await JsonHelper.DeserializeAsync<VoyageDetailResponse>(response).ConfigureAwait(false);
                last = detail.Missions ?? new List<Mission>();
                Mission? started = last.FirstOrDefault(m => m.ProcessId.HasValue && !String.IsNullOrEmpty(m.CaptainId));
                if (started != null) return started;
                await Task.Delay(250).ConfigureAwait(false);
            }

            throw new InvalidOperationException("No mission of voyage " + voyageId + " started within " + _AssignTimeout.TotalSeconds + "s. Missions: "
                + String.Join(", ", last.Select(m => m.Id + "=" + m.Status)));
        }

        private static async Task WaitForCaptainReleasedAsync(HttpClient client, string captainId)
        {
            DateTime deadline = DateTime.UtcNow + _ReleaseTimeout;
            Captain? captain = null;
            while (DateTime.UtcNow < deadline)
            {
                HttpResponseMessage response = await client.GetAsync("/api/v1/captains/" + captainId).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                captain = await JsonHelper.DeserializeAsync<Captain>(response).ConfigureAwait(false);
                if (captain.State != CaptainStateEnum.Working && !captain.ProcessId.HasValue) return;
                await Task.Delay(250).ConfigureAwait(false);
            }

            throw new InvalidOperationException("Captain " + captainId + " was not released within " + _ReleaseTimeout.TotalSeconds
                + "s of the cancel: state " + captain?.State + ", process " + captain?.ProcessId + ", mission " + captain?.CurrentMissionId + ".");
        }

        private static async Task<T> PostAsync<T>(HttpClient client, string route, object body)
        {
            HttpResponseMessage response = await client.PostAsync(route, JsonHelper.ToJsonContent(body)).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new InvalidOperationException("POST " + route + " returned " + (int)response.StatusCode + ": " + text);
            }
            return await JsonHelper.DeserializeAsync<T>(response).ConfigureAwait(false);
        }

        private static async Task<T> GetAsync<T>(HttpClient client, string route)
        {
            HttpResponseMessage response = await client.GetAsync(route).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new InvalidOperationException("GET " + route + " returned " + (int)response.StatusCode + ": " + text);
            }
            return await JsonHelper.DeserializeAsync<T>(response).ConfigureAwait(false);
        }

        private static async Task DeleteAsync(HttpClient client, string route)
        {
            HttpResponseMessage response = await client.DeleteAsync(route).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new InvalidOperationException("DELETE " + route + " returned " + (int)response.StatusCode + ": " + text);
            }
        }

        #endregion
    }
}
