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
    /// A Harbor job survives a link blip: its output and exit, produced while the link is down, are held in the job outbox
    /// and replayed in order after the next handshake; heartbeats on the new link still list it; and the Admiral drops job
    /// messages it already received by their outbox sequence. Also covers the outbox's acknowledgement modes and bounds.
    /// </summary>
    public sealed class HarborJobOutboxSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.HarborJobOutbox";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the suite descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("output_and_exit_during_drop_replay_after_reconnect", "Output and the exit a job produces while the link is down reach the Admiral's listener after the Harbor reconnects", TestTags.Reliability, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HarborConnectionManager manager = NewManager(testDb);
                    ControlledHarborJobRunner runner = new ControlledHarborJobRunner();
                    HarborJobState jobs = new HarborJobState();

                    LoopbackHarborSession first = await LoopbackHarborSession.ConnectAsync(manager, "hbr_replay", NewClient("hbr_replay", runner, jobs, 0)).ConfigureAwait(false);
                    RecordingHarborJobListener listener = new RecordingHarborJobListener();
                    await manager.LaunchAsync("hbr_replay", new HarborLaunchRequest { JobId = "job-r", Runtime = "ClaudeCode", WorkingDirectory = "/w" }, listener).ConfigureAwait(false);
                    await WaitUntilAsync(() => runner.Launched("job-r")).ConfigureAwait(false);
                    runner.Start("job-r", 4100);
                    runner.Output("job-r", "before the drop");
                    await WaitUntilAsync(() => listener.Output.Contains("before the drop")).ConfigureAwait(false);
                    AssertEqual(4100, listener.ProcessId, "started reached the listener");

                    await first.DropAsync().ConfigureAwait(false);
                    AssertFalse(manager.IsConnected("hbr_replay"), "the link is down");

                    // The job keeps running and finishes while nobody can hear it.
                    runner.Output("job-r", "during the drop 1");
                    runner.Output("job-r", "during the drop 2");
                    runner.Exit("job-r", 3);
                    AssertEqual(0, jobs.LiveJobIds().Count, "the job ended on the Harbor");
                    AssertTrue(jobs.Outbox.UnsentCount >= 3, "its output and exit wait in the outbox: " + jobs.Outbox.UnsentCount);
                    AssertNull(listener.ExitCode, "the Admiral has not heard the exit yet");

                    LoopbackHarborSession second = await LoopbackHarborSession.ConnectAsync(manager, "hbr_replay", NewClient("hbr_replay", runner, jobs, 0)).ConfigureAwait(false);
                    await WaitUntilAsync(() => listener.ExitCode.HasValue).ConfigureAwait(false);
                    AssertEqual(3, listener.ExitCode, "the exit reached the listener after the reconnect");
                    List<string> output = new List<string>(listener.Output);
                    AssertEqual(1, output.Count(o => o == "before the drop"), "output already delivered is not delivered again");
                    int one = output.IndexOf("during the drop 1");
                    int two = output.IndexOf("during the drop 2");
                    AssertTrue(one >= 0 && two > one, "output from the drop arrives, in order: " + String.Join(" | ", output));
                    await second.DropAsync().ConfigureAwait(false);
                }
            }));

            cases.Add(CaseAsync("heartbeat_after_reconnect_lists_running_job", "A job still running after a reconnect is listed in the new session's handshake and heartbeats", TestTags.Reliability, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HarborConnectionManager manager = NewManager(testDb);
                    ControlledHarborJobRunner runner = new ControlledHarborJobRunner();
                    HarborJobState jobs = new HarborJobState();

                    LoopbackHarborSession first = await LoopbackHarborSession.ConnectAsync(manager, "hbr_live", NewClient("hbr_live", runner, jobs, 0)).ConfigureAwait(false);
                    RecordingHarborJobListener listener = new RecordingHarborJobListener();
                    await manager.LaunchAsync("hbr_live", new HarborLaunchRequest { JobId = "job-l", Runtime = "ClaudeCode", WorkingDirectory = "/w" }, listener).ConfigureAwait(false);
                    await WaitUntilAsync(() => runner.Launched("job-l")).ConfigureAwait(false);
                    runner.Start("job-l", 4200);
                    await WaitUntilAsync(() => listener.ProcessId.HasValue).ConfigureAwait(false);
                    await first.DropAsync().ConfigureAwait(false);

                    HarborLinkClient client = NewClient("hbr_live", runner, jobs, 50);
                    AssertEqual(1, client.LiveJobs().Count, "a new session's client sees the running job");
                    LoopbackHarborSession second = await LoopbackHarborSession.ConnectAsync(manager, "hbr_live", client).ConfigureAwait(false);
                    AssertEqual(1, manager.InFlightJobs("hbr_live"), "the handshake lists the running job");
                    AssertTrue(client.BuildHeartbeat().LiveJobIds.Contains("job-l"), "heartbeats on the new link list it");

                    runner.Exit("job-l", 0);
                    await WaitUntilAsync(() => listener.ExitCode.HasValue).ConfigureAwait(false);
                    AssertEqual(0, listener.ExitCode);
                    AssertNull(listener.Failure, "the job was not given up on");
                    await second.DropAsync().ConfigureAwait(false);
                }
            }));

            cases.Add(CaseAsync("admiral_drops_replayed_duplicates", "The Admiral drops a job message it already received, acknowledges the last sequence, and starts over for a new outbox epoch", TestTags.Reliability, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HarborConnectionManager manager = NewManager(testDb);
                    List<HarborMessage> sent = new List<HarborMessage>();
                    HarborHandshakeAck ack = await manager.OnHandshakeAsync(Handshake("hbr_dup", "epoch-1"), null, null, Recorder(sent)).ConfigureAwait(false);
                    AssertEqual(0L, ack.LastJobSequence, "a new outbox has nothing acknowledged");

                    RecordingHarborJobListener listener = new RecordingHarborJobListener();
                    await manager.LaunchAsync("hbr_dup", new HarborLaunchRequest { JobId = "job-d", Runtime = "ClaudeCode", WorkingDirectory = "/w" }, listener).ConfigureAwait(false);
                    await manager.OnMessageAsync("hbr_dup", new HarborStarted { JobId = "job-d", ProcessId = 1, OutboxSequence = 1 }).ConfigureAwait(false);
                    await manager.OnMessageAsync("hbr_dup", new HarborOutput { JobId = "job-d", Data = "a", OutboxSequence = 2 }).ConfigureAwait(false);
                    await manager.OnMessageAsync("hbr_dup", new HarborHeartbeat { Sequence = 1, LiveJobIds = new List<string> { "job-d" } }).ConfigureAwait(false);
                    HarborHeartbeatAck? heartbeatAck = sent.OfType<HarborHeartbeatAck>().LastOrDefault();
                    AssertNotNull(heartbeatAck, "heartbeat acknowledged");
                    AssertEqual(2L, heartbeatAck!.LastJobSequence, "the acknowledgement carries the last job sequence");

                    ack = await manager.OnHandshakeAsync(Handshake("hbr_dup", "epoch-1", "job-d"), null, null, Recorder(sent)).ConfigureAwait(false);
                    AssertEqual(2L, ack.LastJobSequence, "the reconnect acknowledgement says what arrived");
                    await manager.OnMessageAsync("hbr_dup", new HarborOutput { JobId = "job-d", Data = "a", OutboxSequence = 2 }).ConfigureAwait(false);
                    await manager.OnMessageAsync("hbr_dup", new HarborOutput { JobId = "job-d", Data = "b", OutboxSequence = 3 }).ConfigureAwait(false);
                    AssertEqual("a,b", String.Join(",", listener.Output), "the replayed duplicate is dropped");

                    ack = await manager.OnHandshakeAsync(Handshake("hbr_dup", "epoch-2", "job-d"), null, null, Recorder(sent)).ConfigureAwait(false);
                    AssertEqual(0L, ack.LastJobSequence, "a restarted Harbor (new epoch) counts from the start");
                    await manager.OnMessageAsync("hbr_dup", new HarborOutput { JobId = "job-d", Data = "c", OutboxSequence = 1 }).ConfigureAwait(false);
                    AssertEqual("a,b,c", String.Join(",", listener.Output), "the new epoch's messages are accepted");
                }
            }));

            cases.Add(CaseAsync("outbox_acknowledgement_modes", "The outbox keeps sent messages until acknowledged, drops them once written for an Admiral that does not acknowledge, and replays the rest on the next session", TestTags.Positive, () =>
            {
                HarborJobOutbox outbox = new HarborJobOutbox();
                outbox.Add(new HarborStarted { JobId = "j" });
                outbox.Add(new HarborOutput { JobId = "j", Data = "1" });
                outbox.Add(new HarborExited { JobId = "j" });

                outbox.BeginSession();
                SendNext(outbox);
                SendNext(outbox);
                AssertEqual(HarborJobOutboxModeEnum.Pending, outbox.Mode);
                AssertEqual(3, outbox.Count, "before the acknowledgement, sent messages are kept");
                outbox.SetAcknowledgementMode(1);
                AssertEqual(2, outbox.Count, "acknowledged messages are dropped");
                outbox.Acknowledge(2);
                AssertEqual(1, outbox.Count);
                AssertEqual(1, outbox.UnsentCount, "the exit is still to be sent");

                outbox.BeginSession();
                outbox.SetAcknowledgementMode(null);
                HarborMessage? next = SendNext(outbox);
                AssertTrue(next is HarborExited, "the next session replays the exit");
                AssertEqual(0, outbox.Count, "an Admiral that does not acknowledge: written messages are dropped");
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("outbox_is_bounded_and_keeps_lifecycle", "A full outbox drops the oldest output and keeps started and exited", TestTags.Negative, () =>
            {
                HarborJobOutbox outbox = new HarborJobOutbox { MaxMessages = 16 };
                outbox.Add(new HarborStarted { JobId = "j" });
                for (int i = 0; i < 40; i++) outbox.Add(new HarborOutput { JobId = "j", Data = "line " + i });
                outbox.Add(new HarborExited { JobId = "j", ExitCode = 0 });

                List<HarborMessage> held = outbox.Snapshot();
                AssertEqual(16, held.Count, "bounded");
                AssertTrue(held[0] is HarborStarted, "started is kept");
                AssertTrue(held[held.Count - 1] is HarborExited, "exited is kept");
                AssertEqual("line 39", ((HarborOutput)held[held.Count - 2]).Data, "the newest output is kept");
                AssertEqual(26L, outbox.DroppedCount, "the oldest output was dropped");
                long previous = 0;
                foreach (HarborMessage message in held)
                {
                    AssertTrue(message.OutboxSequence.HasValue && message.OutboxSequence.Value > previous, "sequences increase");
                    previous = message.OutboxSequence!.Value;
                }

                return Task.CompletedTask;
            }));

            return new TestSuiteDescriptor(suiteId: SuiteId, displayName: "Harbor Job Outbox", cases: cases);
        }

        #endregion

        #region Private-Methods

        private static HarborMessage? SendNext(HarborJobOutbox outbox)
        {
            if (!outbox.TryPeekNext(out HarborMessage? message) || message == null) return null;
            outbox.MarkSent(message);
            return message;
        }

        private static HarborLinkClient NewClient(string harborId, IHarborJobRunner runner, HarborJobState jobs, int heartbeatMs)
        {
            HarborLinkClient client = new HarborLinkClient(harborId, "Rig", new List<HarborCapability> { new HarborCapability { Name = "ClaudeCode" } }, 4,
                new StubHostCommandExecutor(new HostCommandResult { ExitCode = 0 }), Logging(), heartbeatMs, null, runner);
            client.JobState = jobs;
            return client;
        }

        private static HarborConnectionManager NewManager(TestDatabase testDb)
        {
            HarborConnectionManager manager = new HarborConnectionManager(new HarborService(testDb.Driver, Logging()), Logging(), null);
            manager.SweepInterval = TimeSpan.Zero;
            return manager;
        }

        private static HarborHandshake Handshake(string harborId, string epoch, params string[] liveJobIds)
        {
            return new HarborHandshake
            {
                HarborId = harborId,
                Name = "Rig",
                ProtocolVersion = HarborProtocol.Version,
                MaxConcurrentJobs = 4,
                JobOutboxEpoch = epoch,
                LiveJobIds = new List<string>(liveJobIds),
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
