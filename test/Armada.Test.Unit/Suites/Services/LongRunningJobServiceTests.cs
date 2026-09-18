namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Reflection;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Background jobs: stale reaping, and the journal that makes an accepted job survive the process
    /// that accepted it. A restart records an unfinished job as Lost with its reason instead of
    /// forgetting it, the loss is reported on the job's objective, and the dispatch hold lists the
    /// jobs a restart would lose.
    /// </summary>
    public class LongRunningJobServiceTests : TestSuite
    {
        public override string Name => "Long Running Job Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("ReapStaleJobsAsync fails a stale in-flight job past the threshold", () =>
            {
                // Start schedules background execution via Task.Run immediately, so the Accepted state is
                // transient and the job settles into Running. Backdate both anchors so the reap fails the
                // job deterministically whichever in-flight state it is read in.
                LongRunningJobService service = new LongRunningJobService();
                LongRunningJob job = service.Start("stale-accepted", token => HangForeverAsync(token));
                Thread.Sleep(30);
                BackdateJob(service, job.JobId, TimeSpan.FromMinutes(40), backdateSubmitted: true, backdateStarted: true);

                int reaped = service.ReapStaleJobsAsync(staleMinutes: 30, token: CancellationToken.None).GetAwaiter().GetResult();

                AssertEqual(1, reaped, "one stale in-flight job must be reaped");
                AssertTrue(service.TryGetStatus(job.JobId, out LongRunningJob? after), "the reaped job must remain queryable");
                AssertEqual(LongRunningJobStatusEnum.Failed, after!.Status, "the reaped job must be Failed");
                AssertContains("reaped as stale", after.FailureMessage ?? String.Empty);
                return Task.CompletedTask;
            });

            await RunTest("ReapStaleJobsAsync fails a Running job past the threshold", () =>
            {
                LongRunningJobService service = new LongRunningJobService();
                LongRunningJob job = service.Start("stale-running", token => HangForeverAsync(token));

                // Wait for background execution to begin so the job moves to Running.
                Thread.Sleep(30);
                AssertTrue(service.TryGetStatus(job.JobId, out LongRunningJob? running), "the job must be queryable");
                AssertEqual(LongRunningJobStatusEnum.Running, running!.Status, "the job must be Running once execution begins");

                BackdateJob(service, job.JobId, TimeSpan.FromMinutes(40), backdateSubmitted: false, backdateStarted: true);

                int reaped = service.ReapStaleJobsAsync(staleMinutes: 30, token: CancellationToken.None).GetAwaiter().GetResult();

                AssertEqual(1, reaped, "one stale running job must be reaped");
                AssertTrue(service.TryGetStatus(job.JobId, out LongRunningJob? after), "the reaped job must remain queryable");
                AssertEqual(LongRunningJobStatusEnum.Failed, after!.Status, "the reaped job must be Failed");
                return Task.CompletedTask;
            });

            await RunTest("ReapStaleJobsAsync leaves a fresh Accepted job alone", () =>
            {
                LongRunningJobService service = new LongRunningJobService();
                service.Start("fresh-accepted", token => HangForeverAsync(token));

                int reaped = service.ReapStaleJobsAsync(staleMinutes: 60, token: CancellationToken.None).GetAwaiter().GetResult();

                AssertEqual(0, reaped, "a fresh accepted job must not be reaped");
                return Task.CompletedTask;
            });

            await RunTest("ReapStaleJobsAsync leaves a Succeeded job alone", () =>
            {
                LongRunningJobService service = new LongRunningJobService();
                LongRunningJob job = service.Start("quick", token => Task.FromResult<object?>("done"));

                Thread.Sleep(100);
                AssertTrue(service.TryGetStatus(job.JobId, out LongRunningJob? terminal), "the quick job must be queryable");

                int reaped = service.ReapStaleJobsAsync(staleMinutes: 0, token: CancellationToken.None).GetAwaiter().GetResult();

                AssertEqual(0, reaped, "a terminal job must never be reaped");
                AssertEqual(LongRunningJobStatusEnum.Succeeded, terminal!.Status);
                return Task.CompletedTask;
            });

            await RunTest("ReapStaleJobsAsync clamps staleMinutes to a minimum of one", () =>
            {
                LongRunningJobService service = new LongRunningJobService();
                LongRunningJob job = service.Start("stale-clamp", token => HangForeverAsync(token));
                Thread.Sleep(30);
                BackdateJob(service, job.JobId, TimeSpan.FromMinutes(40), backdateSubmitted: true, backdateStarted: true);

                int reaped = service.ReapStaleJobsAsync(staleMinutes: -5, token: CancellationToken.None).GetAwaiter().GetResult();

                AssertEqual(1, reaped, "a negative stale window must clamp to 1 minute and still reap an old job");
                return Task.CompletedTask;
            });

            await RunTest("An accepted job whose process stops reads Lost after the restart, names its objective, and is reported once", async () =>
            {
                string journal = NewJournalDirectory();
                TaskCompletionSource<object?> never = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

                // The process that accepts the job and then stops before the job finishes.
                LongRunningJobService accepting = new LongRunningJobService(journalDirectory: journal);
                LongRunningJob accepted = accepting.Start("voyage_dispatch", _ => never.Task, "obj_example", "vsl_example");
                AssertEqual(LongRunningJobStatusEnum.Accepted, accepted.Status);

                // The next process on the same data directory.
                List<LongRunningJob> reported = new List<LongRunningJob>();
                LongRunningJobService restarted = new LongRunningJobService(
                    journalDirectory: journal,
                    onJobFailedAsync: job => { reported.Add(job); return Task.CompletedTask; });
                AssertTrue(restarted.TryGetStatus(accepted.JobId, out LongRunningJob? beforeRecovery),
                    "the journal answers for the job even before recovery runs, so it is never job_not_found");
                AssertTrue(beforeRecovery!.Status == LongRunningJobStatusEnum.Accepted || beforeRecovery.Status == LongRunningJobStatusEnum.Running);

                int lost = await restarted.RecoverInterruptedJobsAsync();
                AssertEqual(1, lost, "the unfinished job is recorded as lost");
                AssertTrue(restarted.TryGetStatus(accepted.JobId, out LongRunningJob? after), "the lost job stays readable");
                AssertEqual(LongRunningJobStatusEnum.Lost, after!.Status);
                AssertContains("job_lost_on_restart", after.FailureMessage ?? "", "the reason is named");
                AssertEqual("obj_example", after.ObjectiveId);
                AssertEqual("vsl_example", after.VesselId);
                AssertNotNull(after.CompletedAtUtc, "a lost job has an end time");
                AssertEqual(1, reported.Count, "the loss is reported exactly once");
                AssertEqual(accepted.JobId, reported[0].JobId);
                AssertEqual(LongRunningJobStatusEnum.Lost, reported[0].Status);
                AssertEqual(0, restarted.ListUnfinished().Count, "a lost job is not unfinished work");

                // A second restart does not report the same loss again.
                List<LongRunningJob> reportedAgain = new List<LongRunningJob>();
                LongRunningJobService restartedAgain = new LongRunningJobService(
                    journalDirectory: journal,
                    onJobFailedAsync: job => { reportedAgain.Add(job); return Task.CompletedTask; });
                AssertEqual(0, await restartedAgain.RecoverInterruptedJobsAsync(), "a recorded loss is not lost twice");
                AssertEqual(0, reportedAgain.Count);
                AssertTrue(restartedAgain.TryGetStatus(accepted.JobId, out LongRunningJob? third));
                AssertEqual(LongRunningJobStatusEnum.Lost, third!.Status);
                never.TrySetResult(null);
            });

            await RunTest("A finished job stays readable after a restart and after memory eviction", async () =>
            {
                string journal = NewJournalDirectory();
                LongRunningJobService first = new LongRunningJobService(maxRetainedTerminalJobs: 1, journalDirectory: journal);
                LongRunningJob a = first.Start("probe", _ => Task.FromResult<object?>(new { Value = 7 }));
                await WaitTerminalAsync(first, a.JobId);
                LongRunningJob b = first.Start("probe", _ => Task.FromResult<object?>(new { Value = 8 }));
                await WaitTerminalAsync(first, b.JobId);

                AssertTrue(first.TryGetStatus(a.JobId, out LongRunningJob? evicted), "a job evicted from memory is read from the journal");
                AssertEqual(LongRunningJobStatusEnum.Succeeded, evicted!.Status);

                LongRunningJobService second = new LongRunningJobService(journalDirectory: journal);
                AssertEqual(0, await second.RecoverInterruptedJobsAsync(), "a finished job is not lost");
                AssertTrue(second.TryGetStatus(b.JobId, out LongRunningJob? read), "a finished job is readable after a restart");
                AssertEqual(LongRunningJobStatusEnum.Succeeded, read!.Status);
                AssertContains("8", JsonSerializer.Serialize(read.Result), "the result survives");
            });

            await RunTest("A failed job is reported with its objective", async () =>
            {
                string journal = NewJournalDirectory();
                TaskCompletionSource<LongRunningJob> reported = new TaskCompletionSource<LongRunningJob>(TaskCreationOptions.RunContinuationsAsynchronously);
                LongRunningJobService jobs = new LongRunningJobService(
                    journalDirectory: journal,
                    onJobFailedAsync: job => { reported.TrySetResult(job); return Task.CompletedTask; });
                LongRunningJob accepted = jobs.Start("voyage_dispatch", _ => throw new InvalidOperationException("refused in the background"), "obj_failed");
                Task finished = await Task.WhenAny(reported.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                AssertTrue(finished == reported.Task, "a failed job is reported");
                LongRunningJob job = await reported.Task;
                AssertEqual(accepted.JobId, job.JobId);
                AssertEqual(LongRunningJobStatusEnum.Failed, job.Status);
                AssertEqual("obj_failed", job.ObjectiveId);
                AssertContains("refused in the background", job.FailureMessage ?? "");
            });

            await RunTest("A job that cannot be journalled is refused, not accepted into memory", () =>
            {
                string journal = NewJournalDirectory();
                LongRunningJobService jobs = new LongRunningJobService(journalDirectory: journal);
                Directory.Delete(journal, true);
                File.WriteAllText(journal, "not a directory");
                bool ran = false;
                InvalidOperationException? refused = null;
                try
                {
                    jobs.Start("voyage_dispatch", _ => { ran = true; return Task.FromResult<object?>(null); });
                }
                catch (InvalidOperationException ex)
                {
                    refused = ex;
                }
                AssertNotNull(refused, "an unjournalled job must be refused");
                AssertContains("job_journal_unavailable", refused!.Message);
                AssertFalse(ran, "a refused job never runs");
                AssertEqual(0, jobs.ListUnfinished().Count, "a refused job is not tracked");
                File.Delete(journal);
                return Task.CompletedTask;
            });

            await RunTest("Hold status and engage list the jobs accepted but not finished", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    DispatchHold hold = new DispatchHold();
                    LongRunningJobService jobs = new LongRunningJobService(journalDirectory: NewJournalDirectory());
                    TaskCompletionSource<object?> gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    LongRunningJob pending = jobs.Start("voyage_dispatch", _ => gate.Task, "obj_pending", "vsl_pending");

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpCoordinationTools.Register(
                        (name, _, _, h) => { if (name == "armada_dispatch_hold") handler = h; },
                        testDb.Driver,
                        new CoordinationService(logging, testDb.Driver),
                        hold,
                        null,
                        jobs);
                    AssertNotNull(handler);

                    string engaged = JsonSerializer.Serialize(await handler!(JsonSerializer.SerializeToElement(new { action = "engage", reason = "redeploy", setBy = "session-a" })));
                    AssertContains(pending.JobId, engaged, "engage names the unfinished job the restart would lose");
                    AssertContains("obj_pending", engaged);

                    string status = JsonSerializer.Serialize(await handler(JsonSerializer.SerializeToElement(new { action = "status" })));
                    AssertContains(pending.JobId, status, "status names the unfinished job");

                    gate.TrySetResult(null);
                    await WaitTerminalAsync(jobs, pending.JobId);
                    string drained = JsonSerializer.Serialize(await handler(JsonSerializer.SerializeToElement(new { action = "status" })));
                    AssertFalse(drained.Contains(pending.JobId, StringComparison.Ordinal), "a finished job leaves the list");
                    AssertContains("\"UnfinishedJobs\":[]", drained);
                }
            });

            await RunTest("A restarted admiral records a lost dispatch job on its objective and armada_job_status reads Lost", async () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "armada_job_restart_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                DatabaseSettings dbSettings = new DatabaseSettings
                {
                    Type = DatabaseTypeEnum.Sqlite,
                    Filename = Path.Combine(tempDir, "armada.db")
                };
                ArmadaSettings settings = new ArmadaSettings
                {
                    DataDirectory = tempDir,
                    DatabasePath = dbSettings.Filename,
                    Database = dbSettings,
                    LogDirectory = Path.Combine(tempDir, "logs"),
                    DocksDirectory = Path.Combine(tempDir, "docks"),
                    ReposDirectory = Path.Combine(tempDir, "repos"),
                    AdmiralPort = FreePort(),
                    McpPort = FreePort(),
                    ApiKey = "test-key-" + Guid.NewGuid().ToString("N"),
                    HeartbeatIntervalSeconds = 300
                };
                settings.Rest.Hostname = "127.0.0.1";
                settings.AutonomousObjectiveScheduler.Enabled = false;
                settings.SettingsFilePath = Path.Combine(tempDir, "settings.json");
                settings.InitializeDirectories();

                Objective objective;
                using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(dbSettings))
                {
                    objective = await driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Title = "Dispatched just before a restart",
                        Status = ObjectiveStatusEnum.Scoped
                    });
                }

                // The previous admiral process accepted a dispatch and died before the job ran: all that is
                // left of it is the record in the data directory's job journal.
                string jobId = "job_" + Guid.NewGuid().ToString("N").Substring(0, 20);
                Directory.CreateDirectory(Path.Combine(tempDir, "jobs"));
                File.WriteAllText(Path.Combine(tempDir, "jobs", jobId + ".json"), JsonSerializer.Serialize(new
                {
                    JobId = jobId,
                    Operation = "voyage_dispatch",
                    Status = "Accepted",
                    SubmittedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    ObjectiveId = objective.Id,
                    VesselId = "vsl_example"
                }));

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                ArmadaServer server = new ArmadaServer(logging, settings, quiet: true);
                try
                {
                    await server.StartAsync();

                    string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"armada_job_status\",\"arguments\":{\"jobId\":\"" + jobId + "\"}}}";
                    string answer;
                    using (HttpClient client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + settings.McpPort), Timeout = TimeSpan.FromSeconds(30) })
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/mcp"))
                    {
                        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                        request.Headers.Add("Accept", "application/json, text/event-stream");
                        request.Headers.Add("X-Api-Key", settings.ApiKey);
                        using (HttpResponseMessage response = await client.SendAsync(request))
                            answer = await response.Content.ReadAsStringAsync();
                    }
                    AssertFalse(answer.Contains("job_not_found", StringComparison.Ordinal), "an accepted job is never job_not_found after a restart: " + answer);
                    AssertContains("Lost", answer, "armada_job_status reports the job Lost: " + answer);
                    AssertContains("job_lost_on_restart", answer, "and names why: " + answer);

                    using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(dbSettings))
                    {
                        List<ArmadaEvent> events = await driver.Events.EnumerateByEntityAsync("objective", objective.Id, 50);
                        List<ArmadaEvent> lostEvents = events.Where(e => e.EventType == "job.lost").ToList();
                        AssertEqual(1, lostEvents.Count, "the loss is one event on the objective; events: "
                            + String.Join(", ", events.Select(e => e.EventType)));
                        AssertContains(jobId, lostEvents[0].Message);
                        AssertContains("job_lost_on_restart", lostEvents[0].Message);
                    }
                }
                finally
                {
                    server.Stop();
                    try
                    {
                        Directory.Delete(tempDir, true);
                    }
                    catch (IOException)
                    {
                        // A stopped server can briefly hold log handles; the temporary directory is disposable.
                    }
                }
            });
        }

        private static string NewJournalDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "armada_job_journal_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static async Task WaitTerminalAsync(LongRunningJobService jobs, string jobId)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (jobs.TryGetStatus(jobId, out LongRunningJob? job) && job != null
                    && job.Status != LongRunningJobStatusEnum.Accepted && job.Status != LongRunningJobStatusEnum.Running)
                    return;
                await Task.Delay(10);
            }
            throw new TimeoutException("job " + jobId + " did not finish within 10 s");
        }

        private static int FreePort()
        {
            using (TcpListener listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
        }

        private static void BackdateJob(LongRunningJobService service, string jobId, TimeSpan age, bool backdateSubmitted, bool backdateStarted)
        {
            FieldInfo? field = typeof(LongRunningJobService).GetField("_Jobs", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("the _Jobs field must exist");
            ConcurrentDictionary<string, LongRunningJob>? jobs = field.GetValue(service) as ConcurrentDictionary<string, LongRunningJob>
                ?? throw new InvalidOperationException("the _Jobs field must be a ConcurrentDictionary");

            if (!jobs.TryGetValue(jobId, out LongRunningJob? tracked)) return;
            DateTime backdated = DateTime.UtcNow.Subtract(age);
            if (backdateStarted) tracked.StartedAtUtc = backdated;
            if (backdateSubmitted) tracked.SubmittedAtUtc = backdated;
            jobs[jobId] = tracked;
        }

        private static async Task<object?> HangForeverAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return null;
        }
    }
}
