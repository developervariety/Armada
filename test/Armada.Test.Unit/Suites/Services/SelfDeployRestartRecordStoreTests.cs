namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Durable restart record writes, compare-and-swap transitions, locks and the startup guard.
    /// </summary>
    public sealed class SelfDeployRestartRecordStoreTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Self Deploy Restart Record";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateAsync_WritesPrivateRecordWithoutTemporaryLeftovers", async () =>
            {
                if (SkipWindows("CreateAsync_WritesPrivateRecordWithoutTemporaryLeftovers")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployRestartRecordStore store = new SelfDeployRestartRecordStore(Path.Combine(directory.Root, "state"));
                    SelfDeployRestartRecord record = NewRecord(SelfDeployRestartStateEnum.Prepared);
                    SelfDeployRestartTransitionResult created = await store.CreateAsync(record);
                    AssertTrue(created.Applied, "record created");
                    AssertEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.RecordPath), "record is owner-only");
                    AssertEqual(0, Directory.GetFiles(Path.Combine(directory.Root, "state"), "*.tmp").Length, "no temporary file left");
                    SelfDeployRestartRecordReadResult read = await store.ReadAsync();
                    AssertTrue(read.IsReadable, "record readable");
                    AssertEqual(record.OperationId, read.Record!.OperationId, "operation id round-trips");
                    AssertEqual(SelfDeployRestartStateEnum.Prepared, read.Record.State, "state round-trips");
                }
            });

            await RunTest("DefaultLayout_ArtifactCaptureBeforeRecordWrite_KeepsEveryLevelPrivate", async () =>
            {
                if (SkipWindows("DefaultLayout_ArtifactCaptureBeforeRecordWrite_KeepsEveryLevelPrivate")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    string dataDirectory = Path.Combine(directory.Root, "data");
                    string source = Path.Combine(directory.Root, "running");
                    Directory.CreateDirectory(source);
                    File.WriteAllText(Path.Combine(source, "Armada.Server.dll"), "server");
                    SelfDeployCutoverComponents components = SelfDeployCutoverComponents.CreateDefault(dataDirectory, new Armada.Core.Settings.SelfDeploySettings());

                    await components.Artifacts.CaptureAsync(source, "Armada.Server.dll");
                    SelfDeployRestartTransitionResult created = await components.Records.CreateAsync(NewRecord(SelfDeployRestartStateEnum.Prepared));

                    AssertTrue(created.Applied, "record written after the rollback capture created the state directory: " + created.FailureReason);
                    UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                    AssertEqual(privateDirectory, File.GetUnixFileMode(dataDirectory), "data directory created owner-only");
                    AssertEqual(privateDirectory, File.GetUnixFileMode(SelfDeployRestartRecordStore.DirectoryFor(dataDirectory)), "state directory owner-only");
                }
            });

            await RunTest("CreateAsync_NonTerminalRecordExists_RefusesSecondOperation", async () =>
            {
                if (SkipWindows("CreateAsync_NonTerminalRecordExists_RefusesSecondOperation")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployRestartRecordStore store = new SelfDeployRestartRecordStore(Path.Combine(directory.Root, "state"));
                    SelfDeployRestartRecord first = NewRecord(SelfDeployRestartStateEnum.CandidateStarting);
                    await store.CreateAsync(first);
                    SelfDeployRestartTransitionResult second = await store.CreateAsync(NewRecord(SelfDeployRestartStateEnum.Prepared));
                    AssertFalse(second.Applied, "second operation refused");
                    AssertEqual("restart_in_progress", second.FailureReason, "refusal reason");
                    AssertEqual(first.OperationId, (await store.ReadAsync()).Record!.OperationId, "first record kept");
                }
            });

            await RunTest("CreateAsync_TerminalRecordExists_AllowsNewOperation", async () =>
            {
                if (SkipWindows("CreateAsync_TerminalRecordExists_AllowsNewOperation")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployRestartRecordStore store = new SelfDeployRestartRecordStore(Path.Combine(directory.Root, "state"));
                    await store.CreateAsync(NewRecord(SelfDeployRestartStateEnum.RolledBack));
                    SelfDeployRestartTransitionResult second = await store.CreateAsync(NewRecord(SelfDeployRestartStateEnum.Prepared));
                    AssertTrue(second.Applied, "terminal record replaced");
                }
            });

            await RunTest("CreateAsync_UnreadableRecord_RefusesNewOperation", async () =>
            {
                if (SkipWindows("CreateAsync_UnreadableRecord_RefusesNewOperation")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployRestartRecordStore store = new SelfDeployRestartRecordStore(Path.Combine(directory.Root, "state"));
                    await store.CreateAsync(NewRecord(SelfDeployRestartStateEnum.RolledBack));
                    File.WriteAllText(store.RecordPath, "{ not json");
                    SelfDeployRestartRecordReadResult read = await store.ReadAsync();
                    AssertTrue(read.Exists, "corrupt record exists");
                    AssertFalse(read.IsReadable, "corrupt record is not readable");
                    AssertEqual("restart_record_unreadable", read.FailureReason, "corrupt reason");
                    SelfDeployRestartTransitionResult created = await store.CreateAsync(NewRecord(SelfDeployRestartStateEnum.Prepared));
                    AssertFalse(created.Applied, "unknown restart state blocks a new operation");
                }
            });

            await RunTest("TryTransitionAsync_UnexpectedStateOrOperation_DoesNotWrite", async () =>
            {
                if (SkipWindows("TryTransitionAsync_UnexpectedStateOrOperation_DoesNotWrite")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployRestartRecordStore store = new SelfDeployRestartRecordStore(Path.Combine(directory.Root, "state"));
                    SelfDeployRestartRecord record = NewRecord(SelfDeployRestartStateEnum.Prepared);
                    await store.CreateAsync(record);
                    SelfDeployRestartTransitionResult wrongState = await store.TryTransitionAsync(record.OperationId,
                        new[] { SelfDeployRestartStateEnum.Armed },
                        r => r.MoveTo(SelfDeployRestartStateEnum.ExitRequested, "test"));
                    SelfDeployRestartTransitionResult wrongOperation = await store.TryTransitionAsync("sdo_other",
                        new[] { SelfDeployRestartStateEnum.Prepared },
                        r => r.MoveTo(SelfDeployRestartStateEnum.Armed, "test"));
                    AssertFalse(wrongState.Applied, "unexpected state refused");
                    AssertEqual("unexpected_state_Prepared", wrongState.FailureReason, "state reason");
                    AssertFalse(wrongOperation.Applied, "other operation refused");
                    AssertEqual("operation_mismatch", wrongOperation.FailureReason, "operation reason");
                    AssertEqual(SelfDeployRestartStateEnum.Prepared, (await store.ReadAsync()).Record!.State, "record unchanged");
                }
            });

            await RunTest("TryTransitionAsync_ConcurrentWriters_ExactlyOneWins", async () =>
            {
                if (SkipWindows("TryTransitionAsync_ConcurrentWriters_ExactlyOneWins")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    string state = Path.Combine(directory.Root, "state");
                    SelfDeployRestartRecord record = NewRecord(SelfDeployRestartStateEnum.Prepared);
                    await new SelfDeployRestartRecordStore(state).CreateAsync(record);
                    List<Task<SelfDeployRestartTransitionResult>> writers = new List<Task<SelfDeployRestartTransitionResult>>();
                    for (int i = 0; i < 8; i++)
                    {
                        string reason = "writer_" + i;
                        SelfDeployRestartRecordStore writerStore = new SelfDeployRestartRecordStore(state);
                        writers.Add(Task.Run(() => writerStore.TryTransitionAsync(record.OperationId,
                            new[] { SelfDeployRestartStateEnum.Prepared },
                            r => r.MoveTo(SelfDeployRestartStateEnum.Armed, reason))));
                    }
                    SelfDeployRestartTransitionResult[] results = await Task.WhenAll(writers);
                    int applied = 0;
                    foreach (SelfDeployRestartTransitionResult result in results)
                    {
                        if (result.Applied) applied++;
                    }
                    AssertEqual(1, applied, "exactly one compare-and-swap applies");
                }
            });

            await RunTest("SupervisorLock_IsExclusiveUntilReleased", async () =>
            {
                if (SkipWindows("SupervisorLock_IsExclusiveUntilReleased")) return;
                using (SelfDeployTestDirectory directory = new SelfDeployTestDirectory())
                {
                    SelfDeployRestartRecordStore first = new SelfDeployRestartRecordStore(Path.Combine(directory.Root, "state"));
                    SelfDeployRestartRecordStore second = new SelfDeployRestartRecordStore(Path.Combine(directory.Root, "state"));
                    IDisposable? held = first.TryAcquireSupervisorLock();
                    AssertNotNull(held, "first lock acquired");
                    AssertNull(second.TryAcquireSupervisorLock(), "second lock refused while held");
                    held!.Dispose();
                    using (IDisposable? again = second.TryAcquireSupervisorLock())
                    {
                        AssertNotNull(again, "lock available after release");
                    }
                    await Task.CompletedTask;
                }
            });

            await RunTest("MoveTo_BoundsTransitionHistory", () =>
            {
                SelfDeployRestartRecord record = NewRecord(SelfDeployRestartStateEnum.Prepared);
                for (int i = 0; i < 100; i++) record.MoveTo(SelfDeployRestartStateEnum.RollbackStarting, "retry_" + i);
                AssertEqual(SelfDeployRestartRecord.MaximumTransitions, record.Transitions.Count, "history bounded");
                AssertEqual("retry_99", record.Transitions[record.Transitions.Count - 1].Reason, "newest entry kept");
            });

            await RunTest("StartupGuard_AdmitsOnlyTheSupervisedLaunchDuringRestart", () =>
            {
                SelfDeployRestartRecord inFlight = NewRecord(SelfDeployRestartStateEnum.CandidateStarting);
                SelfDeployRestartRecordReadResult present = new SelfDeployRestartRecordReadResult { Exists = true, Record = inFlight };

                AssertTrue(SelfDeployStartupGuard.Evaluate(new SelfDeployRestartRecordReadResult { Exists = false }, null).Allowed, "no record allows start");
                AssertTrue(SelfDeployStartupGuard.Evaluate(present, inFlight.OperationId).Allowed, "supervised launch admitted");

                SelfDeployStartupDecision manual = SelfDeployStartupGuard.Evaluate(present, null);
                AssertFalse(manual.Allowed, "manual start refused during restart");
                AssertEqual("restart_in_progress", manual.Reason, "refusal reason");
                AssertFalse(SelfDeployStartupGuard.Evaluate(present, "sdo_other").Allowed, "other operation refused");

                SelfDeployRestartRecord armed = NewRecord(SelfDeployRestartStateEnum.ExitRequested);
                AssertFalse(SelfDeployStartupGuard.Evaluate(new SelfDeployRestartRecordReadResult { Exists = true, Record = armed }, armed.OperationId).Allowed,
                    "operation id alone does not admit a start before the supervisor launches");

                SelfDeployRestartRecord done = NewRecord(SelfDeployRestartStateEnum.Failed);
                AssertTrue(SelfDeployStartupGuard.Evaluate(new SelfDeployRestartRecordReadResult { Exists = true, Record = done }, null).Allowed, "terminal record allows start");

                SelfDeployStartupDecision corrupt = SelfDeployStartupGuard.Evaluate(
                    new SelfDeployRestartRecordReadResult { Exists = true, FailureReason = "restart_record_unreadable" }, null);
                AssertFalse(corrupt.Allowed, "unreadable record refuses start");
                AssertEqual("restart_record_unreadable", corrupt.Reason, "unreadable reason");
            });
        }

        private bool SkipWindows(string testName)
        {
            if (!OperatingSystem.IsWindows()) return false;
            SkipTest(testName, "These cases assert Unix permission bits; Windows ACLs are covered by the Private Storage Backend suite.");
            return true;
        }

        private static SelfDeployRestartRecord NewRecord(SelfDeployRestartStateEnum state)
        {
            SelfDeployRestartRecord record = new SelfDeployRestartRecord
            {
                OperationId = "sdo_" + Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow
            };
            record.MoveTo(state, "test");
            return record;
        }
    }
}
