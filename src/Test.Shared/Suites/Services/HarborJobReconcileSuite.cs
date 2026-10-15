namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// The Admiral and a Harbor agree on which jobs are alive after a link blip. A started job the Harbor stops listing is
    /// reported to its listener as failed once the grace for its replayed exit passes; a link silent for about three
    /// heartbeat intervals is dropped by the sweep (and a Harbor without heartbeats never is); and on reconnect the Harbor
    /// stops jobs the Admiral no longer owns, so two agents never work in one dock.
    /// </summary>
    public sealed class HarborJobReconcileSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.HarborJobReconcile";
        private static readonly DateTime _Start = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the suite descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("vanished_job_reported_after_grace", "A started job missing from the reconnect handshake is reported failed after the grace when no exit arrives", TestTags.Reliability, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HarborTestClock clock = new HarborTestClock(_Start);
                    HarborConnectionManager manager = NewManager(testDb, clock);
                    await manager.OnHandshakeAsync(Handshake("hbr_v", 15000), null, null, Recorder(new List<HarborMessage>())).ConfigureAwait(false);
                    RecordingHarborJobListener listener = new RecordingHarborJobListener();
                    await manager.LaunchAsync("hbr_v", Launch("job-v"), listener).ConfigureAwait(false);
                    await manager.OnMessageAsync("hbr_v", new HarborStarted { JobId = "job-v", ProcessId = 9 }).ConfigureAwait(false);
                    await manager.OnDisconnectedAsync("hbr_v").ConfigureAwait(false);

                    HarborHandshakeAck ack = await manager.OnHandshakeAsync(Handshake("hbr_v", 15000), null, null, Recorder(new List<HarborMessage>())).ConfigureAwait(false);
                    AssertTrue(ack.OwnedJobIds != null && ack.OwnedJobIds.Contains("job-v"), "the Admiral still owns the job it is waiting on");
                    clock.Advance(TimeSpan.FromSeconds(5));
                    await manager.SweepAsync().ConfigureAwait(false);
                    AssertNull(listener.Failure, "within the grace the exit may still be replayed");

                    clock.Advance(manager.VanishedJobGrace);
                    int acted = await manager.SweepAsync().ConfigureAwait(false);
                    AssertEqual(1, acted, "one job reported");
                    AssertNotNull(listener.Failure, "the vanished job is reported to its listener");
                    AssertFalse(manager.IsJobTracked("job-v"), "and no longer tracked");
                }
            }));

            cases.Add(CaseAsync("replayed_exit_within_grace_wins", "A job missing from a heartbeat whose exit arrives within the grace is reported by its exit, not as failed", TestTags.Reliability, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HarborTestClock clock = new HarborTestClock(_Start);
                    HarborConnectionManager manager = NewManager(testDb, clock);
                    await manager.OnHandshakeAsync(Handshake("hbr_e", 15000), null, null, Recorder(new List<HarborMessage>())).ConfigureAwait(false);
                    RecordingHarborJobListener listener = new RecordingHarborJobListener();
                    await manager.LaunchAsync("hbr_e", Launch("job-e"), listener).ConfigureAwait(false);
                    await manager.OnMessageAsync("hbr_e", new HarborStarted { JobId = "job-e", ProcessId = 9 }).ConfigureAwait(false);

                    await manager.OnMessageAsync("hbr_e", new HarborHeartbeat { LiveJobIds = new List<string>() }).ConfigureAwait(false);
                    clock.Advance(TimeSpan.FromSeconds(2));
                    await manager.OnMessageAsync("hbr_e", new HarborExited { JobId = "job-e", ExitCode = 0 }).ConfigureAwait(false);
                    clock.Advance(manager.VanishedJobGrace + TimeSpan.FromSeconds(1));
                    await manager.OnMessageAsync("hbr_e", new HarborHeartbeat { LiveJobIds = new List<string>() }).ConfigureAwait(false);
                    await manager.SweepAsync().ConfigureAwait(false);

                    AssertEqual(0, listener.ExitCode, "the exit was reported");
                    AssertNull(listener.Failure, "not reported as failed as well");
                }
            }));

            cases.Add(CaseAsync("heartbeat_without_job_reports_it_after_grace", "A heartbeat that stops listing a started job reports it failed on a later heartbeat after the grace", TestTags.Reliability, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HarborTestClock clock = new HarborTestClock(_Start);
                    HarborConnectionManager manager = NewManager(testDb, clock);
                    await manager.OnHandshakeAsync(Handshake("hbr_h", 15000), null, null, Recorder(new List<HarborMessage>())).ConfigureAwait(false);
                    RecordingHarborJobListener listener = new RecordingHarborJobListener();
                    RecordingHarborJobListener launching = new RecordingHarborJobListener();
                    await manager.LaunchAsync("hbr_h", Launch("job-h"), listener).ConfigureAwait(false);
                    await manager.LaunchAsync("hbr_h", Launch("job-not-started"), launching).ConfigureAwait(false);
                    await manager.OnMessageAsync("hbr_h", new HarborStarted { JobId = "job-h", ProcessId = 9 }).ConfigureAwait(false);

                    await manager.OnMessageAsync("hbr_h", new HarborHeartbeat { LiveJobIds = new List<string>() }).ConfigureAwait(false);
                    clock.Advance(manager.VanishedJobGrace + TimeSpan.FromSeconds(1));
                    await manager.OnMessageAsync("hbr_h", new HarborHeartbeat { LiveJobIds = new List<string>() }).ConfigureAwait(false);

                    AssertNotNull(listener.Failure, "the job the Harbor stopped listing is reported");
                    AssertNull(launching.Failure, "a job that has not reported its start is left to the start timeout");
                    AssertTrue(manager.IsJobTracked("job-not-started"));
                }
            }));

            cases.Add(CaseAsync("stale_link_is_dropped_by_sweep", "A link silent for three heartbeat intervals (at least 45 s) is dropped, its transport closed, and its waiting requests failed", TestTags.Reliability, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HarborTestClock clock = new HarborTestClock(_Start);
                    HarborConnectionManager manager = NewManager(testDb, clock);
                    int closed = 0;
                    await manager.OnHandshakeAsync(Handshake("hbr_s", 10000), null, null, Recorder(new List<HarborMessage>()), CancellationToken.None, () => closed++).ConfigureAwait(false);
                    Task<HarborGitResult?> pending = manager.SendGitAsync("hbr_s", new HarborGitRequest { RequestId = "g1", Executable = "git", WorkingDirectory = "/w", Arguments = new List<string> { "status" } }, 0);

                    clock.Advance(TimeSpan.FromSeconds(20));
                    await manager.OnMessageAsync("hbr_s", new HarborHeartbeat()).ConfigureAwait(false);
                    clock.Advance(TimeSpan.FromSeconds(44));
                    AssertEqual(0, await manager.SweepAsync().ConfigureAwait(false), "44 s of silence is within the 45 s minimum");
                    AssertTrue(manager.IsConnected("hbr_s"));

                    clock.Advance(TimeSpan.FromSeconds(2));
                    AssertEqual(1, await manager.SweepAsync().ConfigureAwait(false), "the dead link is dropped");
                    AssertFalse(manager.IsConnected("hbr_s"), "no longer connected");
                    AssertEqual(1, closed, "its transport was closed");
                    await AssertThrowsAsync<InvalidOperationException>(() => pending).ConfigureAwait(false);
                }
            }));

            cases.Add(CaseAsync("long_heartbeat_interval_scales_the_timeout", "The stale timeout is three of the Harbor's heartbeat intervals when that is above the minimum", TestTags.Reliability, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HarborTestClock clock = new HarborTestClock(_Start);
                    HarborConnectionManager manager = NewManager(testDb, clock);
                    await manager.OnHandshakeAsync(Handshake("hbr_slow", 60000), null, null, Recorder(new List<HarborMessage>())).ConfigureAwait(false);
                    clock.Advance(TimeSpan.FromSeconds(170));
                    await manager.SweepAsync().ConfigureAwait(false);
                    AssertTrue(manager.IsConnected("hbr_slow"), "170 s is within three 60 s intervals");
                    clock.Advance(TimeSpan.FromSeconds(11));
                    await manager.SweepAsync().ConfigureAwait(false);
                    AssertFalse(manager.IsConnected("hbr_slow"), "181 s is not");
                }
            }));

            cases.Add(CaseAsync("harbor_without_heartbeats_is_never_swept", "A Harbor that turned heartbeats off, or an older Harbor that has not sent two, is never judged stale", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HarborTestClock clock = new HarborTestClock(_Start);
                    HarborConnectionManager manager = NewManager(testDb, clock);
                    await manager.OnHandshakeAsync(Handshake("hbr_off", 0), null, null, Recorder(new List<HarborMessage>())).ConfigureAwait(false);
                    HarborHandshake older = Handshake("hbr_old", 0);
                    older.HeartbeatIntervalMs = null;
                    await manager.OnHandshakeAsync(older, null, null, Recorder(new List<HarborMessage>())).ConfigureAwait(false);
                    clock.Advance(TimeSpan.FromHours(2));
                    await manager.SweepAsync().ConfigureAwait(false);
                    AssertTrue(manager.IsConnected("hbr_off"), "heartbeats off");
                    AssertTrue(manager.IsConnected("hbr_old"), "an older Harbor with no heartbeats seen");

                    // Once an older Harbor has sent two heartbeats, its observed interval sets the timeout.
                    await manager.OnMessageAsync("hbr_old", new HarborHeartbeat()).ConfigureAwait(false);
                    clock.Advance(TimeSpan.FromSeconds(15));
                    await manager.OnMessageAsync("hbr_old", new HarborHeartbeat()).ConfigureAwait(false);
                    clock.Advance(TimeSpan.FromSeconds(46));
                    await manager.SweepAsync().ConfigureAwait(false);
                    AssertFalse(manager.IsConnected("hbr_old"), "observed 15 s interval: dropped after the 45 s minimum");
                    AssertTrue(manager.IsConnected("hbr_off"), "still never judged");
                }
            }));

            cases.Add(CaseAsync("unowned_job_is_stopped_on_reconnect", "After a reconnect the Harbor stops a job the Admiral no longer owns and keeps the ones it does", TestTags.Reliability, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HarborConnectionManager manager = NewManager(testDb, new HarborTestClock(_Start));
                    ControlledHarborJobRunner runner = new ControlledHarborJobRunner();
                    HarborJobState jobs = new HarborJobState();

                    LoopbackHarborSession first = await LoopbackHarborSession.ConnectAsync(manager, "hbr_own", NewClient("hbr_own", runner, jobs)).ConfigureAwait(false);
                    RecordingHarborJobListener kept = new RecordingHarborJobListener();
                    RecordingHarborJobListener abandoned = new RecordingHarborJobListener();
                    await manager.LaunchAsync("hbr_own", Launch("job-kept"), kept).ConfigureAwait(false);
                    await manager.LaunchAsync("hbr_own", Launch("job-abandoned"), abandoned).ConfigureAwait(false);
                    await WaitUntilAsync(() => runner.Launched("job-kept") && runner.Launched("job-abandoned")).ConfigureAwait(false);
                    runner.Start("job-kept", 501);
                    runner.Start("job-abandoned", 502);
                    await WaitUntilAsync(() => kept.ProcessId.HasValue && abandoned.ProcessId.HasValue).ConfigureAwait(false);

                    // While the laptop sleeps, the Admiral gives up on one job (its mission is recovered elsewhere). The stop
                    // cannot reach the Harbor.
                    await first.DropAsync().ConfigureAwait(false);
                    await manager.KillJobAsync("hbr_own", "job-abandoned", 1000).ConfigureAwait(false);
                    AssertEqual(0, runner.Stopped.Count, "nothing reached the Harbor while the link was down");

                    LoopbackHarborSession second = await LoopbackHarborSession.ConnectAsync(manager, "hbr_own", NewClient("hbr_own", runner, jobs)).ConfigureAwait(false);
                    await WaitUntilAsync(() => runner.Stopped.Contains("job-abandoned")).ConfigureAwait(false);
                    AssertFalse(runner.Stopped.Contains("job-kept"), "the job the Admiral still owns keeps running");
                    AssertTrue(jobs.IsLive("job-kept"));
                    AssertFalse(jobs.IsLive("job-abandoned"), "the unowned job is gone from the Harbor");
                    AssertNull(kept.Failure);
                    await second.DropAsync().ConfigureAwait(false);
                }
            }));

            cases.Add(CaseAsync("older_admiral_kills_nothing", "An acknowledgement without the owned-jobs list (an older Admiral) stops no job", TestTags.Negative, async () =>
            {
                ControlledHarborJobRunner runner = new ControlledHarborJobRunner();
                HarborJobState jobs = new HarborJobState();
                jobs.AddLiveJob(new HarborJobInfo { JobId = "job-x", Runtime = "ClaudeCode" });
                HarborLinkClient client = NewClient("hbr_legacy", runner, jobs);
                ScriptedHarborTransport transport = new ScriptedHarborTransport();
                transport.Enqueue(HarborProtocol.Serialize(new HarborHandshakeAck { Accepted = true }));
                await client.RunSessionAsync(transport, CancellationToken.None).ConfigureAwait(false);
                await Task.Delay(100).ConfigureAwait(false);
                AssertEqual(0, runner.Stopped.Count, "nothing stopped");
                HarborHandshake? handshake = HarborProtocol.Deserialize(transport.Sent.First()) as HarborHandshake;
                AssertNotNull(handshake, "the handshake is sent first");
                AssertTrue(handshake!.LiveJobIds != null && handshake.LiveJobIds.Contains("job-x"), "the handshake lists the running job");
                AssertEqual(HarborJobOutboxModeEnum.Legacy, jobs.Outbox.Mode, "no job acknowledgement: legacy outbox mode");
            }));

            cases.Add(CaseAsync("unknown_ack_fields_are_ignored_by_older_parsers", "The new handshake and acknowledgement fields round-trip, and a message without them still parses", TestTags.Positive, () =>
            {
                HarborHandshakeAck ack = (HarborHandshakeAck)HarborProtocol.Deserialize(HarborProtocol.Serialize(new HarborHandshakeAck { Accepted = true, OwnedJobIds = new List<string> { "a" }, LastJobSequence = 7 }));
                AssertEqual("a", ack.OwnedJobIds![0]);
                AssertEqual(7L, ack.LastJobSequence);
                HarborHandshakeAck plain = (HarborHandshakeAck)HarborProtocol.Deserialize("{\"type\":\"handshakeAck\",\"accepted\":true}");
                AssertNull(plain.OwnedJobIds, "an older Admiral sends no list");
                AssertNull(plain.LastJobSequence);
                HarborOutput output = (HarborOutput)HarborProtocol.Deserialize("{\"type\":\"output\",\"jobId\":\"j\",\"data\":\"x\",\"outboxSequence\":4,\"someFutureField\":1}");
                AssertEqual(4L, output.OutboxSequence, "sequence parsed; unknown fields ignored");
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("client_detects_dead_link", "The Harbor treats its link as dead once acknowledged heartbeats stop for the dead-link timeout", TestTags.Reliability, async () =>
            {
                HarborLinkClient client = NewClient("hbr_dead", new ControlledHarborJobRunner(), new HarborJobState(), 50);
                client.MinimumDeadLinkTimeoutMs = 1000;
                SilentAfterAckTransport transport = new SilentAfterAckTransport();
                Task session = client.RunSessionAsync(transport, CancellationToken.None);
                Task finished = await Task.WhenAny(session, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
                AssertTrue(finished == session, "the session ended on its own once the Admiral stopped answering");
                await session.ConfigureAwait(false);
                AssertTrue(transport.Closed, "the transport was closed");
            }));

            return new TestSuiteDescriptor(suiteId: SuiteId, displayName: "Harbor Job Reconcile", cases: cases);
        }

        #endregion

        #region Private-Methods

        private static HarborLaunchRequest Launch(string jobId)
        {
            return new HarborLaunchRequest { JobId = jobId, Runtime = "ClaudeCode", WorkingDirectory = "/w" };
        }

        private static HarborLinkClient NewClient(string harborId, IHarborJobRunner runner, HarborJobState jobs, int heartbeatMs = 0)
        {
            HarborLinkClient client = new HarborLinkClient(harborId, "Rig", new List<HarborCapability> { new HarborCapability { Name = "ClaudeCode" } }, 4,
                new StubHostCommandExecutor(new HostCommandResult { ExitCode = 0 }), Logging(), heartbeatMs, null, runner);
            client.JobState = jobs;
            return client;
        }

        private static HarborConnectionManager NewManager(TestDatabase testDb, HarborTestClock clock)
        {
            HarborConnectionManager manager = new HarborConnectionManager(new HarborService(testDb.Driver, Logging()), Logging(), null);
            manager.SweepInterval = TimeSpan.Zero;
            manager.Clock = clock.Now;
            return manager;
        }

        private static HarborHandshake Handshake(string harborId, int heartbeatIntervalMs)
        {
            return new HarborHandshake
            {
                HarborId = harborId,
                Name = "Rig",
                ProtocolVersion = HarborProtocol.Version,
                MaxConcurrentJobs = 4,
                HeartbeatIntervalMs = heartbeatIntervalMs,
                JobOutboxEpoch = "epoch",
                LiveJobIds = new List<string>(),
                Capabilities = new List<HarborCapability> { new HarborCapability { Name = "ClaudeCode", Available = true } }
            };
        }

        private static HarborSendDelegate Recorder(List<HarborMessage> sink)
        {
            return (HarborMessage message, CancellationToken token) =>
            {
                lock (sink) sink.Add(message);
                return Task.CompletedTask;
            };
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
