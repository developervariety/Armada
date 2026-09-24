namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Proves the properties that keep an end-to-end run independent of the host it runs on and of
    /// the order its cases execute in:
    ///
    /// a test server keeps its code index under its own data directory; a test server reads only its own
    /// settings file, so an unrelated write to the host operator's settings cannot lower this run's fleet
    /// capacity mid-run; cancelling a case's active work really
    /// frees that capacity for the next case; and a refused tool result fails naming the refusal instead
    /// of being carried forward as a blank id.
    /// </summary>
    public sealed class HarnessIsolationSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.HarnessIsolation";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the suite descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("default_data_directory_is_not_the_live_armada_home",
                "DefaultDataDirectory_IsNotTheLiveArmadaHome", TestTags.Positive, () =>
            {
                // The runner redirects the default data directory to a temp root as its first statement, the
                // same guard the unit and automated runners carry. Without it every default-resolved path --
                // settings.json above all -- lands in the live Armada home, and the in-process servers watch
                // and write the host operator's real settings.
                string liveHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".armada");
                AssertTrue(Constants.DefaultDataDirectory != liveHome,
                    "the default data directory must not be the live Armada home: " + Constants.DefaultDataDirectory);
                AssertTrue(!ArmadaSettings.DefaultSettingsPath.StartsWith(liveHome, StringComparison.Ordinal),
                    "the default settings path must not be in the live Armada home: " + ArmadaSettings.DefaultSettingsPath);
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("code_index_resolves_under_the_test_servers_data_directory",
                "CodeIndex_ResolvesUnderTheTestServersDataDirectory", TestTags.Positive, async () =>
            {
                // A test server is given its own data directory. Its code index must live there too: an
                // index directory resolved from the process-wide default instead lands in whatever that
                // default points at, which for an unredirected process is the live Armada home.
                E2EServerFixture fx = await E2EServerFixture.StartIsolatedAsync(settings => { }).ConfigureAwait(false);
                try
                {
                    string vesselId = await CreateVesselAsync(fx.AuthClient).ConfigureAwait(false);
                    HttpResponseMessage response = await fx.AuthClient.GetAsync(
                        "/api/v1/vessels/" + vesselId + "/code-index/status").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, response.StatusCode);
                    CodeIndexStatus status = await JsonHelper.DeserializeAsync<CodeIndexStatus>(response).ConfigureAwait(false);

                    string dataDirectory = Path.GetFullPath(fx.Settings.DataDirectory) + Path.DirectorySeparatorChar;
                    AssertTrue(Path.GetFullPath(status.IndexDirectory).StartsWith(dataDirectory, StringComparison.Ordinal),
                        "the code index directory '" + status.IndexDirectory + "' must be under the test server's data directory '"
                        + dataDirectory + "'");
                }
                finally
                {
                    fx.Stop();
                }
            }));

            cases.Add(CaseAsync("settings_hot_reload_reads_only_this_servers_settings_file",
                "SettingsHotReload_ReadsOnlyThisServersSettingsFile", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.StartIsolatedAsync(settings => { }).ConfigureAwait(false);
                try
                {
                    AssertTrue(fx.SettingsFilePath != ArmadaSettings.DefaultSettingsPath,
                        "a test server must not be bound to the machine-wide settings file");
                    AssertEqual(100, fx.Settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages);

                    // Write this server's own settings file. The watcher must apply it, which is the same
                    // path by which an unrelated file would reach the server if the path were not scoped.
                    ArmadaSettings edited = new ArmadaSettings();
                    edited.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 4;
                    await edited.SaveAsync(fx.SettingsFilePath).ConfigureAwait(false);

                    int applied = await WaitForLimitAsync(fx, 4).ConfigureAwait(false);
                    AssertEqual(4, applied);
                }
                finally
                {
                    fx.Stop();
                }
            }));

            cases.Add(CaseAsync("cancelling_active_work_frees_fleet_capacity",
                "CancellingActiveWork_FreesFleetCapacity", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.StartIsolatedAsync(settings =>
                {
                    settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 2;
                    settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = 2;
                }).ConfigureAwait(false);
                try
                {
                    string vesselId = await CreateVesselAsync(fx.AuthClient).ConfigureAwait(false);

                    await CreateMissionAsync(fx.AuthClient, vesselId, "Residue1", HttpStatusCode.Created).ConfigureAwait(false);
                    await CreateMissionAsync(fx.AuthClient, vesselId, "Residue2", HttpStatusCode.Created).ConfigureAwait(false);

                    // The residue of the two previous cases now fills the fleet, exactly as a suite that
                    // never cleans up leaves it for whichever case runs next.
                    string refusal = await CreateMissionAsync(fx.AuthClient, vesselId, "Residue3", HttpStatusCode.Conflict).ConfigureAwait(false);
                    AssertTrue(refusal.Contains("fleet_capacity_reached", StringComparison.Ordinal),
                        "a full fleet refuses with fleet_capacity_reached: " + refusal);

                    await fx.CancelActiveWorkAsync().ConfigureAwait(false);

                    await CreateMissionAsync(fx.AuthClient, vesselId, "AfterCleanup", HttpStatusCode.Created).ConfigureAwait(false);
                }
                finally
                {
                    fx.Stop();
                }
            }));

            cases.Add(CaseAsync("refused_tool_result_fails_naming_the_refusal",
                "RefusedToolResult_FailsNamingTheRefusal", TestTags.Negative, () =>
            {
                string refusal = "{\"Error\":\"Fleet capacity is full: 7 active work unit(s), limit 7.\","
                    + "\"Code\":\"fleet_capacity_reached\",\"ActiveCount\":7,\"Limit\":7}";

                string message = "";
                try
                {
                    McpToolResults.RequireSuccess("armada_create_mission", refusal);
                }
                catch (AssertionException ex)
                {
                    message = ex.Message;
                }

                AssertTrue(message.Contains("armada_create_mission", StringComparison.Ordinal),
                    "the failure names the tool that was refused: " + message);
                AssertTrue(message.Contains("fleet_capacity_reached", StringComparison.Ordinal),
                    "the failure carries the refusal code: " + message);
                AssertTrue(message.Contains("Fleet capacity is full", StringComparison.Ordinal),
                    "the failure carries the refusal message: " + message);

                // A normal payload passes through untouched, so the guard costs nothing on the happy path.
                string payload = "{\"Id\":\"msn_abc\",\"Title\":\"Mission\"}";
                AssertEqual(payload, McpToolResults.RequireSuccess("armada_create_mission", payload));
                return Task.CompletedTask;
            }));

            return new TestSuiteDescriptor(SuiteId, "Harness isolation", cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<int> WaitForLimitAsync(E2EServerFixture fixture, int expected)
        {
            // The watcher debounces for 750ms, so poll rather than sleeping a fixed interval.
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            int observed = fixture.Settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages;
            while (DateTime.UtcNow < deadline)
            {
                observed = fixture.Settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages;
                if (observed == expected) return observed;
                await Task.Delay(100).ConfigureAwait(false);
            }

            return observed;
        }

        private static async Task<string> CreateVesselAsync(HttpClient client)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            HttpResponseMessage fleetResponse = await client.PostAsync("/api/v1/fleets",
                JsonHelper.ToJsonContent(new { Name = "HarnessIsolationFleet-" + suffix })).ConfigureAwait(false);
            Fleet fleet = await JsonHelper.DeserializeAsync<Fleet>(fleetResponse).ConfigureAwait(false);

            HttpResponseMessage vesselResponse = await client.PostAsync("/api/v1/vessels",
                JsonHelper.ToJsonContent(new
                {
                    Name = "HarnessIsolationVessel-" + suffix,
                    RepoUrl = TestRepoHelper.GetLocalBareRepoUrl(),
                    FleetId = fleet.Id
                })).ConfigureAwait(false);
            Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(vesselResponse).ConfigureAwait(false);
            return vessel.Id;
        }

        private static async Task<string> CreateMissionAsync(HttpClient client, string vesselId, string title, HttpStatusCode expected)
        {
            HttpResponseMessage response = await client.PostAsync("/api/v1/missions",
                JsonHelper.ToJsonContent(new { Title = title, VesselId = vesselId })).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            AssertEqual(expected, response.StatusCode);
            return body;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
