namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server.Routes;
    using Armada.Test.Common;

    /// <summary>Behavior tests for the in-memory Harbor host sample ring and its authorized read.</summary>
    public sealed class HarborHostMetricsTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Harbor Host Metrics";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Samples_AreBoundedByCountAndAge", () =>
            {
                DateTime now = Utc(2026, 3, 1, 0, 0, 0);
                HarborHostMetricsBuffer buffer = Buffer(() => now, maximumSamples: 3, maximumAge: TimeSpan.FromMinutes(10), staleAfter: TimeSpan.FromMinutes(5));
                for (int index = 1; index <= 5; index++)
                {
                    now = Utc(2026, 3, 1, 0, 0, 0).AddSeconds(index * 10);
                    AssertTrue(buffer.Record("host-a", new HarborHostSample { CpuPercent = index, ActiveJobs = index }), "sample " + index + " is stored");
                }
                HarborHostMetricsSnapshot bounded = buffer.Read("host-a");
                AssertEqual(HarborHostMetricsSnapshot.StateCurrent, bounded.State, "a fresh ring is current");
                AssertEqual("3,4,5", Cpus(bounded), "the ring keeps the newest samples");

                now = Utc(2026, 3, 1, 0, 20, 0);
                HarborHostMetricsSnapshot expired = buffer.Read("host-a");
                AssertEqual(HarborHostMetricsSnapshot.StateNoData, expired.State, "samples past the age cap are gone");
                AssertEqual(0, expired.Points.Count, "an expired ring has no points");
                AssertTrue(expired.StaleSinceUtc == null, "an expired ring is not a stale zero");
            });

            await RunTest("StaleSample_IsAGapNotAZero", () =>
            {
                DateTime now = Utc(2026, 3, 1, 0, 0, 0);
                HarborHostMetricsBuffer buffer = Buffer(() => now, staleAfter: TimeSpan.FromSeconds(45));
                AssertTrue(buffer.Record("host-a", new HarborHostSample { CpuPercent = 40, ActiveJobs = 2, ActiveCaptains = 1 }), "the real sample is stored");
                now = Utc(2026, 3, 1, 0, 2, 0);
                HarborHostMetricsSnapshot stale = buffer.Read("host-a");
                AssertEqual(HarborHostMetricsSnapshot.StateStale, stale.State, "a late sample is stale");
                AssertEqual(Utc(2026, 3, 1, 0, 0, 0), stale.StaleSinceUtc, "stale since the last real sample");
                AssertEqual(40d, stale.Points[0].CpuPercent, "the last real cpu stays");
                HarborHostMetricPoint gap = stale.Points[stale.Points.Count - 1];
                AssertTrue(gap.Missing, "the series ends in a gap");
                AssertTrue(gap.CpuPercent == null, "the gap cpu is null");
                AssertTrue(gap.ActiveJobs == null, "the gap job count is null");
                AssertTrue(gap.ActiveCaptains == null, "the gap captain count is null");

                now = Utc(2026, 3, 1, 0, 3, 0);
                AssertTrue(buffer.Record("host-a", new HarborHostSample { CpuPercent = 55, ActiveJobs = 1 }), "a later sample is stored");
                HarborHostMetricsSnapshot resumed = buffer.Read("host-a");
                AssertEqual(HarborHostMetricsSnapshot.StateCurrent, resumed.State, "a new sample is current");
                AssertTrue(resumed.Points.Exists(point => point.Missing && point.CpuPercent == null), "the hole stays a gap");
                AssertEqual("40,55", Cpus(resumed), "the hole is not filled with zero");
            });

            await RunTest("ReportedZero_StaysZero", () =>
            {
                DateTime now = Utc(2026, 3, 1, 0, 0, 0);
                HarborHostMetricsBuffer buffer = Buffer(() => now);
                AssertTrue(buffer.Record("host-a", new HarborHostSample { CpuPercent = 0, ActiveJobs = 0, ActiveCaptains = 0 }), "a measured zero is stored");
                HarborHostMetricsSnapshot current = buffer.Read("host-a");
                AssertEqual(HarborHostMetricsSnapshot.StateCurrent, current.State, "a measured zero is current");
                AssertEqual(0d, current.Points[0].CpuPercent, "cpu zero is the measurement");
                AssertEqual(0, current.Points[0].ActiveJobs, "job zero is the measurement");
                AssertTrue(buffer.Record("host-a", new HarborHostSample { CpuPercent = 250, ActiveJobs = 2 }), "an out-of-range cpu does not reject the rest");
                HarborHostMetricsSnapshot sanitized = buffer.Read("host-a");
                HarborHostMetricPoint latest = sanitized.Points[sanitized.Points.Count - 1];
                AssertTrue(latest.CpuPercent == null, "an out-of-range cpu is absent");
                AssertEqual(2, latest.ActiveJobs, "the job count remains");
            });

            await RunTest("OlderHarbor_WithoutMetrics_IsNoData", () =>
            {
                DateTime now = Utc(2026, 3, 1, 0, 0, 0);
                HarborHostMetricsBuffer buffer = Buffer(() => now);
                HarborHeartbeat older = (HarborHeartbeat)HarborProtocol.Deserialize("{\"type\":\"heartbeat\",\"liveJobIds\":[\"job-a\"]}");
                AssertTrue(!buffer.Record("host-a", older.Host), "an omitted host sample is not stored");
                HarborHeartbeat empty = (HarborHeartbeat)HarborProtocol.Deserialize("{\"type\":\"heartbeat\",\"liveJobIds\":[],\"host\":{}}");
                AssertTrue(!buffer.Record("host-a", empty.Host), "an empty host object is not stored");
                HarborHostMetricsSnapshot snapshot = buffer.Read("host-a");
                AssertEqual(HarborHostMetricsSnapshot.StateNoData, snapshot.State, "no sample is no-data");
                AssertEqual(0, snapshot.Points.Count, "no-data has no points");
                AssertTrue(snapshot.StaleSinceUtc == null, "no-data has no stale time");
            });

            await RunTest("Probe_OmitsUnreadableCounters", () =>
            {
                AssertTrue(HarborHostProbe.CpuPercent(null, "cpu 1 0 1 10 0 0 0 0") == null, "the first cpu line has no percent");
                AssertTrue(HarborHostProbe.CpuPercent("cpu 1 0 1 10", null) == null, "a missing later cpu line has no percent");
                double? busy = HarborHostProbe.CpuPercent("cpu 100 0 100 1000 0 0 0 0", "cpu 200 0 100 1100 0 0 0 0");
                AssertEqual(50d, busy, "a measured cpu delta is the percent");

                HarborHostSample memory = new HarborHostSample();
                HarborHostProbe.ReadMemory(memory, "MemTotal: 1000 kB\n");
                AssertTrue(memory.MemoryTotalBytes == null, "memory without available stays unset");
                AssertTrue(memory.MemoryUsedBytes == null, "missing available memory is not zero used");
                HarborHostProbe.ReadMemory(memory, "MemTotal: 1000 kB\nMemAvailable: 1000 kB\n");
                AssertEqual(0L, memory.MemoryUsedBytes, "a fully free memory reading is zero");

                HarborHostSample disk = new HarborHostSample();
                HarborHostProbe.ReadDisk(disk, null, 10);
                AssertTrue(disk.DiskUsedBytes == null, "a missing disk size stays unset");
                HarborHostProbe.ReadDisk(disk, 1000, 1000);
                AssertEqual(0L, disk.DiskUsedBytes, "a fully free disk reading is zero");
            });

            await RunTest("HiddenRunner_IsNotFound_AndOwnerReadsNoData", async () =>
            {
                MemoryEnrollments enrollments = new MemoryEnrollments();
                enrollments.Rows["host-a"] = Enrollment("host-a");
                enrollments.Rows["host-b"] = Enrollment("host-b");
                DateTime now = Utc(2026, 3, 1, 0, 0, 0);
                HarborHostMetricsBuffer buffer = Buffer(() => now);
                buffer.Record("host-a", new HarborHostSample { CpuPercent = 41.5, ActiveJobs = 1 });
                HarborHostMetricsService service = new HarborHostMetricsService(buffer, enrollments, new MemoryAuthority());
                AuthContext owner = AuthContext.Authenticated("tenant-a", "owner-user", false, false, "bearer");
                AuthContext other = AuthContext.Authenticated("tenant-a", "other-user", false, false, "bearer");

                AssertTrue(await service.ReadAsync(other, "host-a").ConfigureAwait(false) == null, "another caller cannot read the series");
                HarborHostMetricsSnapshot? empty = await service.ReadAsync(owner, "host-b").ConfigureAwait(false);
                AssertTrue(empty != null, "the owner can read a runner that has sent no sample");
                AssertEqual(HarborHostMetricsSnapshot.StateNoData, empty!.State, "a runner with no sample is no-data");
                HarborHostMetricsSnapshot? visible = await service.ReadAsync(owner, "host-a").ConfigureAwait(false);
                AssertEqual(41.5, visible!.Points[0].CpuPercent, "the owner reads the stored cpu");
            });

            await RunTest("AnonymousMetricsRoute_IsUnauthorized", async () =>
            {
                MemoryEnrollments enrollments = new MemoryEnrollments();
                enrollments.Rows["host-a"] = Enrollment("host-a");
                DateTime now = Utc(2026, 3, 1, 0, 0, 0);
                HarborHostMetricsBuffer buffer = Buffer(() => now);
                buffer.Record("host-a", new HarborHostSample { CpuPercent = 41.5 });
                await using (MetricsServer server = await MetricsServer.StartAsync(buffer, enrollments).ConfigureAwait(false))
                {
                    HttpReply anonymous = await server.GetAsync(null, "host-a").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Unauthorized, anonymous.Status, "an anonymous caller is refused");
                    AssertTrue(!anonymous.Body.Contains("41.5", StringComparison.Ordinal), "a refusal does not include the sample");

                    HttpReply other = await server.GetAsync("other", "host-a").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NotFound, other.Status, "another caller reads the host as unknown");
                    AssertTrue(!other.Body.Contains("41.5", StringComparison.Ordinal), "a hidden series is not returned");

                    HttpReply owner = await server.GetAsync("owner", "host-b").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NotFound, owner.Status, "an unenrolled runner is unknown");
                }
            });
        }

        private static HarborHostMetricsBuffer Buffer(Func<DateTime> clock, int? maximumSamples = null, TimeSpan? maximumAge = null, TimeSpan? staleAfter = null)
        {
            return new HarborHostMetricsBuffer(clock, maximumSamples, maximumAge, staleAfter);
        }

        private static DateTime Utc(int year, int month, int day, int hour, int minute, int second)
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }

        private static string Cpus(HarborHostMetricsSnapshot snapshot)
        {
            List<string> values = new List<string>();
            foreach (HarborHostMetricPoint point in snapshot.Points)
            {
                if (!point.Missing && point.CpuPercent.HasValue) values.Add(point.CpuPercent.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return String.Join(",", values);
        }

        private static HarborRunnerEnrollment Enrollment(string runnerId)
        {
            return new HarborRunnerEnrollment
            {
                RunnerId = runnerId,
                TenantId = "tenant-a",
                UserId = "owner-user",
                AuthMethod = "bearer",
                Active = true
            };
        }

        private sealed class MemoryEnrollments : IHarborRunnerEnrollmentMethods
        {
            public Dictionary<string, HarborRunnerEnrollment> Rows { get; } = new Dictionary<string, HarborRunnerEnrollment>(StringComparer.Ordinal);

            public Task<HarborRunnerEnrollment?> ReadAsync(string runnerId, CancellationToken token = default)
            {
                Rows.TryGetValue(runnerId, out HarborRunnerEnrollment? enrollment);
                return Task.FromResult(enrollment);
            }

            public Task<bool> TryEnrollAsync(HarborRunnerEnrollment enrollment, long expectedGeneration, CancellationToken token = default)
            {
                throw new NotSupportedException();
            }

            public Task<bool> TryRevokeAsync(string runnerId, long expectedGeneration, string revokedByUserId, DateTime revokedUtc, CancellationToken token = default)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class MemoryAuthority : IHarborRunnerAuthority
        {
            public Task<bool> HasAuthorityOverOwnerAsync(AuthContext caller, string ownerTenantId, string ownerUserId, CancellationToken token = default)
            {
                return Task.FromResult(caller != null && caller.IsAuthenticated && caller.IsAdmin);
            }
        }

        private sealed class HttpReply
        {
            public HttpReply(HttpStatusCode status, string body)
            {
                Status = status;
                Body = body;
            }

            public HttpStatusCode Status { get; }
            public string Body { get; }
        }

        private sealed class MetricsServer : IAsyncDisposable
        {
            private readonly Webserver _Server;
            private readonly CancellationTokenSource _Cancellation;
            private readonly HttpClient _Http;

            private MetricsServer(Webserver server, CancellationTokenSource cancellation, HttpClient http)
            {
                _Server = server;
                _Cancellation = cancellation;
                _Http = http;
            }

            public static Task<MetricsServer> StartAsync(HarborHostMetricsBuffer buffer, MemoryEnrollments enrollments)
            {
                int port;
                using (TcpListener listener = new TcpListener(IPAddress.Loopback, 0))
                {
                    listener.Start();
                    port = ((IPEndPoint)listener.LocalEndpoint).Port;
                }
                WebserverSettings settings = new WebserverSettings();
                settings.Hostname = "127.0.0.1";
                settings.Port = port;
                Webserver server = new Webserver(settings, async (HttpContextBase context) =>
                {
                    context.Response.StatusCode = 404;
                    await context.Response.Send().ConfigureAwait(false);
                });
                Func<HttpContextBase, Task<AuthContext>> authenticate = context =>
                {
                    string header = context.Request.Headers.Get("Authorization") ?? String.Empty;
                    if (header.EndsWith(" owner", StringComparison.Ordinal))
                        return Task.FromResult(AuthContext.Authenticated("tenant-a", "owner-user", false, false, "bearer"));
                    if (header.EndsWith(" other", StringComparison.Ordinal))
                        return Task.FromResult(AuthContext.Authenticated("tenant-a", "other-user", false, false, "bearer"));
                    return Task.FromResult(new AuthContext());
                };
                new HarborHostMetricsRoutes(new HarborHostMetricsService(buffer, enrollments, new MemoryAuthority()))
                    .Register(server, authenticate, new AuthorizationService());
                CancellationTokenSource cancellation = new CancellationTokenSource();
                server.Start(cancellation.Token);
                HttpClient http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port + "/") };
                return Task.FromResult(new MetricsServer(server, cancellation, http));
            }

            public async Task<HttpReply> GetAsync(string? caller, string runnerId)
            {
                using (HttpRequestMessage request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, "api/v1/harbor-runners/" + runnerId + "/metrics"))
                {
                    if (caller != null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + caller);
                    using (HttpResponseMessage response = await _Http.SendAsync(request).ConfigureAwait(false))
                    {
                        return new HttpReply(response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    }
                }
            }

            public ValueTask DisposeAsync()
            {
                _Cancellation.Cancel();
                try { _Server.Stop(); } catch (Exception) { }
                _Server.Dispose();
                _Http.Dispose();
                _Cancellation.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
