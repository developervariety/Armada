namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Harbor job control lists a Harbor's recorded jobs one page at a time and stops one by ID: a running job on a
    /// connected Harbor is stopped there, an ended job is left alone, a disconnected Harbor is reported, and another
    /// tenant's Harbor is invisible.
    /// </summary>
    public sealed class HarborJobControlSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.HarborJobControl";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the suite descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("lists_one_page_newest_first", "Jobs are listed one page at a time, newest launch first, and openOnly hides ended jobs", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HarborService harbors = new HarborService(testDb.Driver, Logging());
                    Harbor harbor = await testDb.Driver.Harbors.CreateAsync(new Harbor { Id = "hbr_list", Name = "list" }).ConfigureAwait(false);
                    DateTime now = DateTime.UtcNow;
                    for (int i = 0; i < 5; i++)
                    {
                        await testDb.Driver.HarborJobs.CreateAsync(new HarborJobRecord
                        {
                            JobId = "job-" + i, HarborId = harbor.Id, Runtime = "ClaudeCode", LaunchedUtc = now.AddMinutes(-10 + i),
                            EndedUtc = i < 2 ? now.AddMinutes(-1) : (DateTime?)null,
                            Outcome = i < 2 ? HarborJobOutcomeEnum.Succeeded : HarborJobOutcomeEnum.Running
                        }).ConfigureAwait(false);
                    }

                    HarborJobControlService control = new HarborJobControlService(harbors, testDb.Driver, NewManager(harbors));
                    AuthContext admin = AuthContext.Authenticated("default", "default", true, true, "ApiKey");

                    EnumerationResult<HarborJobRecord>? first = await control.EnumerateJobsAsync(admin, harbor.Id, false, new EnumerationQuery { PageNumber = 1, PageSize = 2 }).ConfigureAwait(false);
                    AssertNotNull(first);
                    AssertEqual(5L, first!.TotalRecords);
                    AssertEqual(3, first.TotalPages);
                    AssertEqual("job-4,job-3", String.Join(",", first.Objects.Select(j => j.JobId)), "newest launch first");

                    EnumerationResult<HarborJobRecord>? last = await control.EnumerateJobsAsync(admin, harbor.Id, false, new EnumerationQuery { PageNumber = 3, PageSize = 2 }).ConfigureAwait(false);
                    AssertEqual("job-0", String.Join(",", last!.Objects.Select(j => j.JobId)), "the last page holds the remainder");

                    EnumerationResult<HarborJobRecord>? open = await control.EnumerateJobsAsync(admin, harbor.Id, true, new EnumerationQuery { PageSize = 10 }).ConfigureAwait(false);
                    AssertEqual(3L, open!.TotalRecords, "openOnly lists only jobs that have not ended");

                    AuthContext stranger = AuthContext.Authenticated("other-tenant", "other-user", false, false, "ApiKey");
                    await testDb.Driver.Harbors.UpdateAsync(SetTenant(harbor, "default")).ConfigureAwait(false);
                    AssertNull(await control.EnumerateJobsAsync(stranger, harbor.Id, false, new EnumerationQuery()).ConfigureAwait(false), "another tenant's Harbor is not visible");
                }
            }));

            cases.Add(CaseAsync("stop_outcomes", "Stop sends to a connected Harbor, leaves an ended job alone, and reports a disconnected Harbor or an unknown job", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HarborService harbors = new HarborService(testDb.Driver, Logging());
                    HarborConnectionManager manager = NewManager(harbors);
                    HarborJobControlService control = new HarborJobControlService(harbors, testDb.Driver, manager);
                    AuthContext admin = AuthContext.Authenticated("default", "default", true, true, "ApiKey");

                    ControlledHarborJobRunner runner = new ControlledHarborJobRunner();
                    HarborJobState state = new HarborJobState();
                    LoopbackHarborSession session = await LoopbackHarborSession.ConnectAsync(manager, "hbr_stop", NewClient("hbr_stop", runner, state)).ConfigureAwait(false);
                    await manager.LaunchAsync("hbr_stop", new HarborLaunchRequest { JobId = "job-live", Runtime = "ClaudeCode", WorkingDirectory = "/w" }, new RecordingHarborJobListener()).ConfigureAwait(false);
                    await WaitUntilAsync(() => runner.Launched("job-live")).ConfigureAwait(false);
                    runner.Start("job-live", 4200);

                    await testDb.Driver.HarborJobs.CreateAsync(new HarborJobRecord { JobId = "job-live", HarborId = "hbr_stop", Runtime = "ClaudeCode" }).ConfigureAwait(false);
                    await testDb.Driver.HarborJobs.CreateAsync(new HarborJobRecord { JobId = "job-done", HarborId = "hbr_stop", Runtime = "ClaudeCode", EndedUtc = DateTime.UtcNow, Outcome = HarborJobOutcomeEnum.Succeeded }).ConfigureAwait(false);

                    HarborJobStopResult live = await control.StopJobAsync(admin, "hbr_stop", "job-live").ConfigureAwait(false);
                    AssertEqual(HarborJobStopStatusEnum.Requested, live.Status, live.Message);
                    AssertTrue(live.Job!.StopRequested, "the record notes the stop");
                    await WaitUntilAsync(() => runner.Stopped.Contains("job-live")).ConfigureAwait(false);

                    HarborJobStopResult done = await control.StopJobAsync(admin, "hbr_stop", "job-done").ConfigureAwait(false);
                    AssertEqual(HarborJobStopStatusEnum.AlreadyEnded, done.Status);
                    AssertFalse(runner.Stopped.Contains("job-done"), "nothing is sent for an ended job");

                    HarborJobStopResult unknown = await control.StopJobAsync(admin, "hbr_stop", "job-missing").ConfigureAwait(false);
                    AssertEqual(HarborJobStopStatusEnum.NotFound, unknown.Status);

                    await session.DropAsync().ConfigureAwait(false);
                    await testDb.Driver.HarborJobs.CreateAsync(new HarborJobRecord { JobId = "job-orphan", HarborId = "hbr_stop", Runtime = "ClaudeCode" }).ConfigureAwait(false);
                    HarborJobStopResult offline = await control.StopJobAsync(admin, "hbr_stop", "job-orphan").ConfigureAwait(false);
                    AssertEqual(HarborJobStopStatusEnum.HarborNotConnected, offline.Status);
                }
            }));

            return new TestSuiteDescriptor(SuiteId, "Harbor job control", cases);
        }

        #endregion

        #region Private-Methods

        private static Harbor SetTenant(Harbor harbor, string tenantId)
        {
            harbor.TenantId = tenantId;
            return harbor;
        }

        private static HarborLinkClient NewClient(string harborId, IHarborJobRunner runner, HarborJobState jobs)
        {
            HarborLinkClient client = new HarborLinkClient(harborId, "Rig", new List<HarborCapability> { new HarborCapability { Name = "ClaudeCode" } }, 4,
                new StubHostCommandExecutor(new HostCommandResult { ExitCode = 0 }), Logging(), 0, null, runner);
            client.JobState = jobs;
            return client;
        }

        private static HarborConnectionManager NewManager(HarborService harbors)
        {
            HarborConnectionManager manager = new HarborConnectionManager(harbors, Logging(), null);
            manager.SweepInterval = TimeSpan.Zero;
            return manager;
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            MonotonicDeadline deadline = MonotonicDeadline.After(TimeSpan.FromSeconds(10));
            while (!condition())
            {
                if (deadline.Passed) throw new TimeoutException("condition not met within 10 s");
                await Task.Delay(10).ConfigureAwait(false);
            }
        }

        private static LoggingModule Logging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body());
        }

        #endregion
    }
}
