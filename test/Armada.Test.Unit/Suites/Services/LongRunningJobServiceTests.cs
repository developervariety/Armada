namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Concurrent;
    using System.Reflection;
    using System.Threading;
    using Armada.Server;
    using Armada.Test.Common;

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
