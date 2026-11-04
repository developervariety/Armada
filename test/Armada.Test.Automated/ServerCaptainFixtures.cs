namespace Armada.Test.Automated
{
    using System;
    using System.Net.Http;
    using System.Reflection;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
    using Armada.Test.Common;

    /// <summary>
    /// Stores captain rows that no API may create. Captain state, assignment and process are
    /// server-owned, so every create and update route refuses them; a fixture that needs a
    /// captain already at work writes the row through the running server's database.
    /// </summary>
    public static class ServerCaptainFixtures
    {
        #region Private-Members

        private static readonly TimeSpan _BusyWaitBound = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan _BusyPollInterval = TimeSpan.FromMilliseconds(50);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Store a Working captain in the default tenant.
        /// </summary>
        /// <param name="server">Running in-process server.</param>
        /// <param name="captainId">Captain ID, or null for a generated one.</param>
        /// <param name="name">Captain name.</param>
        /// <param name="runtime">Agent runtime.</param>
        /// <param name="missionId">Mission the captain owns, or null.</param>
        /// <param name="processId">Process the captain owns, or null.</param>
        /// <param name="allowedPersonas">JSON array of allowed personas, or null for any.</param>
        /// <returns>The stored captain.</returns>
        public static async Task<Captain> CreateWorkingCaptainAsync(
            ArmadaServer server,
            string? captainId,
            string name,
            AgentRuntimeEnum runtime,
            string? missionId,
            int? processId,
            string? allowedPersonas)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            Captain captain = new Captain(name, runtime);
            if (!String.IsNullOrEmpty(captainId)) captain.Id = captainId;
            captain.TenantId = Armada.Core.Constants.DefaultTenantId;
            captain.UserId = Armada.Core.Constants.DefaultUserId;
            captain.State = CaptainStateEnum.Working;
            captain.CurrentMissionId = missionId;
            captain.ProcessId = processId;
            captain.AllowedPersonas = allowedPersonas;
            DatabaseDriver database = (DatabaseDriver)typeof(ArmadaServer)
                .GetField("_Database", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server)!;
            return await database.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        /// <summary>
        /// Store an idle Premium captain for a positive dispatch fixture in the mission tenant.
        /// </summary>
        public static async Task<Captain> CreateEligibleIdleCaptainAsync(
            ArmadaServer server,
            string tenantId,
            string userId,
            params string[] allowedPersonas)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            Captain captain = new Captain("eligible-test-captain-" + Guid.NewGuid().ToString("N"))
            {
                TenantId = tenantId,
                UserId = userId,
                Tier = CaptainTierEnum.Premium,
                State = CaptainStateEnum.Idle,
                AllowedPersonas = JsonSerializer.Serialize(allowedPersonas)
            };
            DatabaseDriver database = (DatabaseDriver)typeof(ArmadaServer)
                .GetField("_Database", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server)!;
            return await database.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete one fixture captain through the server route after its owned work is closed.
        /// A voyage queues its assignment in the background, so a pass that started before the voyage was
        /// cancelled can still claim the stopped captain and then hand it back when its commit finds the
        /// mission gone. A delete refused while the captain is busy therefore waits, within a bound, for the
        /// captain to leave the busy states, then stops and deletes it once more; a captain that stays busy
        /// fails by name.
        /// </summary>
        public static async Task DeleteFixtureCaptainAsync(HttpClient authClient, string captainId)
        {
            if (authClient == null) throw new ArgumentNullException(nameof(authClient));
            if (String.IsNullOrWhiteSpace(captainId)) throw new ArgumentException("A captain ID is required.", nameof(captainId));
            await StopFixtureCaptainAsync(authClient, captainId).ConfigureAwait(false);
            string? conflict = await TryDeleteFixtureCaptainAsync(authClient, captainId).ConfigureAwait(false);
            if (conflict == null) return;

            CaptainStateEnum settled = await WaitUntilNotBusyAsync(authClient, captainId, _BusyWaitBound).ConfigureAwait(false);
            if (IsBusy(settled))
            {
                throw new InvalidOperationException("fixture_captain_still_busy: captain " + captainId + " was still " + settled
                    + " " + _BusyWaitBound.TotalSeconds + " s after its delete was refused: " + conflict);
            }

            Console.WriteLine("  fixture captain " + captainId + " was busy at delete; it reached " + settled + ", deleting again");
            await StopFixtureCaptainAsync(authClient, captainId).ConfigureAwait(false);
            string? second = await TryDeleteFixtureCaptainAsync(authClient, captainId).ConfigureAwait(false);
            if (second != null)
                throw new InvalidOperationException("Could not remove the fixture captain after voyage cleanup: " + second);
        }

        #endregion

        #region Private-Methods

        private static async Task StopFixtureCaptainAsync(HttpClient authClient, string captainId)
        {
            using (HttpResponseMessage stopResponse = await authClient.PostAsync(
                "/api/v1/captains/" + captainId + "/stop", null).ConfigureAwait(false))
            {
                if (stopResponse.StatusCode != System.Net.HttpStatusCode.OK
                    && stopResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
                {
                    string detail = await stopResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new InvalidOperationException("Could not stop the fixture captain after voyage cleanup: " + detail);
                }
            }
        }

        /// <summary>Delete the captain; null when it is gone, the refusal detail when the server refused it as busy.</summary>
        private static async Task<string?> TryDeleteFixtureCaptainAsync(HttpClient authClient, string captainId)
        {
            using (HttpResponseMessage response = await authClient.DeleteAsync("/api/v1/captains/" + captainId).ConfigureAwait(false))
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NoContent
                    || response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;

                string detail = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.StatusCode != System.Net.HttpStatusCode.Conflict)
                    throw new InvalidOperationException("Could not remove the fixture captain after voyage cleanup: " + detail);
                return detail;
            }
        }

        /// <summary>Read the captain until it leaves the busy states or the bound passes; returns the last state read.</summary>
        private static async Task<CaptainStateEnum> WaitUntilNotBusyAsync(HttpClient authClient, string captainId, TimeSpan bound)
        {
            DateTime deadline = DateTime.UtcNow + bound;
            while (true)
            {
                using (HttpResponseMessage response = await authClient.GetAsync("/api/v1/captains/" + captainId).ConfigureAwait(false))
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return CaptainStateEnum.Idle;
                    Captain captain = await JsonHelper.DeserializeAsync<Captain>(response).ConfigureAwait(false);
                    if (!IsBusy(captain.State) || DateTime.UtcNow >= deadline) return captain.State;
                }

                await Task.Delay(_BusyPollInterval).ConfigureAwait(false);
            }
        }

        /// <summary>The states in which the server refuses a captain delete.</summary>
        private static bool IsBusy(CaptainStateEnum state)
        {
            return state == CaptainStateEnum.Working
                || state == CaptainStateEnum.Planning
                || state == CaptainStateEnum.Refining;
        }

        #endregion
    }
}
