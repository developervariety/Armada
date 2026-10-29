namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Routes;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// A vessel's git status is reused for a short time: a second list refresh inside the lifetime reads no git, a
    /// failed read is reused for a shorter time, callers that ask while a read runs share it, and each vessel is read
    /// on its own, so a refresh reads only the vessels it asks for.
    /// </summary>
    public class VesselGitStatusCacheTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Vessel Git Status Cache";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("A second read inside the lifetime reuses the status and reads no git", async () =>
            {
                ReadCounter reads = new ReadCounter();
                ManualClock clock = new ManualClock();
                VesselGitStatusCache cache = new VesselGitStatusCache(reads.ReadAsync, clock);
                Vessel vessel = NewVessel("vsl_one");

                VesselGitStatus first = await cache.GetAsync(vessel).ConfigureAwait(false);
                clock.Advance(VesselGitStatusCache.DefaultSuccessLifetime - TimeSpan.FromSeconds(1));
                VesselGitStatus second = await cache.GetAsync(vessel).ConfigureAwait(false);

                AssertEqual(1, reads.TotalReads, "git was read once");
                AssertEqual(3, first.CommitsAhead ?? -1, "The status carries the commits ahead");
                AssertEqual(first.CommitsAhead, second.CommitsAhead, "The second read returns the held status");
                AssertEqual("vsl_one", second.VesselId, "The status names its vessel");
            });

            await RunTest("A read after the lifetime reads git again", async () =>
            {
                ReadCounter reads = new ReadCounter();
                ManualClock clock = new ManualClock();
                VesselGitStatusCache cache = new VesselGitStatusCache(reads.ReadAsync, clock);
                Vessel vessel = NewVessel("vsl_one");

                await cache.GetAsync(vessel).ConfigureAwait(false);
                clock.Advance(VesselGitStatusCache.DefaultSuccessLifetime + TimeSpan.FromSeconds(1));
                await cache.GetAsync(vessel).ConfigureAwait(false);

                AssertEqual(2, reads.TotalReads, "an expired status is read again");
            });

            await RunTest("A failed status is reused for the shorter error lifetime only", async () =>
            {
                ReadCounter reads = new ReadCounter { Fail = true };
                ManualClock clock = new ManualClock();
                VesselGitStatusCache cache = new VesselGitStatusCache(reads.ReadAsync, clock);
                Vessel vessel = NewVessel("vsl_one");

                VesselGitStatus failed = await cache.GetAsync(vessel).ConfigureAwait(false);
                AssertNotNull(failed.Error, "the failure is reported");
                clock.Advance(VesselGitStatusCache.DefaultErrorLifetime - TimeSpan.FromSeconds(1));
                await cache.GetAsync(vessel).ConfigureAwait(false);
                AssertEqual(1, reads.TotalReads, "a failure is reused inside the error lifetime");

                reads.Fail = false;
                clock.Advance(TimeSpan.FromSeconds(2));
                VesselGitStatus recovered = await cache.GetAsync(vessel).ConfigureAwait(false);
                AssertEqual(2, reads.TotalReads, "a failure is read again after the error lifetime, long before the success lifetime");
                AssertNull(recovered.Error, "the repaired checkout reports its status");
            });

            await RunTest("A reader that throws becomes an error status instead of failing the caller", async () =>
            {
                VesselGitStatusCache cache = new VesselGitStatusCache((id, dir, branch) => throw new InvalidOperationException("git is missing"));
                VesselGitStatus status = await cache.GetAsync(NewVessel("vsl_one")).ConfigureAwait(false);
                AssertEqual("Git error: git is missing", status.Error, "The failure is named");
                AssertEqual("vsl_one", status.VesselId, "The status names its vessel");
            });

            await RunTest("Each vessel is read on its own and only the vessels asked for are read", async () =>
            {
                ReadCounter reads = new ReadCounter();
                VesselGitStatusCache cache = new VesselGitStatusCache(reads.ReadAsync, new ManualClock());

                await cache.GetAsync(NewVessel("vsl_one")).ConfigureAwait(false);
                await cache.GetAsync(NewVessel("vsl_two")).ConfigureAwait(false);
                await cache.GetAsync(NewVessel("vsl_one")).ConfigureAwait(false);
                await cache.GetAsync(NewVessel("vsl_two")).ConfigureAwait(false);

                AssertEqual("vsl_one,vsl_two", String.Join(",", reads.VesselsRead.OrderBy(id => id, StringComparer.Ordinal)), "Only the two requested vessels were read, once each");
            });

            await RunTest("A changed working directory or default branch is not served from the old status", async () =>
            {
                ReadCounter reads = new ReadCounter();
                VesselGitStatusCache cache = new VesselGitStatusCache(reads.ReadAsync, new ManualClock());
                Vessel vessel = NewVessel("vsl_one");

                await cache.GetAsync(vessel).ConfigureAwait(false);
                vessel.DefaultBranch = "develop";
                await cache.GetAsync(vessel).ConfigureAwait(false);
                vessel.WorkingDirectory = "/work/elsewhere";
                await cache.GetAsync(vessel).ConfigureAwait(false);

                AssertEqual(3, reads.TotalReads, "each checkout and branch is read for itself");
                AssertEqual("main,develop,develop", String.Join(",", reads.Branches), "The reader receives the vessel's default branch");
            });

            await RunTest("Callers that ask while a read runs share it, and one caller leaving does not fail the others", async () =>
            {
                TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int reads = 0;
                VesselGitStatusCache cache = new VesselGitStatusCache(async (id, dir, branch) =>
                {
                    Interlocked.Increment(ref reads);
                    await release.Task.ConfigureAwait(false);
                    return new VesselGitStatus { VesselId = id, CommitsAhead = 1, CommitsBehind = 2 };
                });
                Vessel vessel = NewVessel("vsl_one");

                using (CancellationTokenSource leaving = new CancellationTokenSource())
                {
                    Task<VesselGitStatus> leaver = cache.GetAsync(vessel, leaving.Token);
                    Task<VesselGitStatus> stayer = cache.GetAsync(vessel);
                    leaving.Cancel();
                    await AssertThrowsAsync<OperationCanceledException>(() => leaver);

                    release.SetResult(true);
                    VesselGitStatus status = await stayer.ConfigureAwait(false);
                    AssertEqual(2, status.CommitsBehind ?? -1, "The remaining caller gets the status");
                    AssertEqual(1, Volatile.Read(ref reads), "Both callers shared one read");
                }
            });

            await RunTest("The git-status route reads git once for repeated requests and never for a vessel nobody asks about", async () =>
            {
                string directory = Path.Combine(Path.GetTempPath(), "armada-git-status-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    ReadCounter reads = new ReadCounter();
                    using (RouteHost host = await RouteHost.StartAsync(new VesselGitStatusCache(reads.ReadAsync, new ManualClock())).ConfigureAwait(false))
                    {
                        Vessel asked = await host.Database.Driver.Vessels.CreateAsync(new Vessel { Name = "asked", RepoUrl = "https://example.test/asked.git", WorkingDirectory = directory, DefaultBranch = "main" }).ConfigureAwait(false);
                        Vessel idle = await host.Database.Driver.Vessels.CreateAsync(new Vessel { Name = "idle", RepoUrl = "https://example.test/idle.git", WorkingDirectory = directory, DefaultBranch = "main" }).ConfigureAwait(false);
                        Vessel unconfigured = await host.Database.Driver.Vessels.CreateAsync(new Vessel { Name = "unconfigured", RepoUrl = "https://example.test/none.git", DefaultBranch = "main" }).ConfigureAwait(false);

                        for (int refresh = 0; refresh < 3; refresh++)
                        {
                            HttpResponseMessage response = await host.Client.GetAsync("api/v1/vessels/" + asked.Id + "/git-status").ConfigureAwait(false);
                            AssertEqual(200, (int)response.StatusCode, "status code");
                            using (JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)))
                            {
                                AssertEqual(asked.Id, Property(body.RootElement, "vesselId").GetString(), "vessel id");
                                AssertEqual(3, Property(body.RootElement, "commitsAhead").GetInt32(), "commits ahead");
                                AssertEqual(0, Property(body.RootElement, "commitsBehind").GetInt32(), "commits behind");
                                AssertEqual(JsonValueKind.Undefined, Property(body.RootElement, "error").ValueKind, "a status that was read carries no error");
                            }
                        }

                        HttpResponseMessage missing = await host.Client.GetAsync("api/v1/vessels/vsl_missing/git-status").ConfigureAwait(false);
                        AssertEqual(404, (int)missing.StatusCode, "an unknown vessel is not found");

                        HttpResponseMessage noDirectory = await host.Client.GetAsync("api/v1/vessels/" + unconfigured.Id + "/git-status").ConfigureAwait(false);
                        using (JsonDocument body = JsonDocument.Parse(await noDirectory.Content.ReadAsStringAsync().ConfigureAwait(false)))
                            AssertEqual("No working directory configured or directory does not exist", Property(body.RootElement, "error").GetString(), "a vessel without a checkout reports why");

                        AssertEqual(asked.Id, String.Join(",", reads.VesselsRead), "git was read once, for the one vessel that was asked about, and not at all for the others");
                        AssertEqual(1, reads.TotalReads, "three requests inside the lifetime read git once");
                        AssertNotNull(idle.Id, "the idle vessel exists and was never requested");
                    }
                }
                finally
                {
                    Directory.Delete(directory, true);
                }
            });
        }

        // The test server names properties as the route serializer does; match without regard to case.
        private static JsonElement Property(JsonElement element, string name)
        {
            foreach (JsonProperty property in element.EnumerateObject())
                if (String.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
            return default;
        }

        private static Vessel NewVessel(string id)
        {
            return new Vessel { Id = id, Name = id, WorkingDirectory = "/work/" + id, DefaultBranch = "main" };
        }

        private sealed class RouteHost : IDisposable
        {
            private readonly Webserver _Server;
            private readonly CancellationTokenSource _Cancellation;

            public TestDatabase Database { get; }

            public HttpClient Client { get; }

            private RouteHost(TestDatabase database, Webserver server, CancellationTokenSource cancellation, HttpClient client)
            {
                Database = database;
                _Server = server;
                _Cancellation = cancellation;
                Client = client;
            }

            public static async Task<RouteHost> StartAsync(VesselGitStatusCache cache)
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                try
                {
                    string apiKey = "git-status-key-" + Guid.NewGuid().ToString("N");
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.ApiKey = apiKey;
                    JsonSerializerOptions jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                    AuthenticationService authentication = new AuthenticationService(database.Driver, new SessionTokenService(), settings, logging);
                    WorkflowProfileService profiles = new WorkflowProfileService(database.Driver, logging);
                    VesselRoutes vessels = new VesselRoutes(
                        database.Driver,
                        new VesselReadinessService(database.Driver, profiles, logging),
                        new LandingPreviewService(database.Driver, logging),
                        (_, _, _, _, _, _, _, _) => Task.CompletedTask,
                        jsonOptions,
                        gitStatus: cache);

                    int port;
                    using (TcpListener listener = new TcpListener(IPAddress.Loopback, 0))
                    {
                        listener.Start();
                        port = ((IPEndPoint)listener.LocalEndpoint).Port;
                        listener.Stop();
                    }

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
                    vessels.Register(server, authenticate, new AuthorizationService());

                    CancellationTokenSource cancellation = new CancellationTokenSource();
                    server.Start(cancellation.Token);
                    HttpClient client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port + "/"), Timeout = TimeSpan.FromSeconds(10) };
                    client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
                    return new RouteHost(database, server, cancellation, client);
                }
                catch
                {
                    database.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                Client.Dispose();
                _Cancellation.Cancel();
                try { _Server.Stop(); }
                catch (Exception exception) { Console.WriteLine("Git status route test server stop failed: " + exception.Message); }
                _Server.Dispose();
                _Cancellation.Dispose();
                Database.Dispose();
            }
        }

        private sealed class ReadCounter
        {
            private readonly ConcurrentQueue<string> _Vessels = new ConcurrentQueue<string>();
            private readonly ConcurrentQueue<string> _Branches = new ConcurrentQueue<string>();

            public bool Fail { get; set; } = false;

            public int TotalReads => _Vessels.Count;

            public List<string> VesselsRead => _Vessels.Distinct().ToList();

            public List<string> Branches => _Branches.ToList();

            public Task<VesselGitStatus> ReadAsync(string vesselId, string workingDirectory, string baseBranch)
            {
                _Vessels.Enqueue(vesselId);
                _Branches.Enqueue(baseBranch);
                if (Fail) return Task.FromResult(new VesselGitStatus { VesselId = vesselId, Error = "Git error: offline" });
                return Task.FromResult(new VesselGitStatus { VesselId = vesselId, CommitsAhead = 3, CommitsBehind = 0 });
            }
        }

        private sealed class ManualClock : TimeProvider
        {
            private DateTimeOffset _Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

            public void Advance(TimeSpan by)
            {
                _Now += by;
            }

            public override DateTimeOffset GetUtcNow()
            {
                return _Now;
            }
        }
    }
}
