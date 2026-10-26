namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Exercises saved mission actions at the actual REST boundary.</summary>
    public sealed class FleetActionApiSuite : IArmadaTestSuite
    {
        private const string SuiteId = "E2E.FleetActionApi";

        /// <summary>Build the owner, body, run replay and cancellation contract case.</summary>
        public TestSuiteDescriptor Build()
        {
            return new TestSuiteDescriptor(SuiteId, "Fleet Action API", new List<TestCaseDescriptor>
            {
                new TestCaseDescriptor(SuiteId, "owned_mission_actions_and_runs", "Owned mission actions preserve run identity and reject scoped operators",
                    async (CancellationToken _) => await VerifyApiAsync().ConfigureAwait(false), new List<string> { TestTags.Positive })
            });
        }

        private static async Task VerifyApiAsync()
        {
            E2EServerFixture fx = await E2EServerFixture.StartIsolatedAsync(settings => { }).ConfigureAwait(false);
            try
            {
                using (HttpResponseMessage denied = await fx.UnauthClient.GetAsync("/api/v1/fleet-actions").ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.Unauthorized, denied.StatusCode, "anonymous callers cannot read saved mission actions");
                using (HttpClient tenantAdmin = await AskThreadApiTestClients.CreateBearerUserAsync(fx, true).ConfigureAwait(false))
                using (HttpClient ordinary = await AskThreadApiTestClients.CreateBearerUserAsync(fx, false).ConfigureAwait(false))
                {
                    foreach (HttpClient client in new[] { tenantAdmin, ordinary })
                    {
                        using (HttpResponseMessage denied = await client.GetAsync("/api/v1/fleet-actions").ConfigureAwait(false))
                            AssertEqual(HttpStatusCode.Forbidden, denied.StatusCode, "tenant administrators and users cannot read operator actions");
                        using (HttpResponseMessage denied = await client.PostAsync("/api/v1/fleet-actions", JsonHelper.ToJsonContent(new { name = "Denied" })).ConfigureAwait(false))
                            AssertEqual(HttpStatusCode.Forbidden, denied.StatusCode, "operator authorization precedes request validation");
                    }
                }

                SessionTokenService tokens = new SessionTokenService(fx.Settings.SessionTokenEncryptionKey);
                string missionToken = tokens.CreateMissionToken(Constants.DefaultTenantId, Constants.DefaultUserId, "msn_action_scope").Token!;
                using (HttpClient mission = AskThreadApiTestClients.CreateBearerClient(fx.BaseUrl, missionToken))
                using (HttpResponseMessage denied = await mission.GetAsync("/api/v1/fleet-action-runs").ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.Forbidden, denied.StatusCode, "a scoped admin mission cannot use operator action routes");

                FleetAction action;
                using (HttpResponseMessage created = await fx.AuthClient.PostAsync("/api/v1/fleet-actions", JsonHelper.ToJsonContent(new
                {
                    name = "API Mission Action", promptTemplate = "Inspect {vesselName} ({vesselId}).", concurrency = 1, active = true
                })).ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.OK, created.StatusCode, "camel-case action fields are accepted");
                    action = await JsonHelper.DeserializeAsync<FleetAction>(created).ConfigureAwait(false);
                    AssertEqual("API Mission Action", action.Name, "the definition preserves the submitted name");
                }
                using (HttpClient otherAdmin = await AskThreadApiTestClients.CreateOtherAdminAsync(fx).ConfigureAwait(false))
                using (HttpResponseMessage hidden = await otherAdmin.GetAsync("/api/v1/fleet-actions/" + action.Id).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.NotFound, hidden.StatusCode, "another global administrator cannot read this owner's definition");

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                Vessel vessel;
                using (DatabaseDriver database = DatabaseDriverFactory.Create(fx.Settings.Database, logging))
                {
                    Pipeline pipeline = await database.Pipelines.ReadByNameAsync("FullPipeline").ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The integration fixture must seed its normal full pipeline.");
                    vessel = await database.Vessels.CreateAsync(new Vessel
                    {
                        Name = "Action Target", RepoUrl = fx.TempDir, TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId, DefaultPipelineId = pipeline.Id
                    }).ConfigureAwait(false);
                }

                string requestId = Guid.NewGuid().ToString("N");
                FleetActionRun run = await StartRunAsync(fx.AuthClient, action.Id, requestId, vessel.Id).ConfigureAwait(false);
                AssertEqual(1, run.Snapshot.Targets.Count, "the run freezes the selected target");
                AssertEqual(vessel.Id, run.Snapshot.Targets[0].VesselId, "the exact vessel is saved");
                AssertContains(vessel.Name, run.Snapshot.Targets[0].RenderedPrompt, "the saved prompt renders the target name");
                FleetActionRun replay = await StartRunAsync(fx.AuthClient, action.Id, requestId, vessel.Id).ConfigureAwait(false);
                AssertEqual(run.Id, replay.Id, "the same request returns the original run");
                using (HttpResponseMessage conflict = await fx.AuthClient.PostAsync("/api/v1/fleet-actions/" + action.Id + "/run",
                    JsonHelper.ToJsonContent(new { requestId, vesselIds = new[] { vessel.Id, "vsl_changed_input" } })).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.Conflict, conflict.StatusCode, "changed input cannot reuse an accepted request identity");
                using (HttpResponseMessage cancelled = await fx.AuthClient.PostAsync("/api/v1/fleet-action-runs/" + run.Id + "/cancel", JsonHelper.ToJsonContent(new { })).ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.OK, cancelled.StatusCode, "run cancellation is recorded through the owned route");
                    FleetActionRun current = await JsonHelper.DeserializeAsync<FleetActionRun>(cancelled).ConfigureAwait(false);
                    AssertEqual(run.Id, current.Id, "cancellation keeps the exact run identity");
                }
                using (HttpResponseMessage deleted = await fx.AuthClient.DeleteAsync("/api/v1/fleet-actions/" + action.Id).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.OK, deleted.StatusCode, "the definition can be removed");
                using (HttpResponseMessage retained = await fx.AuthClient.GetAsync("/api/v1/fleet-action-runs/" + run.Id).ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.OK, retained.StatusCode, "definition deletion preserves the run");
                    FleetActionRun saved = await JsonHelper.DeserializeAsync<FleetActionRun>(retained).ConfigureAwait(false);
                    AssertEqual(action.Name, saved.Snapshot.ActionName, "the run retains its immutable definition snapshot");
                }
            }
            finally
            {
                fx.Stop();
            }
        }

        private static async Task<FleetActionRun> StartRunAsync(HttpClient client, string actionId, string requestId, string vesselId)
        {
            using (HttpResponseMessage response = await client.PostAsync("/api/v1/fleet-actions/" + actionId + "/run",
                JsonHelper.ToJsonContent(new { requestId, vesselIds = new[] { vesselId } })).ConfigureAwait(false))
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode, "a saved run is accepted: " + body);
                return JsonHelper.Deserialize<FleetActionRun>(body);
            }
        }
    }
}
