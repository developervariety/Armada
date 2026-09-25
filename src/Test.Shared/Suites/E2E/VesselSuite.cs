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
    /// End-to-end Vessel API descriptors covering CRUD, list, pagination, ordering, fleet filtering,
    /// and enumeration against a live in-process Armada server provided by <see cref="E2EServerFixture"/>.
    /// </summary>
    public sealed class VesselSuite : IArmadaTestSuite
    {
        #region Private-Members

        private readonly object _SeedLock = new object();
        private Task<string>? _SharedVesselFleet;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Vessel API end-to-end suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            #region CRUD - Create

            cases.Add(CaseAsync("create_vessel_github_token_override_does_not_leak_and_sets_has_override", "Create Vessel GitHubTokenOverride DoesNotLeakAndSetsHasOverride", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdFleetIds = new List<string>();
                List<string> createdVesselIds = new List<string>();

                string fleetId = await CreateFleetAsync(authClient, createdFleetIds, "GitHubOverrideFleet");
                string token = "ghp_create_override_" + Guid.NewGuid().ToString("N").Substring(0, 10);
                StringContent content = JsonHelper.ToJsonContent(new
                {
                    Name = "GitHubOverrideVessel",
                    FleetId = fleetId,
                    RepoUrl = "https://github.com/test/github-override",
                    GitHubTokenOverride = token
                });

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/vessels", content);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                string responseText = await response.Content.ReadAsStringAsync();
                AssertFalse(responseText.Contains(token, StringComparison.Ordinal));
                AssertFalse(responseText.Contains("\"gitHubTokenOverride\"", StringComparison.Ordinal));

                Vessel vessel = JsonHelper.Deserialize<Vessel>(responseText);
                createdVesselIds.Add(vessel.Id);
                AssertTrue(vessel.HasGitHubTokenOverride);

                HttpResponseMessage getResponse = await authClient.GetAsync("/api/v1/vessels/" + vessel.Id);
                string getText = await getResponse.Content.ReadAsStringAsync();
                AssertFalse(getText.Contains(token, StringComparison.Ordinal));
                AssertFalse(getText.Contains("\"gitHubTokenOverride\"", StringComparison.Ordinal));
                Vessel fetched = JsonHelper.Deserialize<Vessel>(getText);
                AssertTrue(fetched.HasGitHubTokenOverride);
            }));

            #endregion

            #region CRUD - Read

            #endregion

            #region CRUD - Update

            cases.Add(CaseAsync("update_vessel_omitting_github_token_override_preserves_existing_override", "Update Vessel OmittingGitHubTokenOverride PreservesExistingOverride", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdFleetIds = new List<string>();
                List<string> createdVesselIds = new List<string>();

                string fleetId = await CreateFleetAsync(authClient, createdFleetIds, "PreserveGitHubOverrideFleet");
                string token = "ghp_preserve_" + Guid.NewGuid().ToString("N").Substring(0, 10);

                HttpResponseMessage createResponse = await authClient.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                {
                    Name = "PreserveGitHubOverride",
                    FleetId = fleetId,
                    RepoUrl = "https://github.com/test/preserve-override",
                    GitHubTokenOverride = token
                }));
                Vessel created = await JsonHelper.DeserializeAsync<Vessel>(createResponse);
                createdVesselIds.Add(created.Id);
                AssertTrue(created.HasGitHubTokenOverride);

                HttpResponseMessage updateResponse = await authClient.PutAsync("/api/v1/vessels/" + created.Id, JsonHelper.ToJsonContent(new
                {
                    Name = "PreserveGitHubOverrideUpdated",
                    FleetId = fleetId,
                    RepoUrl = "https://github.com/test/preserve-override"
                }));
                AssertEqual(HttpStatusCode.OK, updateResponse.StatusCode);

                Vessel updated = await JsonHelper.DeserializeAsync<Vessel>(updateResponse);
                AssertTrue(updated.HasGitHubTokenOverride);

                HttpResponseMessage getResponse = await authClient.GetAsync("/api/v1/vessels/" + created.Id);
                Vessel fetched = await JsonHelper.DeserializeAsync<Vessel>(getResponse);
                AssertTrue(fetched.HasGitHubTokenOverride);
            }));

            cases.Add(CaseAsync("update_vessel_github_token_override_does_not_echo", "Update Vessel GitHubTokenOverride DoesNotEcho", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdFleetIds = new List<string>();
                List<string> createdVesselIds = new List<string>();

                string fleetId = await CreateFleetAsync(authClient, createdFleetIds, "EchoGitHubOverrideFleet");
                HttpResponseMessage createResponse = await authClient.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                {
                    Name = "EchoGitHubOverride",
                    FleetId = fleetId,
                    RepoUrl = "https://github.com/test/echo-override"
                }));
                Vessel created = await JsonHelper.DeserializeAsync<Vessel>(createResponse);
                createdVesselIds.Add(created.Id);
                AssertFalse(created.HasGitHubTokenOverride);

                string token = "ghp_update_echo_" + Guid.NewGuid().ToString("N").Substring(0, 10);
                HttpResponseMessage updateResponse = await authClient.PutAsync("/api/v1/vessels/" + created.Id, JsonHelper.ToJsonContent(new
                {
                    Name = "EchoGitHubOverride",
                    FleetId = fleetId,
                    RepoUrl = "https://github.com/test/echo-override",
                    GitHubTokenOverride = token
                }));
                AssertEqual(HttpStatusCode.OK, updateResponse.StatusCode);
                string updateText = await updateResponse.Content.ReadAsStringAsync();
                AssertFalse(updateText.Contains(token, StringComparison.Ordinal));
                AssertFalse(updateText.Contains("\"gitHubTokenOverride\"", StringComparison.Ordinal));
                AssertTrue(JsonHelper.Deserialize<Vessel>(updateText).HasGitHubTokenOverride);

                HttpResponseMessage listResponse = await authClient.GetAsync("/api/v1/vessels");
                string listText = await listResponse.Content.ReadAsStringAsync();
                AssertFalse(listText.Contains(token, StringComparison.Ordinal));
                AssertFalse(listText.Contains("\"gitHubTokenOverride\"", StringComparison.Ordinal));
            }));

            cases.Add(CaseAsync("update_vessel_empty_github_token_override_clears_override", "Update Vessel EmptyGitHubTokenOverride ClearsOverride", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                List<string> createdFleetIds = new List<string>();
                List<string> createdVesselIds = new List<string>();

                string fleetId = await CreateFleetAsync(authClient, createdFleetIds, "ClearGitHubOverrideFleet");
                string token = "ghp_clear_" + Guid.NewGuid().ToString("N").Substring(0, 10);

                HttpResponseMessage createResponse = await authClient.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                {
                    Name = "ClearGitHubOverride",
                    FleetId = fleetId,
                    RepoUrl = "https://github.com/test/clear-override",
                    GitHubTokenOverride = token
                }));
                Vessel created = await JsonHelper.DeserializeAsync<Vessel>(createResponse);
                createdVesselIds.Add(created.Id);

                HttpResponseMessage updateResponse = await authClient.PutAsync("/api/v1/vessels/" + created.Id, JsonHelper.ToJsonContent(new
                {
                    Name = "ClearGitHubOverride",
                    FleetId = fleetId,
                    RepoUrl = "https://github.com/test/clear-override",
                    GitHubTokenOverride = ""
                }));
                AssertEqual(HttpStatusCode.OK, updateResponse.StatusCode);
                Vessel updated = await JsonHelper.DeserializeAsync<Vessel>(updateResponse);
                AssertFalse(updated.HasGitHubTokenOverride);

                HttpResponseMessage getResponse = await authClient.GetAsync("/api/v1/vessels/" + created.Id);
                Vessel fetched = await JsonHelper.DeserializeAsync<Vessel>(getResponse);
                AssertFalse(fetched.HasGitHubTokenOverride);
            }));

            #endregion

            #region CRUD - Delete

            #endregion

            #region List - Empty and Basic

            #endregion

            #region List - Pagination

            cases.Add(CaseAsync("list_vessels_25_items_pagesize_10_page_1_has_10_items", "List Vessels 25 Items PageSize 10 Page 1 Has 10 Items", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                string fleetId = await EnsureVesselsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/vessels?pageSize=10&pageNumber=1&fleetId=" + fleetId);
                EnumerationResult<Vessel> result = await JsonHelper.DeserializeAsync<EnumerationResult<Vessel>>(response);

                AssertEqual(10, result.Objects.Count);
                AssertEqual(25, result.TotalRecords);
                AssertEqual(3, result.TotalPages);
                AssertEqual(1, result.PageNumber);
                AssertEqual(10, result.PageSize);
            }));

            cases.Add(CaseAsync("list_vessels_25_items_pagesize_10_page_2_has_10_items", "List Vessels 25 Items PageSize 10 Page 2 Has 10 Items", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                string fleetId = await EnsureVesselsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/vessels?pageSize=10&pageNumber=2&fleetId=" + fleetId);
                EnumerationResult<Vessel> result = await JsonHelper.DeserializeAsync<EnumerationResult<Vessel>>(response);

                AssertEqual(10, result.Objects.Count);
                AssertEqual(2, result.PageNumber);
            }));

            cases.Add(CaseAsync("list_vessels_25_items_pagesize_10_page_3_has_5_items", "List Vessels 25 Items PageSize 10 Page 3 Has 5 Items", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                string fleetId = await EnsureVesselsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/vessels?pageSize=10&pageNumber=3&fleetId=" + fleetId);
                EnumerationResult<Vessel> result = await JsonHelper.DeserializeAsync<EnumerationResult<Vessel>>(response);

                AssertEqual(5, result.Objects.Count);
                AssertEqual(3, result.PageNumber);
            }));

            cases.Add(CaseAsync("list_vessels_25_items_verify_first_record_page_1_and_last_record_page_3", "List Vessels 25 Items Verify First Record Page 1 And Last Record Page 3", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                string fleetId = await EnsureVesselsSeededAsync(authClient);

                HttpResponseMessage page1Resp = await authClient.GetAsync(
                    "/api/v1/vessels?pageSize=10&pageNumber=1&order=CreatedAscending&fleetId=" + fleetId);
                EnumerationResult<Vessel> page1Result = await JsonHelper.DeserializeAsync<EnumerationResult<Vessel>>(page1Resp);
                string firstItemName = page1Result.Objects[0].Name;

                HttpResponseMessage page3Resp = await authClient.GetAsync(
                    "/api/v1/vessels?pageSize=10&pageNumber=3&order=CreatedAscending&fleetId=" + fleetId);
                EnumerationResult<Vessel> page3Result = await JsonHelper.DeserializeAsync<EnumerationResult<Vessel>>(page3Resp);
                string lastItemName = page3Result.Objects[page3Result.Objects.Count - 1].Name;

                AssertStartsWith("SharedPagVessel_00", firstItemName);
                AssertStartsWith("SharedPagVessel_24", lastItemName);
            }));

            cases.Add(CaseAsync("list_vessels_page_beyond_last_page_returns_empty_objects", "List Vessels Page Beyond Last Page Returns Empty Objects", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                string fleetId = await EnsureVesselsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync(
                    "/api/v1/vessels?pageSize=10&pageNumber=99&fleetId=" + fleetId);
                EnumerationResult<Vessel> result = await JsonHelper.DeserializeAsync<EnumerationResult<Vessel>>(response);

                AssertEqual(0, result.Objects.Count);
            }));

            #endregion

            #region List - Ordering

            #endregion

            #region List - Filter by FleetId

            #endregion

            #region Enumerate (POST)

            #endregion

            #region Enumerate - Pagination Consistency with GET

            #endregion

            #region CRUD - ProjectContext and StyleGuide

            #endregion

            #region Enumerate - Edge Cases

            #endregion

            return new TestSuiteDescriptor(
                suiteId: "E2E.Vessel",
                displayName: "Vessel API Tests",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Creates a fleet and returns its ID.
        /// </summary>
        /// <summary>
        /// Ensure a shared fleet holding exactly 25 vessels exists, seeding it once for the suite, and
        /// return that fleet's ID. The pagination cases scope their queries with <c>?fleetId=</c>, so a
        /// single shared fleet of 25 vessels satisfies their exact-count assertions (25 total, 3 pages,
        /// page 3 has 5) without each case re-creating a fleet and 25 vessels over sequential HTTP
        /// round-trips. The vessels are named <c>SharedPagVessel_00..24</c> and created in order, so a
        /// CreatedAscending query returns _00 first and _24 last. The seed Task is memoized: the first
        /// pagination case pays the cost and the rest await the completed Task. Ordering and fleet-filter
        /// cases keep creating their own fleets, so they are unaffected by this shared dataset.
        /// </summary>
        /// <param name="authClient">Authenticated client for the shared e2e server.</param>
        /// <returns>The ID of the shared fleet holding the 25 seeded vessels.</returns>
        private Task<string> EnsureVesselsSeededAsync(HttpClient authClient)
        {
            lock (_SeedLock)
            {
                if (_SharedVesselFleet == null) _SharedVesselFleet = SeedSharedVesselsAsync(authClient);
                return _SharedVesselFleet;
            }
        }

        private static async Task<string> SeedSharedVesselsAsync(HttpClient authClient)
        {
            List<string> fleetIds = new List<string>();
            List<string> vesselIds = new List<string>();

            string fleetId = await CreateFleetAsync(authClient, fleetIds, "SharedPagFleet").ConfigureAwait(false);
            for (int i = 0; i < 25; i++)
            {
                await CreateVesselAndReturnIdAsync(authClient, vesselIds, "SharedPagVessel_" + i.ToString("D2"), fleetId: fleetId).ConfigureAwait(false);
            }

            return fleetId;
        }

        private static async Task<string> CreateFleetAsync(HttpClient client, List<string> createdFleetIds, string name = "TestFleet")
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            StringContent content = JsonHelper.ToJsonContent(new { Name = uniqueName });
            HttpResponseMessage resp = await client.PostAsync("/api/v1/fleets", content);
            resp.EnsureSuccessStatusCode();
            Fleet fleet = await JsonHelper.DeserializeAsync<Fleet>(resp);
            createdFleetIds.Add(fleet.Id);
            return fleet.Id;
        }

        /// <summary>
        /// Creates a vessel and returns the typed Vessel object.
        /// </summary>
        private static async Task<Vessel> CreateVesselAsync(
            HttpClient client,
            List<string> createdVesselIds,
            string name,
            string? fleetId = null,
            string? repoUrl = null,
            string? localPath = null,
            string? workingDirectory = null,
            string? defaultBranch = null,
            bool? active = null)
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string effectiveRepoUrl = repoUrl ?? "https://github.com/test/" + uniqueName.ToLowerInvariant().Replace(" ", "-");

            object body;
            if (fleetId != null && localPath != null && workingDirectory != null && defaultBranch != null && active != null)
                body = new { Name = uniqueName, FleetId = fleetId, RepoUrl = effectiveRepoUrl, LocalPath = localPath, WorkingDirectory = workingDirectory, DefaultBranch = defaultBranch, Active = active };
            else if (fleetId != null && defaultBranch != null)
                body = new { Name = uniqueName, FleetId = fleetId, RepoUrl = effectiveRepoUrl, DefaultBranch = defaultBranch };
            else if (fleetId != null)
                body = new { Name = uniqueName, FleetId = fleetId, RepoUrl = effectiveRepoUrl };
            else
                body = new { Name = uniqueName, RepoUrl = effectiveRepoUrl };

            StringContent content = JsonHelper.ToJsonContent(body);
            HttpResponseMessage resp = await client.PostAsync("/api/v1/vessels", content);
            resp.EnsureSuccessStatusCode();
            Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(resp);
            createdVesselIds.Add(vessel.Id);
            return vessel;
        }

        /// <summary>
        /// Creates a vessel and returns only its ID.
        /// </summary>
        private static async Task<string> CreateVesselAndReturnIdAsync(
            HttpClient client,
            List<string> createdVesselIds,
            string name,
            string? fleetId = null,
            string? repoUrl = null)
        {
            Vessel vessel = await CreateVesselAsync(client, createdVesselIds, name, fleetId: fleetId, repoUrl: repoUrl);
            return vessel.Id;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "E2E.Vessel",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
