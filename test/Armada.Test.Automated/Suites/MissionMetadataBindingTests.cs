namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>Metadata updates preserve mission ownership and distinguish absent bindings from explicit null.</summary>
    public sealed class MissionMetadataBindingTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Mission metadata bindings";

        private readonly HttpClient _Client;
        private readonly List<string> _MissionIds = new List<string>();
        private readonly List<string> _VesselIds = new List<string>();
        private readonly List<string> _VoyageIds = new List<string>();
        private string? _FleetId;

        /// <summary>Create binding tests against the authenticated isolated test server.</summary>
        public MissionMetadataBindingTests(HttpClient authClient)
        {
            _Client = authClient ?? throw new ArgumentNullException(nameof(authClient));
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Metadata_OmittedBindings_UpdatesMetadataAndPreservesBindings", () =>
                AssertUpdateAsync(false, null, false, null, HttpStatusCode.OK)).ConfigureAwait(false);
            await RunTest("Metadata_SameVesselWithOmittedVoyage_AcceptsUpdate", () =>
                AssertUpdateAsync(true, "same", false, null, HttpStatusCode.OK)).ConfigureAwait(false);
            await RunTest("Metadata_SameVoyageWithOmittedVessel_AcceptsUpdate", () =>
                AssertUpdateAsync(false, null, true, "same", HttpStatusCode.OK)).ConfigureAwait(false);
            await RunTest("Metadata_SameBindings_AcceptsUpdate", () =>
                AssertUpdateAsync(true, "same", true, "same", HttpStatusCode.OK)).ConfigureAwait(false);
            await RunTest("Metadata_ChangedVessel_RejectsWithoutPersistingChanges", () =>
                AssertUpdateAsync(true, "different", false, null, HttpStatusCode.Conflict)).ConfigureAwait(false);
            await RunTest("Metadata_ChangedVoyage_RejectsWithoutPersistingChanges", () =>
                AssertUpdateAsync(false, null, true, "different", HttpStatusCode.Conflict)).ConfigureAwait(false);
            await RunTest("Metadata_ExplicitNullVessel_RejectsWithoutPersistingChanges", () =>
                AssertUpdateAsync(true, null, false, null, HttpStatusCode.Conflict)).ConfigureAwait(false);
            await RunTest("Metadata_ExplicitNullVoyage_RejectsWithoutPersistingChanges", () =>
                AssertUpdateAsync(false, null, true, null, HttpStatusCode.Conflict)).ConfigureAwait(false);
            await RunTest("Metadata_ExplicitNullUnboundVoyage_AcceptsUpdate", () =>
                AssertUpdateAsync(false, null, true, null, HttpStatusCode.OK, false)).ConfigureAwait(false);
            await RunTest("MetadataBindings_Cleanup", CleanupAsync).ConfigureAwait(false);
        }

        private async Task AssertUpdateAsync(bool includeVessel, string? vesselChoice, bool includeVoyage,
            string? voyageChoice, HttpStatusCode expected, bool bindVoyage = true)
        {
            await EnsureFixtureAsync().ConfigureAwait(false);
            Dictionary<string, object?> create = new Dictionary<string, object?>
            {
                ["Title"] = "Binding original",
                ["Description"] = "Original metadata",
                ["Priority"] = 73,
                ["VesselId"] = _VesselIds[0]
            };
            if (bindVoyage) create["VoyageId"] = _VoyageIds[0];
            Mission before;
            using (StringContent content = JsonHelper.ToJsonContent(create))
            using (HttpResponseMessage response = await _Client.PostAsync("/api/v1/missions", content).ConfigureAwait(false))
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode, body);
                MissionCreateResponse wrapper = JsonHelper.Deserialize<MissionCreateResponse>(body);
                before = wrapper.Mission ?? JsonHelper.Deserialize<Mission>(body);
                AssertFalse(String.IsNullOrEmpty(before.Id));
                _MissionIds.Add(before.Id);
            }
            AssertEqual(_VesselIds[0], before.VesselId);
            AssertEqual(bindVoyage ? _VoyageIds[0] : null, before.VoyageId);
            Dictionary<string, object?> update = new Dictionary<string, object?>
            {
                ["title"] = "Binding updated",
                ["description"] = "Updated metadata",
                ["priority"] = 42
            };
            if (includeVessel) update["vesselId"] = vesselChoice == "same" ? before.VesselId : vesselChoice == "different" ? _VesselIds[1] : null;
            if (includeVoyage) update["voyageId"] = voyageChoice == "same" ? before.VoyageId : voyageChoice == "different" ? _VoyageIds[1] : null;
            // Default serialization retains explicit null; absent keys remain absent on the wire.
            using (StringContent content = new StringContent(JsonSerializer.Serialize(update), Encoding.UTF8, "application/json"))
            using (HttpResponseMessage response = await _Client.PutAsync("/api/v1/missions/" + before.Id, content).ConfigureAwait(false))
                AssertEqual(expected, response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));

            using (HttpResponseMessage response = await _Client.GetAsync("/api/v1/missions/" + before.Id).ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
                Mission stored = await JsonHelper.DeserializeAsync<Mission>(response).ConfigureAwait(false);
                AssertEqual(before.VesselId, stored.VesselId, "Stored vessel binding");
                AssertEqual(before.VoyageId, stored.VoyageId, "Stored voyage binding");
                AssertEqual(expected == HttpStatusCode.OK ? "Binding updated" : before.Title, stored.Title, "Stored title");
                AssertEqual(expected == HttpStatusCode.OK ? "Updated metadata" : before.Description, stored.Description, "Stored description");
                AssertEqual(expected == HttpStatusCode.OK ? 42 : before.Priority, stored.Priority, "Stored priority");
            }
        }

        private async Task EnsureFixtureAsync()
        {
            if (_FleetId == null)
            {
                using (StringContent content = JsonHelper.ToJsonContent(new { Name = "Metadata bindings " + Guid.NewGuid().ToString("N") }))
                using (HttpResponseMessage response = await _Client.PostAsync("/api/v1/fleets", content).ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    _FleetId = (await JsonHelper.DeserializeAsync<Fleet>(response).ConfigureAwait(false)).Id;
                }
            }
            while (_VesselIds.Count < 2)
            {
                using (StringContent content = JsonHelper.ToJsonContent(new { Name = "Binding vessel " + Guid.NewGuid().ToString("N"), FleetId = _FleetId, RepoUrl = TestRepoHelper.GetLocalBareRepoUrl() }))
                using (HttpResponseMessage response = await _Client.PostAsync("/api/v1/vessels", content).ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    _VesselIds.Add((await JsonHelper.DeserializeAsync<Vessel>(response).ConfigureAwait(false)).Id);
                }
            }
            while (_VoyageIds.Count < 2)
            {
                using (StringContent content = JsonHelper.ToJsonContent(new { Title = "Binding voyage " + Guid.NewGuid().ToString("N") }))
                using (HttpResponseMessage response = await _Client.PostAsync("/api/v1/voyages", content).ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    _VoyageIds.Add((await JsonHelper.DeserializeAsync<Voyage>(response).ConfigureAwait(false)).Id);
                }
            }
        }

        private async Task CleanupAsync()
        {
            foreach (string id in _MissionIds) await DeleteAsync("/api/v1/missions/" + id).ConfigureAwait(false);
            foreach (string id in _VoyageIds)
            {
                await DeleteAsync("/api/v1/voyages/" + id).ConfigureAwait(false);
                await DeleteAsync("/api/v1/voyages/" + id + "/purge").ConfigureAwait(false);
            }
            foreach (string id in _VesselIds) await DeleteAsync("/api/v1/vessels/" + id).ConfigureAwait(false);
            if (_FleetId != null) await DeleteAsync("/api/v1/fleets/" + _FleetId).ConfigureAwait(false);
        }

        private async Task DeleteAsync(string path)
        {
            using (HttpResponseMessage response = await _Client.DeleteAsync(path).ConfigureAwait(false))
                AssertTrue(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound,
                    "Fixture cleanup failed: " + await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
    }
}
