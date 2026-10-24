namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Runtimes.Harbor;
    using Armada.Test.Common;

    /// <summary>Exercises the local Harbor journal and supervisor with real child processes.</summary>
    public sealed class HarborRunnerTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Harbor Runner";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Supervisor captures exact launch inputs and persists both output streams before terminal state", async () =>
            {
                using (TestArea area = CreateArea())
                {
                    HarborSupervisorOptions options = Options(area);
                    options.MaxRetainedJobs = 1;
                    HarborJobJournal journal = new HarborJobJournal(options);
                    HarborLaunchRequest request = Request(area, "inspect-job", "attempt-key", "inspect", "first argument", "two words");
                    request.Prompt = "bounded test prompt";
                    request.PromptViaStdin = true;
                    request.Environment["MAX_THINKING_TOKENS"] = "173";

                    int exitCode = await HarborJobSupervisor.RunAsync(new HarborSupervisorStart(options, request)).ConfigureAwait(false);
                    HarborLocalJobRecord record = journal.Read(request.JobId);
                    AssertEqual(23, exitCode, "the child's actual exit code is returned");
                    AssertFalse(record.IsRunning, "the child is no longer running");
                    AssertTrue(record.StopConfirmed, "terminal state follows a confirmed child exit");
                    AssertEqual(23, record.ExitCode ?? -1, "the journal retains the known child exit code");
                    AssertTrue(record.ProcessId.HasValue && record.ProcessStartedUtc.HasValue, "the journal retains exact child identity");
                    AssertEqual(DateTimeKind.Utc, record.ProcessStartedUtc!.Value.Kind, "child start time is normalized to UTC");

                    List<HarborOutput> output = journal.ReadOutput(request.JobId, record.FirstOutputSequence);
                    AssertEqual(record.NextOutputSequence - record.FirstOutputSequence, (long)output.Count, "every persisted sequence is readable");
                    for (int index = 0; index < output.Count; index++)
                        AssertEqual(record.FirstOutputSequence + index, output[index].Sequence, "output sequences are contiguous");
                    string stdout = String.Concat(output.Where(item => item.Stream == HarborOutputStreamEnum.Stdout).Select(item => item.Data));
                    string stderr = String.Concat(output.Where(item => item.Stream == HarborOutputStreamEnum.Stderr).Select(item => item.Data));
                    List<string> stdoutLines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
                    List<string> observedArguments = stdoutLines.Where(line => line.StartsWith("ARG:", StringComparison.Ordinal))
                        .Select(line => Decode(line.Substring(4))).ToList();
                    AssertEqual("first argument", observedArguments[0], "the first resolved argument is unchanged");
                    AssertEqual("two words", observedArguments[1], "an argument with spaces remains one argument");
                    AssertEqual(area.Work, Decode(stdoutLines.Single(line => line.StartsWith("CWD:", StringComparison.Ordinal)).Substring(4)), "the child uses the exact working directory");
                    AssertEqual("173", Decode(stdoutLines.Single(line => line.StartsWith("ENV:", StringComparison.Ordinal)).Substring(4)), "only the selected non-secret environment value reaches the child");
                    AssertEqual("bounded test prompt", Decode(stdoutLines.Single(line => line.StartsWith("PROMPT:", StringComparison.Ordinal)).Substring(7)), "the prompt arrives on stdin");
                    AssertContains("stderr-marker", stderr, "stderr is captured separately");
                    AssertContains(new string('a', 4095) + "😀tail\n", stdout, "a non-BMP character split at the reader boundary is preserved exactly");
                    AssertEqual(false, record.OutputGap, "complete output has no gap");

                    HarborJobJournalException duplicate = await CaptureJournalFailureAsync(
                        () => HarborJobSupervisor.RunAsync(new HarborSupervisorStart(options, request)), "duplicate launch").ConfigureAwait(false);
                    AssertEqual("job_already_exists", duplicate.ReasonCode, "a repeated server job id is refused");
                    AssertEqual(record.ProcessId, journal.Read(request.JobId).ProcessId, "the refused duplicate did not replace the child identity");
                    AssertEqual((long)output.Count, (long)journal.ReadOutput(request.JobId, 0).Count, "the refused duplicate did not append another child's output");

                    HarborLaunchRequest quotaRequest = Request(area, "over-quota-job", "second-attempt", "inspect", "unused");
                    HarborJobJournalException quota = await CaptureJournalFailureAsync(
                        () => HarborJobSupervisor.RunAsync(new HarborSupervisorStart(options, quotaRequest)), "retained job quota").ConfigureAwait(false);
                    AssertEqual("job_capacity_exhausted", quota.ReasonCode, "the retained job quota refuses a new child");
                    AssertEqual(1, journal.Enumerate().Count, "quota refusal creates no second retained job");
                }
            }).ConfigureAwait(false);

            await RunTest("Stop requests require the exact launch key and confirm the real child exit", async () =>
            {
                using (TestArea area = CreateArea())
                {
                    HarborSupervisorOptions options = Options(area);
                    HarborJobJournal journal = new HarborJobJournal(options);
                    HarborLaunchRequest request = Request(area, "held-job", "held-key", "hold");
                    Task<int> run = HarborJobSupervisor.RunAsync(new HarborSupervisorStart(options, request));
                    HarborLocalJobRecord? record = null;
                    try
                    {
                        record = await WaitForRecordAsync(journal, request.JobId, candidate => candidate.IsRunning && candidate.ProcessId.HasValue).ConfigureAwait(false);
                        AssertTrue(await WaitForOutputAsync(journal, request.JobId).ConfigureAwait(false), "the child published its ready record before stop");
                        AssertTrue(HarborJobJournal.IsExactProcessAlive(record.ProcessId!.Value, record.ProcessStartedUtc,
                            record.ProcessLinuxStartTicks, record.ProcessLinuxBootId), "the recorded child is alive at its exact start identity");

                        HarborJobJournalException wrongKey = null!;
                        try { journal.WriteStopRequest(request.JobId, "different-key", 0); }
                        catch (HarborJobJournalException exception) { wrongKey = exception; }
                        AssertNotNull(wrongKey, "a stop request with another attempt key is refused");
                        AssertEqual("stop_identity_mismatch", wrongKey.ReasonCode, "wrong-key refusal is named");
                        AssertTrue(HarborJobJournal.IsExactProcessAlive(record.ProcessId.Value, record.ProcessStartedUtc,
                            record.ProcessLinuxStartTicks, record.ProcessLinuxBootId), "a refused stop leaves the actual child running");

                        journal.WriteStopRequest(request.JobId, request.LaunchKey, 0);
                        Task completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                        AssertTrue(Object.ReferenceEquals(completed, run), "the exact stop request completes within the bounded wait");
                        await run.ConfigureAwait(false);
                        HarborLocalJobRecord stopped = journal.Read(request.JobId);
                        AssertTrue(stopped.StopConfirmed, "the supervisor records the child exit after the stop");
                        AssertFalse(stopped.IsRunning, "the stopped child is not reported as running");
                        AssertNotNull(stopped.ExitCode, "forced stop still records a known process exit code");
                        AssertFalse(HarborJobJournal.IsExactProcessAlive(record.ProcessId.Value, record.ProcessStartedUtc,
                            record.ProcessLinuxStartTicks, record.ProcessLinuxBootId), "the exact child is gone after confirmation");
                        AssertEqual(1, journal.Enumerate().Count, "one retained job has one terminal record");
                    }
                    finally
                    {
                        HarborLocalJobRecord? cleanupRecord = record;
                        if (cleanupRecord == null)
                        {
                            try { cleanupRecord = journal.Read(request.JobId); }
                            catch (HarborJobJournalException exception)
                            {
                                Trace.TraceWarning("Harbor runner test cleanup could not read the recorded child identity: " + exception.ReasonCode);
                            }
                        }
                        if (!run.IsCompleted && cleanupRecord?.ProcessId is int processId && cleanupRecord.ProcessStartedUtc.HasValue)
                        {
                            try
                            {
                                using (Process child = Process.GetProcessById(processId))
                                {
                                    DateTime actualStartedUtc = child.StartTime.ToUniversalTime();
                                    actualStartedUtc = new DateTime(actualStartedUtc.Ticks - (actualStartedUtc.Ticks % 10), DateTimeKind.Utc);
                                    if (!child.HasExited && actualStartedUtc == cleanupRecord.ProcessStartedUtc.Value)
                                        child.Kill(true);
                                }
                            }
                            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException || exception is System.ComponentModel.Win32Exception || exception is NotSupportedException)
                            {
                                Trace.TraceWarning("Harbor runner test cleanup could not verify or stop the recorded child: " + exception.GetType().Name);
                            }
                        }
                        Task finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                        if (Object.ReferenceEquals(finished, run))
                        {
                            try { await run.ConfigureAwait(false); }
                            catch (HarborJobJournalException exception)
                            {
                                Trace.TraceWarning("Harbor runner test cleanup observed a named journal failure: " + exception.ReasonCode);
                            }
                        }
                        else
                        {
                            Trace.TraceWarning("Harbor runner test cleanup did not observe supervisor completion within five seconds.");
                            area.PreserveForDiagnosis = true;
                            _ = run.ContinueWith(completed => { _ = completed.Exception; },
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                        }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Malformed committed output, terminal trailing bytes, and missing sequences cannot replay", async () =>
            {
                using (TestArea area = CreateArea())
                {
                    HarborSupervisorOptions options = Options(area);
                    HarborJobJournal journal = new HarborJobJournal(options);
                    HarborLaunchRequest corruptRequest = Request(area, "malformed-committed-row", "malformed-key", "inspect", "one");
                    await HarborJobSupervisor.RunAsync(new HarborSupervisorStart(options, corruptRequest)).ConfigureAwait(false);
                    AssertTrue(journal.Read(corruptRequest.JobId).StopConfirmed, "the child exit is confirmed before committed-row validation");
                    string corruptPath = Path.Combine(area.State, corruptRequest.JobId, "output.jsonl");
                    string[] corruptLines = await File.ReadAllLinesAsync(corruptPath).ConfigureAwait(false);
                    AssertTrue(corruptLines.Length > 1, "the fixture produced committed output rows");
                    corruptLines[0] = "{\"Sequence\":";
                    await File.WriteAllLinesAsync(corruptPath, corruptLines, new UTF8Encoding(false)).ConfigureAwait(false);
                    HarborJobJournalException corrupt = CaptureJournalFailure(() => journal.ReadOutput(corruptRequest.JobId, 0), "malformed committed row");
                    AssertEqual("output_spool_corrupt", corrupt.ReasonCode, "a malformed committed record is refused by name");

                    HarborLaunchRequest trailingRequest = Request(area, "terminal-trailing-data", "trailing-key", "inspect", "tail");
                    await HarborJobSupervisor.RunAsync(new HarborSupervisorStart(options, trailingRequest)).ConfigureAwait(false);
                    AssertTrue(journal.Read(trailingRequest.JobId).StopConfirmed, "the child exit is confirmed before terminal-tail validation");
                    string trailingPath = Path.Combine(area.State, trailingRequest.JobId, "output.jsonl");
                    await File.AppendAllTextAsync(trailingPath, "{\"Sequence\":", new UTF8Encoding(false)).ConfigureAwait(false);
                    HarborJobJournalException trailing = CaptureJournalFailure(() => journal.ReadOutput(trailingRequest.JobId, 0), "terminal trailing data");
                    AssertEqual("output_spool_trailing_data", trailing.ReasonCode, "uncommitted bytes after the terminal cursor are refused by name");

                    HarborLaunchRequest gapRequest = Request(area, "sequence-gap", "gap-key", "inspect", "two");
                    await HarborJobSupervisor.RunAsync(new HarborSupervisorStart(options, gapRequest)).ConfigureAwait(false);
                    string gapPath = Path.Combine(area.State, gapRequest.JobId, "output.jsonl");
                    string[] lines = await File.ReadAllLinesAsync(gapPath).ConfigureAwait(false);
                    AssertTrue(lines.Length > 1, "the fixture produced multiple independently sequenced output records");
                    await File.WriteAllLinesAsync(gapPath, lines.Skip(1), new UTF8Encoding(false)).ConfigureAwait(false);
                    HarborJobJournalException gap = CaptureJournalFailure(() => journal.ReadOutput(gapRequest.JobId, 0), "sequence gap");
                    AssertEqual("output_sequence_invalid", gap.ReasonCode, "a missing sequence cannot be returned as replay output");
                }
            }).ConfigureAwait(false);

            await RunTest("A child outside configured roots is refused without changing outside content", async () =>
            {
                using (TestArea area = CreateArea())
                {
                    string outside = Path.Combine(area.Root, "outside");
                    Directory.CreateDirectory(outside);
                    string sentinel = Path.Combine(outside, "keep.txt");
                    await File.WriteAllTextAsync(sentinel, "owner data").ConfigureAwait(false);
                    HarborSupervisorOptions options = Options(area);
                    HarborLaunchRequest request = Request(area, "outside-root", "outside-key", "inspect", "unused");
                    request.WorkingDirectory = outside;
                    HarborJobJournalException refusal = await CaptureJournalFailureAsync(
                        () => HarborJobSupervisor.RunAsync(new HarborSupervisorStart(options, request)), "outside-root launch").ConfigureAwait(false);
                    AssertEqual("working_directory_outside_roots", refusal.ReasonCode, "a path outside configured roots is refused");
                    AssertEqual("owner data", await File.ReadAllTextAsync(sentinel).ConfigureAwait(false), "outside-root refusal leaves outside content unchanged");
                    AssertFalse(journalRecordRunning(options, request.JobId), "no child was started for the outside path");
                }
            }).ConfigureAwait(false);

            await RunTest("A symbolic-link working path is refused without changing its target", async () =>
            {
                using (TestArea area = CreateArea())
                {
                    string outside = Path.Combine(area.Root, "link-target");
                    Directory.CreateDirectory(outside);
                    string sentinel = Path.Combine(outside, "keep.txt");
                    await File.WriteAllTextAsync(sentinel, "outside target").ConfigureAwait(false);
                    string link = Path.Combine(area.WorkingRoot, "linked-work");
                    try { Directory.CreateSymbolicLink(link, outside); }
                    catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is PlatformNotSupportedException)
                    {
                        SkipTest("A symbolic-link working path is refused without changing its target", "This host cannot create a directory symbolic link: " + exception.GetType().Name);
                        return;
                    }

                    HarborSupervisorOptions options = Options(area);
                    HarborLaunchRequest request = Request(area, "linked-root", "linked-key", "inspect", "unused");
                    request.WorkingDirectory = link;
                    HarborJobJournalException refusal = await CaptureJournalFailureAsync(
                        () => HarborJobSupervisor.RunAsync(new HarborSupervisorStart(options, request)), "linked working directory").ConfigureAwait(false);
                    AssertEqual("working_directory_link_refused", refusal.ReasonCode, "a symlink component is refused");
                    AssertEqual("outside target", await File.ReadAllTextAsync(sentinel).ConfigureAwait(false), "symlink refusal leaves target content unchanged");
                }
            }).ConfigureAwait(false);

            await RunTest("Spool overflow leaves a named output gap and cannot be replayed as complete", async () =>
            {
                using (TestArea area = CreateArea())
                {
                    HarborSupervisorOptions options = Options(area);
                    options.MaxSpoolBytes = 1024;
                    HarborJobJournal journal = new HarborJobJournal(options);
                    HarborLaunchRequest request = Request(area, "oversized-output", "oversized-key", "large");
                    HarborJobJournalException failure = await CaptureJournalFailureAsync(
                        () => HarborJobSupervisor.RunAsync(new HarborSupervisorStart(options, request)), "spool overflow").ConfigureAwait(false);
                    AssertEqual("output_limit_exceeded", failure.ReasonCode, "the child cannot report success after output persistence fails");
                    HarborLocalJobRecord record = journal.Read(request.JobId);
                    AssertTrue(record.OutputGap, "the durable job record names an incomplete spool");
                    AssertEqual("output_limit_exceeded", record.FailureReason, "the persisted failure reason is stable");
                    HarborJobJournalException gap = CaptureJournalFailure(() => journal.ReadOutput(request.JobId, 0), "overflow replay");
                    AssertEqual("output_gap", gap.ReasonCode, "the incomplete spool cannot be read as valid replay");
                }
            }).ConfigureAwait(false);

            await RunTest("The shared storage policy rejects aggregate reservations above the runner limit", async () =>
            {
                using (TestArea area = CreateArea())
                {
                    HarborSupervisorOptions pinnedOptions = Options(area);
                    HarborJobJournal pinnedJournal = new HarborJobJournal(pinnedOptions);
                    HarborSupervisorOptions changedOptions = Options(area);
                    changedOptions.MaxRetainedJobs = 32;
                    HarborJobJournalException mismatch = null!;
                    try { new HarborJobJournal(changedOptions); }
                    catch (HarborJobJournalException exception) { mismatch = exception; }
                    AssertNotNull(mismatch, "a changed reservation cannot reuse the same private journal root");
                    AssertEqual("storage_policy_mismatch", mismatch.ReasonCode, "a pinned policy mismatch has a stable refusal code");
                    AssertEqual(0, pinnedJournal.Enumerate().Count, "policy mismatch does not create a job record");

                    HarborSupervisorOptions options = Options(area);
                    options.MaxRetainedJobs = 2;
                    options.MaxSpoolBytes = HarborSupervisorStoragePolicy.MaximumAggregateSpoolBytes / 2;
                    HarborSupervisorStoragePolicy.Validate(options);

                    options.MaxSpoolBytes++;
                    ArgumentException policyFailure = null!;
                    try { HarborSupervisorStoragePolicy.Validate(options); }
                    catch (ArgumentException exception) { policyFailure = exception; }
                    AssertNotNull(policyFailure, "the first reservation above one GiB is refused");

                    HarborJobJournalException journalFailure = null!;
                    try { new HarborJobJournal(options); }
                    catch (HarborJobJournalException exception) { journalFailure = exception; }
                    AssertNotNull(journalFailure, "the journal applies the shared storage policy");
                    AssertEqual("storage_policy_invalid", journalFailure.ReasonCode, "journal policy refusal is named");

                    HarborRunnerSettings settings = new HarborRunnerSettings
                    {
                        RunnerId = "runner_test",
                        Name = "Harbor component test",
                        LinkUri = "ws://localhost",
                        Credential = "private-test-credential",
                        MaxConcurrentJobs = 2,
                        Supervisor = options
                    };
                    ArgumentException settingsFailure = null!;
                    try { settings.Validate(); }
                    catch (ArgumentException exception) { settingsFailure = exception; }
                    AssertNotNull(settingsFailure, "runner settings use the same aggregate policy");
                }
                await Task.CompletedTask.ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Stale unpublished stages are removed without deleting visible job history", async () =>
            {
                using (TestArea area = CreateArea())
                {
                    HarborSupervisorOptions options = Options(area);
                    HarborJobJournal journal = new HarborJobJournal(options);
                    HarborLaunchRequest visibleRequest = Request(area, "visible-history", "visible-key", "inspect", "retained");
                    await HarborJobSupervisor.RunAsync(new HarborSupervisorStart(options, visibleRequest)).ConfigureAwait(false);
                    HarborLocalJobRecord visibleBefore = journal.Read(visibleRequest.JobId);
                    List<HarborOutput> outputBefore = journal.ReadOutput(visibleRequest.JobId, visibleBefore.FirstOutputSequence);

                    string staging = Path.Combine(area.State, ".pending-" + Guid.NewGuid().ToString("N"));
                    HarborPrivateStorage.EnsurePrivateDirectory(staging);
                    string outputPath = Path.Combine(staging, "output.jsonl");
                    await File.WriteAllTextAsync(outputPath, String.Empty, new UTF8Encoding(false)).ConfigureAwait(false);
                    HarborPrivateStorage.EnsurePrivateFile(outputPath);
                    string temporaryRecord = Path.Combine(staging, "job.json.0123456789abcdef.tmp");
                    await File.WriteAllTextAsync(temporaryRecord, "incomplete unpublished metadata", new UTF8Encoding(false)).ConfigureAwait(false);
                    HarborPrivateStorage.EnsurePrivateFile(temporaryRecord);

                    HarborLaunchRequest nextRequest = Request(area, "next-visible-job", "next-key", "inspect", "new");
                    HarborLocalJobRecord pending = journal.CreateIntent(nextRequest);
                    AssertFalse(Directory.Exists(staging), "the stale unpublished stage is removed under the admission lock");
                    AssertEqual(nextRequest.JobId, pending.JobId, "the next intent is published after stale-stage cleanup");
                    HarborLocalJobRecord visibleAfter = journal.Read(visibleRequest.JobId);
                    AssertTrue(visibleAfter.StopConfirmed, "the previous visible job remains terminal");
                    AssertEqual(visibleBefore.NextOutputSequence, visibleAfter.NextOutputSequence, "the previous visible output cursor is unchanged");
                    AssertEqual((long)outputBefore.Count, (long)journal.ReadOutput(visibleRequest.JobId, visibleAfter.FirstOutputSequence).Count,
                        "the previous visible output remains replayable");
                    AssertEqual(2, journal.Enumerate().Count, "both published job histories remain visible");
                }
            }).ConfigureAwait(false);

            await RunTest("Runner launch fails clearly when no absolute child executable is configured", async () =>
            {
                using (TestArea area = CreateArea())
                {
                    HarborSupervisorOptions options = Options(area);
                    options.Commands["missing"] = "relative-command";
                    HarborLaunchRequest request = Request(area, "invalid-command", "invalid-key", "inspect", "unused");
                    request.Runtime = "missing";
                    HarborJobJournalException failure = await CaptureJournalFailureAsync(
                        () => HarborJobSupervisor.RunAsync(new HarborSupervisorStart(options, request)), "invalid executable").ConfigureAwait(false);
                    AssertEqual("runtime_command_invalid", failure.ReasonCode, "the executable path must be absolute and exist");
                    AssertFalse(journalRecordRunning(options, request.JobId), "invalid command does not leave a running child");
                }
            }).ConfigureAwait(false);
        }

        private static TestArea CreateArea()
        {
            string root = Path.Combine(Path.GetTempPath(), "armada_harbor_runner_" + Guid.NewGuid().ToString("N"));
            string workingRoot = Path.Combine(root, "work");
            string work = Path.Combine(workingRoot, "dock");
            string state = Path.Combine(root, "state");
            Directory.CreateDirectory(work);
            return new TestArea(root, workingRoot, work, state);
        }

        private static HarborSupervisorOptions Options(TestArea area)
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The unit process path is unavailable.");
            return new HarborSupervisorOptions
            {
                StateDirectory = area.State,
                WorkingRoots = new List<string> { area.WorkingRoot },
                Commands = new Dictionary<string, string>(StringComparer.Ordinal) { ["test"] = executable },
                MaxRetainedJobs = 64,
                MaxSpoolBytes = 16L * 1024L * 1024L
            };
        }

        private static HarborLaunchRequest Request(TestArea area, string jobId, string launchKey, string mode, params string[] arguments)
        {
            List<string> resolved = new List<string>();
            string? processPath = Environment.ProcessPath;
            if (processPath != null && String.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                resolved.Add(typeof(Program).Assembly.Location);
            resolved.Add("--harbor-runner-child");
            resolved.Add(mode);
            resolved.AddRange(arguments);
            return new HarborLaunchRequest
            {
                JobId = jobId,
                LaunchKey = launchKey,
                Runtime = "test",
                WorkingDirectory = area.Work,
                PromptViaStdin = false,
                Arguments = resolved,
                Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            };
        }

        private static async Task<HarborLocalJobRecord> WaitForRecordAsync(HarborJobJournal journal, string jobId, Func<HarborLocalJobRecord, bool> ready)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                HarborLocalJobRecord record = journal.Read(jobId);
                if (ready(record)) return record;
                await Task.Delay(20).ConfigureAwait(false);
            }
            throw new TimeoutException("The Harbor child did not reach its recorded running state within five seconds.");
        }

        private static async Task<bool> WaitForOutputAsync(HarborJobJournal journal, string jobId)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                HarborLocalJobRecord record = journal.Read(jobId);
                if (record.NextOutputSequence > record.FirstOutputSequence) return true;
                await Task.Delay(20).ConfigureAwait(false);
            }
            return false;
        }

        private static async Task<HarborJobJournalException> CaptureJournalFailureAsync(Func<Task<int>> action, string label)
        {
            try { await action().ConfigureAwait(false); }
            catch (HarborJobJournalException exception) { return exception; }
            throw new InvalidOperationException("Expected a named Harbor journal failure for " + label + ".");
        }

        private static HarborJobJournalException CaptureJournalFailure(Func<List<HarborOutput>> action, string label)
        {
            try { action(); }
            catch (HarborJobJournalException exception) { return exception; }
            throw new InvalidOperationException("Expected a named Harbor journal failure for " + label + ".");
        }

        private static bool journalRecordRunning(HarborSupervisorOptions options, string jobId)
        {
            HarborLocalJobRecord record = new HarborJobJournal(options).Read(jobId);
            return record.IsRunning;
        }

        private static string Decode(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private sealed class TestArea : IDisposable
        {
            public string Root { get; }
            public string WorkingRoot { get; }
            public string Work { get; }
            public string State { get; }
            public bool PreserveForDiagnosis { get; set; }

            public TestArea(string root, string workingRoot, string work, string state)
            {
                Root = root;
                WorkingRoot = workingRoot;
                Work = work;
                State = state;
            }

            public void Dispose()
            {
                if (PreserveForDiagnosis)
                {
                    Trace.TraceWarning("Harbor runner test retained temporary area for diagnosis: " + Root);
                    return;
                }
                try { Directory.Delete(Root, true); }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    Trace.TraceWarning("Harbor runner test cleanup could not remove temporary area '" + Root + "': " + exception.GetType().Name);
                }
            }
        }
    }
}
