namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp.Tools;
    using Armada.Server.Routes;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    /// <summary>
    /// Mission list surfaces read the summary projection and never return a mission's heavy fields
    /// (description, diff snapshot, agent output), so a page of missions stays small however large
    /// each mission's persisted payload grew. The REST list and enumerate routes and the MCP
    /// enumerate tool are separate entry points to that rule, each driven here against a database
    /// that refuses full-row mission reads. The mission detail route returns the description and
    /// still withholds the diff snapshot and agent output.
    /// </summary>
    public class MissionListProjectionTests : TestSuite
    {
        private const string _DescriptionMarker = "DESCRIPTION-MARKER";
        private const string _DiffMarker = "DIFF-SNAPSHOT-MARKER";
        private const string _OutputMarker = "AGENT-OUTPUT-MARKER";

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>Suite name.</summary>
        public override string Name => "Mission List Projection";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("REST mission list and enumerate read summaries and return no heavy fields", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission heavy = await CreateHeavyMissionAsync(testDb).ConfigureAwait(false);
                    SummaryOnlyMissionMethods missions = SummaryOnlyMissionMethods.Install(testDb.Driver);

                    using (MissionRouteHost host = MissionRouteHost.Start(testDb))
                    {
                        HttpResponseMessage listResponse = await host.Client.GetAsync("/api/v1/missions").ConfigureAwait(false);
                        string listBody = await listResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.OK, listResponse.StatusCode, "GET /api/v1/missions: " + listBody);
                        AssertSummaryPage(listBody, heavy, "GET /api/v1/missions");

                        HttpResponseMessage enumerateResponse = await host.Client.PostAsync(
                            "/api/v1/missions/enumerate",
                            new StringContent("{}", Encoding.UTF8, "application/json")).ConfigureAwait(false);
                        string enumerateBody = await enumerateResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.OK, enumerateResponse.StatusCode, "POST /api/v1/missions/enumerate: " + enumerateBody);
                        AssertSummaryPage(enumerateBody, heavy, "POST /api/v1/missions/enumerate");
                    }

                    AssertEqual(0, missions.RefusedCalls.Count,
                        "the list routes must not hydrate full mission rows: " + String.Join(", ", missions.RefusedCalls));
                    AssertEqual(2, missions.SummaryCalls, "each list route must read the summary projection once");
                }
            });

            await RunTest("REST mission detail returns the description and withholds diff snapshot and agent output", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission heavy = await CreateHeavyMissionAsync(testDb).ConfigureAwait(false);

                    using (MissionRouteHost host = MissionRouteHost.Start(testDb))
                    {
                        HttpResponseMessage response = await host.Client.GetAsync("/api/v1/missions/" + heavy.Id).ConfigureAwait(false);
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.OK, response.StatusCode, "GET /api/v1/missions/{id}: " + body);

                        Mission? detail = JsonSerializer.Deserialize<Mission>(body, _JsonOptions);
                        AssertNotNull(detail, "the detail response must be a mission");
                        AssertEqual(heavy.Id, detail!.Id, "detail id");
                        AssertContains(_DescriptionMarker, detail.Description ?? String.Empty, "the detail view keeps the description");
                        AssertNull(detail.DiffSnapshot, "the detail view must not return the diff snapshot");
                        AssertNull(detail.AgentOutput, "the detail view must not return persisted agent output");
                        AssertFalse(body.Contains(_DiffMarker, StringComparison.Ordinal), "no diff snapshot text in the detail body");
                        AssertFalse(body.Contains(_OutputMarker, StringComparison.Ordinal), "no agent output text in the detail body");
                    }
                }
            });

            await RunTest("MCP armada_enumerate missions reads summaries and returns no heavy fields", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission heavy = await CreateHeavyMissionAsync(testDb).ConfigureAwait(false);
                    SummaryOnlyMissionMethods missions = SummaryOnlyMissionMethods.Install(testDb.Driver);

                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                    McpEnumerateTools.Register((name, _, _, handler) => { handlers[name] = handler; }, testDb.Driver);

                    foreach (string entityType in new[] { "missions", "mission" })
                    {
                        object page = await handlers["armada_enumerate"](Args("{\"entityType\":\"" + entityType + "\",\"pageSize\":10}")).ConfigureAwait(false);
                        string json = JsonSerializer.Serialize(page);
                        AssertContains(heavy.Id, json, entityType + ": the mission row is present");
                        AssertContains("Heavy projection mission", json, entityType + ": the mission title is present");
                        AssertFalse(json.Contains(_DescriptionMarker, StringComparison.Ordinal), entityType + ": no description text");
                        AssertFalse(json.Contains(_DiffMarker, StringComparison.Ordinal), entityType + ": no diff snapshot text");
                        AssertFalse(json.Contains(_OutputMarker, StringComparison.Ordinal), entityType + ": no agent output text");
                        AssertTrue(json.Length < 10_000, entityType + ": a one-mission page stays small (was " + json.Length + " chars)");
                    }

                    AssertEqual(0, missions.RefusedCalls.Count,
                        "MCP mission enumeration must not hydrate full mission rows: " + String.Join(", ", missions.RefusedCalls));
                    AssertEqual(2, missions.SummaryCalls, "each MCP mission enumeration must read the summary projection once");
                }
            });
        }

        private void AssertSummaryPage(string body, Mission heavy, string surface)
        {
            EnumerationResult<Mission>? page = JsonSerializer.Deserialize<EnumerationResult<Mission>>(body, _JsonOptions);
            AssertNotNull(page, surface + " must return a mission page");
            AssertEqual(1, page!.Objects.Count, surface + " returns the seeded mission");
            Mission summary = page.Objects[0];
            AssertEqual(heavy.Id, summary.Id, surface + " mission id");
            AssertEqual("Heavy projection mission", summary.Title, surface + " mission title");
            AssertEqual(MissionStatusEnum.WorkProduced, summary.Status, surface + " mission status");
            AssertNull(summary.Description, surface + " must not return the description");
            AssertNull(summary.DiffSnapshot, surface + " must not return the diff snapshot");
            AssertNull(summary.AgentOutput, surface + " must not return agent output");
            AssertFalse(body.Contains(_DescriptionMarker, StringComparison.Ordinal), surface + ": no description text");
            AssertFalse(body.Contains(_DiffMarker, StringComparison.Ordinal), surface + ": no diff snapshot text");
            AssertFalse(body.Contains(_OutputMarker, StringComparison.Ordinal), surface + ": no agent output text");
        }

        private static async Task<Mission> CreateHeavyMissionAsync(TestDatabase testDb)
        {
            Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("projection-vessel", "https://github.com/test/repo.git")
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId
            }).ConfigureAwait(false);

            string filler = new string('x', 64 * 1024);
            Mission mission = new Mission("Heavy projection mission")
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId,
                VesselId = vessel.Id,
                Status = MissionStatusEnum.WorkProduced,
                Description = _DescriptionMarker + filler,
                DiffSnapshot = _DiffMarker + filler,
                AgentOutput = _OutputMarker + filler
            };
            return await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static JsonElement Args(string json)
        {
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                return doc.RootElement.Clone();
            }
        }

        /// <summary>
        /// A loopback webserver with only <see cref="MissionRoutes"/> registered, an API key that
        /// authenticates as an administrator, and an <see cref="HttpClient"/> bound to it.
        /// </summary>
        private sealed class MissionRouteHost : IDisposable
        {
            private readonly Webserver _Server;
            private readonly CancellationTokenSource _Cancellation;

            /// <summary>Client bound to this host, sending the administrator API key.</summary>
            public HttpClient Client { get; }

            private MissionRouteHost(Webserver server, CancellationTokenSource cancellation, HttpClient client)
            {
                _Server = server;
                _Cancellation = cancellation;
                Client = client;
            }

            /// <summary>Start a host serving the mission routes over the test database.</summary>
            /// <param name="database">Test database.</param>
            /// <returns>The running host.</returns>
            public static MissionRouteHost Start(TestDatabase database)
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;

                string apiKey = "projection-key-" + Guid.NewGuid().ToString("N");
                ArmadaSettings settings = new ArmadaSettings();
                settings.ApiKey = apiKey;
                settings.DocksDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_projection_docks_" + Guid.NewGuid().ToString("N"));
                settings.ReposDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_projection_repos_" + Guid.NewGuid().ToString("N"));

                StubGitService git = new StubGitService();
                IDockService docks = new DockService(logging, database.Driver, settings, git);
                ICaptainService captains = new CaptainService(logging, database.Driver, settings, git, docks);
                MissionService missionService = new MissionService(logging, database.Driver, settings, docks, captains,
                    resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                IAdmiralService admiral = RecordingAdmiral.Create().Service;

                WorkflowProfileService workflowProfiles = new WorkflowProfileService(database.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(database.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(database.Driver, workflowProfiles, readiness, logging);
                DeploymentEnvironmentService environments = new DeploymentEnvironmentService(database.Driver, workflowProfiles, logging);
                DeploymentService deployments = new DeploymentService(database.Driver, workflowProfiles, environments, checkRuns, logging);
                GitHubIntegrationService gitHub = new GitHubIntegrationService(
                    database.Driver, new ObjectiveService(database.Driver), checkRuns, deployments, settings, logging);

                Func<string, string, string?, string?, string?, string?, string?, string?, Task> emitEvent =
                    (_, _, _, _, _, _, _, _) => Task.CompletedTask;
                MissionStatusTransitionService transitions = new MissionStatusTransitionService(
                    database.Driver, admiral, missionService, git,
                    (_, _) => Task.FromResult(false),
                    (_, _) => Task.CompletedTask,
                    emitEvent, logging);

                AuthenticationService authentication = new AuthenticationService(database.Driver, new SessionTokenService(), settings, logging);

                int port = ReservePort();
                WebserverSettings webserverSettings = new WebserverSettings();
                webserverSettings.Hostname = "127.0.0.1";
                webserverSettings.Port = port;
                Webserver server = new Webserver(webserverSettings, async (HttpContextBase ctx) =>
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.Send().ConfigureAwait(false);
                });

                Func<HttpContextBase, Task<AuthContext>> authenticate = ctx => authentication.AuthenticateAsync(
                    ctx.Request.Headers.Get("Authorization"),
                    ctx.Request.Headers.Get("X-Token"),
                    ctx.Request.Headers.Get("X-Api-Key"));
                MissionRoutes routes = new MissionRoutes(
                    database.Driver, admiral, missionService, settings, git,
                    new LandingService(logging, database.Driver, settings, git),
                    new LandingPreviewService(database.Driver, logging, settings),
                    gitHub, emitEvent, null, logging, _JsonOptions, transitions);
                routes.Register(server, authenticate, new AuthorizationService());

                CancellationTokenSource cancellation = new CancellationTokenSource();
                server.Start(cancellation.Token);
                HttpClient client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port + "/"), Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
                return new MissionRouteHost(server, cancellation, client);
            }

            /// <summary>Dispose the host.</summary>
            public void Dispose()
            {
                Client.Dispose();
                _Cancellation.Cancel();
                try { _Server.Stop(); }
                catch (Exception exception) { Console.WriteLine("Mission route test server stop failed: " + exception.Message); }
                _Server.Dispose();
                _Cancellation.Dispose();
            }

            private static int ReservePort()
            {
                using (TcpListener listener = new TcpListener(IPAddress.Loopback, 0))
                {
                    listener.Start();
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    listener.Stop();
                    return port;
                }
            }
        }
    }
}
