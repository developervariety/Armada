namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Settings;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Runtimes.Harbor;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

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

            await RunTest("Owned checkout operations enforce replay, path, head, and live-writer fences", async () =>
            {
                using (TestArea area = CreateArea())
                {
                    string source = Path.Combine(area.Root, "source");
                    string bare = Path.Combine(area.Root, "repository.git");
                    string checkouts = Path.Combine(area.WorkingRoot, "owned-checkouts");
                    Directory.CreateDirectory(source);
                    Directory.CreateDirectory(checkouts);
                    await RunGitFixtureAsync(source, "init").ConfigureAwait(false);
                    await RunGitFixtureAsync(source, "config", "user.name", "Harbor test").ConfigureAwait(false);
                    await RunGitFixtureAsync(source, "config", "user.email", "harbor-test@example.invalid").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(source, "README.md"), "owned checkout fixture\n").ConfigureAwait(false);
                    await RunGitFixtureAsync(source, "add", "README.md").ConfigureAwait(false);
                    await RunGitFixtureAsync(source, "commit", "-m", "fixture").ConfigureAwait(false);
                    await RunGitFixtureAsync(source, "branch", "fixture/inspection").ConfigureAwait(false);
                    await RunGitFixtureAsync(source, "remote", "add", "origin", source).ConfigureAwait(false);
                    string baseCommit = (await RunGitFixtureAsync(source, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    await RunGitFixtureAsync(area.Root, "clone", "--bare", source, bare).ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(source, "README.md"), "primary target advanced\n").ConfigureAwait(false);
                    await RunGitFixtureAsync(source, "add", "README.md").ConfigureAwait(false);
                    await RunGitFixtureAsync(source, "commit", "-m", "advance primary target").ConfigureAwait(false);
                    string primaryHead = (await RunGitFixtureAsync(source, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

                    HarborSupervisorOptions options = Options(area);
                    options.MaxRetainedJobs = 32;
                    options.MaxVerificationSpoolBytes = 32L * 1024L * 1024L;
                    HarborRepositoryGrant grant = new HarborRepositoryGrant
                    {
                        Id = "grant-test",
                        VesselId = "vessel-test",
                        TenantId = "tenant-test",
                        UserId = "user-test",
                        RepositoryPath = bare,
                        CheckoutRoot = checkouts,
                        WorkingDirectory = source,
                        RepositoryUrl = source,
                        VerificationCommands = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["large"] = RunnerChildCommand("verify-large", "5242880"),
                            ["hold"] = RunnerChildCommand("verify-hold"),
                            ["fail"] = RunnerChildCommand("verify-fail"),
                            ["rerun"] = RunnerChildCommand("verify-rerun")
                        }
                    };
                    HarborRunnerSettings settings = new HarborRunnerSettings
                    {
                        RunnerId = "runner-test",
                        Name = "Harbor workspace test",
                        LinkUri = "ws://localhost",
                        Credential = "private-workspace-test-credential",
                        Supervisor = options,
                        RepositoryGrants = new List<HarborRepositoryGrant> { grant }
                    };
                    CapturingHarborPullRequestService pullRequests = new CapturingHarborPullRequestService();
                    IGitService git = new GitService(new LoggingModule(), (platform, workingDirectory) =>
                    {
                        pullRequests.WorkingDirectory = workingDirectory;
                        return pullRequests;
                    });
                    HarborRunnerWorkspace workspace = new HarborRunnerWorkspace(settings, git);
                    HarborHandshakeAck ack = new HarborHandshakeAck
                    {
                        Accepted = true,
                        TenantId = grant.TenantId,
                        UserId = grant.UserId,
                        EnrollmentGeneration = 1,
                        ConnectionGeneration = 1
                    };
                    string checkoutId = "hco_" + Guid.NewGuid().ToString("N");
                    HarborCheckoutBinding binding = new HarborCheckoutBinding
                    {
                        RunnerId = settings.RunnerId,
                        EnrollmentGeneration = 1,
                        TenantId = grant.TenantId,
                        UserId = grant.UserId,
                        RepositoryGrantId = grant.Id,
                        RepositoryFingerprint = HarborRunnerWorkspace.RepositoryFingerprint(grant.RepositoryUrl),
                        CheckoutId = checkoutId,
                        BaseCommit = baseCommit,
                        BranchName = "agent/test",
                        Revision = "revision-1"
                    };
                    HarborWorkspaceOperationRequest repositoryInspect = WorkspaceRequest(settings, grant, binding, ack, HarborWorkspaceOperationEnum.InspectRepository, "inspect-repository", "unused-inspect-key");
                    repositoryInspect.Binding = null;
                    repositoryInspect.RequestedRevision = "fixture/inspection";
                    repositoryInspect.ExpectedRepositoryFingerprint = binding.RepositoryFingerprint;
                    HarborWorkspaceOperationResult repositoryResult = await workspace.ExecuteAsync(repositoryInspect, ack).ConfigureAwait(false);
                    AssertTrue(repositoryResult.Accepted, "repository inspection resolves the requested branch through the configured bare grant");
                    AssertEqual(baseCommit, repositoryResult.HeadCommit, "repository inspection returns the requested ref as a full commit identity");
                    AssertEqual(binding.RepositoryFingerprint, repositoryResult.RepositoryFingerprint, "repository inspection returns the verified origin fingerprint");
                    HarborWorkspaceOperationRequest provision = WorkspaceRequest(settings, grant, binding, ack, HarborWorkspaceOperationEnum.ProvisionCheckout, "provision-request", "provision-key");
                    provision.ExpectedHead = baseCommit;
                    provision.Detached = true;
                    HarborWorkspaceOperationResult provisioned = await workspace.ExecuteAsync(provision, ack).ConfigureAwait(false);
                    AssertTrue(provisioned.Accepted, "the configured grant provisions the exact checkout binding: " + provisioned.Reason);
                    AssertEqual(baseCommit, provisioned.HeadCommit, "the provisioned checkout is pinned to the expected full commit");

                    string checkoutPath = Path.Combine(checkouts, checkoutId);
                    string gitPointerPath = Path.Combine(checkoutPath, ".git");
                    string originalGitPointer = await File.ReadAllTextAsync(gitPointerPath).ConfigureAwait(false);
                    string decoyCheckout = Path.Combine(area.WorkingRoot, "decoy-worktree");
                    await RunGitFixtureAsync(bare, "worktree", "add", "--detach", decoyCheckout, baseCommit).ConfigureAwait(false);
                    try
                    {
                        await File.WriteAllTextAsync(gitPointerPath,
                            "gitdir: " + Path.Combine(bare, "worktrees", "decoy-worktree") + Environment.NewLine).ConfigureAwait(false);
                        HarborWorkspaceOperationRequest wrongGitIdentity = WorkspaceRequest(settings, grant, binding, ack,
                            HarborWorkspaceOperationEnum.InspectCheckout, "wrong-git-metadata", "unused-wrong-git-key");
                        HarborWorkspaceOperationResult identityRefusal = await workspace.ExecuteAsync(wrongGitIdentity, ack).ConfigureAwait(false);
                        AssertFalse(identityRefusal.Accepted, "the owned checkout refuses foreign linked-worktree metadata");
                        AssertEqual("workspace_checkout_git_identity_mismatch", identityRefusal.Reason,
                            "a mismatched but existing Git pointer has a stable named refusal");
                    }
                    finally
                    {
                        await File.WriteAllTextAsync(gitPointerPath, originalGitPointer).ConfigureAwait(false);
                    }
                    HarborWorkspaceOperationResult restoredIdentity = await workspace.ExecuteAsync(
                        WorkspaceRequest(settings, grant, binding, ack, HarborWorkspaceOperationEnum.InspectCheckout,
                            "restored-git-metadata", "unused-restored-git-key"), ack).ConfigureAwait(false);
                    AssertTrue(restoredIdentity.Accepted, "the valid checkout pointer remains usable after the refusal");

                    HarborWorkspaceOperationRequest largeVerification = WorkspaceRequest(settings, grant, binding, ack,
                        HarborWorkspaceOperationEnum.RunVerification, "large-verification-request", "large-verification-key");
                    largeVerification.ExpectedHead = baseCommit;
                    largeVerification.Verification = Verification("large", "large-check", 5 * 1024 * 1024);
                    HarborWorkspaceOperationResult largeResult = await workspace.ExecuteAsync(largeVerification, ack).ConfigureAwait(false);
                    AssertTrue(largeResult.Accepted, "the fixed configured verification command completes: " + largeResult.Reason);
                    AssertEqual(0, largeResult.ExitCode ?? -1, "the real child exit code is retained");
                    AssertFalse(largeResult.Truncated, "output larger than the normal 4 MiB check limit remains complete when explicitly allowed");
                    AssertEqual(5L * 1024L * 1024L, largeResult.StandardOutputLength, "full stdout length is recorded");
                    AssertEqual(5L * 1024L * 1024L, largeResult.StandardErrorLength, "full stderr length is recorded");
                    AssertTrue(largeResult.StartedUtc.HasValue && largeResult.DurationMilliseconds.HasValue, "verification timing facts are returned");
                    byte[] largeStdout = await ReadVerificationStreamAsync(workspace, settings, grant, binding, ack,
                        largeResult, baseCommit, "large-check", false).ConfigureAwait(false);
                    byte[] largeStderr = await ReadVerificationStreamAsync(workspace, settings, grant, binding, ack,
                        largeResult, baseCommit, "large-check", true).ConfigureAwait(false);
                    AssertEqual(5 * 1024 * 1024, largeStdout.Length, "chunk reads return all stdout bytes");
                    AssertEqual(5 * 1024 * 1024, largeStderr.Length, "chunk reads return all stderr bytes");
                    AssertEqual(largeResult.StandardOutputSha256, Convert.ToHexString(SHA256.HashData(largeStdout)).ToLowerInvariant(),
                        "the stored stdout hash matches the returned chunks");
                    AssertEqual(largeResult.StandardErrorSha256, Convert.ToHexString(SHA256.HashData(largeStderr)).ToLowerInvariant(),
                        "the stored stderr hash matches the returned chunks");
                    HarborWorkspaceOperationRequest wrongOutputOwner = WorkspaceRequest(settings, grant, binding, ack,
                        HarborWorkspaceOperationEnum.ReadVerificationOutput, "wrong-output-owner", "unused-owner-key");
                    wrongOutputOwner.UserId = "different-user";
                    wrongOutputOwner.VerificationOutput = new HarborVerificationOutputRequest
                    {
                        OutputId = largeResult.VerificationOutputId!, CheckId = "large-check", StandardError = false, Offset = 0, MaxBytes = 1024
                    };
                    HarborWorkspaceOperationResult wrongOwnerResult = await workspace.ExecuteAsync(wrongOutputOwner, ack).ConfigureAwait(false);
                    AssertFalse(wrongOwnerResult.Accepted, "another user cannot read a retained verification spool");
                    HarborWorkspaceOperationRequest wrongOutputCheck = WorkspaceRequest(settings, grant, binding, ack,
                        HarborWorkspaceOperationEnum.ReadVerificationOutput, "wrong-output-check", "unused-check-key");
                    wrongOutputCheck.VerificationOutput = new HarborVerificationOutputRequest
                    {
                        OutputId = largeResult.VerificationOutputId!, CheckId = "different-check", StandardError = false, Offset = 0, MaxBytes = 1024
                    };
                    HarborWorkspaceOperationResult wrongCheckResult = await workspace.ExecuteAsync(wrongOutputCheck, ack).ConfigureAwait(false);
                    AssertFalse(wrongCheckResult.Accepted, "output cannot be read under another check identity");

                    HarborWorkspaceOperationRequest failedVerification = WorkspaceRequest(settings, grant, binding, ack,
                        HarborWorkspaceOperationEnum.RunVerification, "failed-verification-request", "failed-verification-key");
                    failedVerification.ExpectedHead = baseCommit;
                    failedVerification.Verification = Verification("fail", "failed-check", 4096);
                    HarborWorkspaceOperationResult failedResult = await workspace.ExecuteAsync(failedVerification, ack).ConfigureAwait(false);
                    AssertTrue(failedResult.Accepted, "a non-zero verification still retains its complete result");
                    AssertEqual(1, failedResult.ExitCode ?? -1, "a failing child is never changed into a passing result");

                    HarborWorkspaceOperationRequest rerunVerification = WorkspaceRequest(settings, grant, binding, ack,
                        HarborWorkspaceOperationEnum.RunVerification, "rerun-verification-request", "rerun-verification-key");
                    rerunVerification.ExpectedHead = baseCommit;
                    rerunVerification.Verification = Verification("fail", "rerun-check", 4096);
                    rerunVerification.Verification.Rerun = new HarborVerificationRerunRequest
                    {
                        PriorOutputId = failedResult.VerificationOutputId!,
                        PriorCheckId = "failed-check",
                        FailingTestNames = new List<string> { "Namespace.TestCase" },
                        RerunCommandKey = "rerun",
                        RerunCommandSha256 = CommandHash(grant.VerificationCommands["rerun"])
                    };
                    HarborWorkspaceOperationResult rerunResult = await workspace.ExecuteAsync(rerunVerification, ack).ConfigureAwait(false);
                    AssertTrue(rerunResult.Accepted, "the runner builds the rerun from the retained failed output and fixed profile command: " + rerunResult.Reason);
                    AssertEqual(0, rerunResult.ExitCode ?? -1, "the planned rerun exit remains observable");
                    byte[] rerunOutput = await ReadVerificationStreamAsync(workspace, settings, grant, binding, ack,
                        rerunResult, baseCommit, "rerun-check", false).ConfigureAwait(false);
                    AssertContains("Namespace.TestCase", Encoding.UTF8.GetString(rerunOutput),
                        "the selected name reaches the configured profile rerun through its bounded environment variable");

                    HarborWorkspaceOperationRequest heldVerification = WorkspaceRequest(settings, grant, binding, ack,
                        HarborWorkspaceOperationEnum.RunVerification, "held-verification-request", "held-verification-key");
                    heldVerification.ExpectedHead = baseCommit;
                    heldVerification.Verification = Verification("hold", "held-check", 4096);
                    heldVerification.Verification.TimeoutSeconds = 2;
                    Task<HarborWorkspaceOperationResult> heldTask = workspace.ExecuteAsync(heldVerification, ack);
                    await WaitForFileAsync(Path.Combine(checkoutPath, ".harbor-verification-ready")).ConfigureAwait(false);
                    HarborWorkspaceOperationRequest blockedWrite = WorkspaceRequest(settings, grant, binding, ack,
                        HarborWorkspaceOperationEnum.WriteText, "during-verification-write", "during-verification-write-key");
                    blockedWrite.ExpectedHead = baseCommit;
                    blockedWrite.RelativePath = "must-not-write-during-verification.txt";
                    blockedWrite.Text = "writer is live";
                    HarborWorkspaceOperationResult blockedVerificationWrite = await workspace.ExecuteAsync(blockedWrite, ack).ConfigureAwait(false);
                    AssertFalse(blockedVerificationWrite.Accepted, "checkout mutation is held while the verification process may still write");
                    AssertEqual("workspace_verification_writer_uncertain", blockedVerificationWrite.Reason, "the live verification hold is named");
                    AssertFalse(File.Exists(Path.Combine(checkoutPath, "must-not-write-during-verification.txt")), "the refused write leaves checkout files unchanged");
                    HarborWorkspaceOperationResult heldResult = await heldTask.ConfigureAwait(false);
                    AssertTrue(heldResult.Accepted && heldResult.TimedOut && heldResult.KillConfirmed,
                        "the timeout result records a confirmed process-tree stop");
                    HarborWorkspaceOperationResult afterVerificationWrite = await workspace.ExecuteAsync(blockedWrite, ack).ConfigureAwait(false);
                    AssertTrue(afterVerificationWrite.Accepted, "the same operation key can run after the verification writer has a confirmed stop");

                    HarborWorkspaceOperationRequest primaryInspect = WorkspaceRequest(settings, grant, binding, ack, HarborWorkspaceOperationEnum.Git, "inspect-primary-target", "unused-primary-inspect-key");
                    primaryInspect.Git = new HarborGitOperationRequest { Operation = HarborGitOperationEnum.GetHeadCommitHash, UseWorkingDirectory = true };
                    HarborWorkspaceOperationResult primaryResult = await workspace.ExecuteAsync(primaryInspect, ack).ConfigureAwait(false);
                    AssertTrue(primaryResult.Accepted, "the configured primary target is used only after origin verification");
                    AssertEqual(primaryHead, primaryResult.Git?.Text, "the typed Git read observes the configured primary target");

                    HarborWorkspaceOperationRequest write = WorkspaceRequest(settings, grant, binding, ack, HarborWorkspaceOperationEnum.WriteText, "write-request", "write-key");
                    write.ExpectedHead = baseCommit;
                    write.RelativePath = "result.txt";
                    write.Text = "one durable write";
                    HarborWorkspaceOperationResult written = await workspace.ExecuteAsync(write, ack).ConfigureAwait(false);
                    AssertTrue(written.Accepted, "the first bound file write succeeds");
                    HarborWorkspaceOperationRequest emptyWrite = WorkspaceRequest(settings, grant, binding, ack,
                        HarborWorkspaceOperationEnum.WriteBytes, "write-empty-request", "write-empty-key");
                    emptyWrite.ExpectedHead = baseCommit;
                    emptyWrite.RelativePath = "empty.bin";
                    emptyWrite.BytesBase64 = String.Empty;
                    HarborWorkspaceOperationResult emptyWritten = await workspace.ExecuteAsync(emptyWrite, ack).ConfigureAwait(false);
                    AssertTrue(emptyWritten.Accepted, "an empty byte payload creates a zero-byte checkout file");
                    AssertEqual(0L, new FileInfo(Path.Combine(checkoutPath, "empty.bin")).Length, "the empty byte payload is persisted as zero bytes");
                    HarborWorkspaceOperationRequest emptyRead = WorkspaceRequest(settings, grant, binding, ack,
                        HarborWorkspaceOperationEnum.ReadBytes, "read-empty-request", "read-empty-key");
                    emptyRead.RelativePath = "empty.bin";
                    emptyRead.MaxBytes = 16;
                    HarborWorkspaceOperationResult emptyReadResult = await workspace.ExecuteAsync(emptyRead, ack).ConfigureAwait(false);
                    AssertTrue(emptyReadResult.Accepted, "a zero-byte file can be read through the typed operation");
                    AssertEqual(0L, emptyReadResult.Length, "the typed read reports the empty file length");
                    AssertEqual(String.Empty, emptyReadResult.BytesBase64, "the typed read returns the empty base64 payload");
                    HarborWorkspaceOperationRequest retry = WorkspaceRequest(settings, grant, binding, ack, HarborWorkspaceOperationEnum.WriteText, "retry-request", "write-key");
                    retry.ExpectedHead = baseCommit;
                    retry.RelativePath = "result.txt";
                    retry.Text = write.Text;
                    retry.ConnectionGeneration = 2;
                    retry.CorrelationId = "retry-correlation";
                    retry.TraceParent = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01";
                    HarborHandshakeAck retryAck = new HarborHandshakeAck
                    {
                        Accepted = true,
                        TenantId = ack.TenantId,
                        UserId = ack.UserId,
                        EnrollmentGeneration = ack.EnrollmentGeneration,
                        ConnectionGeneration = 2
                    };
                    HarborWorkspaceOperationResult replayed = await workspace.ExecuteAsync(retry, retryAck).ConfigureAwait(false);
                    AssertTrue(replayed.Accepted, "the same completed operation is replayed across connection generations");
                    AssertEqual("retry-request", replayed.RequestId, "replay carries the current request identity");
                    AssertEqual("one durable write", await File.ReadAllTextAsync(Path.Combine(checkoutPath, "result.txt")).ConfigureAwait(false), "replay does not execute a second write");

                    HarborWorkspaceOperationRequest conflict = WorkspaceRequest(settings, grant, binding, ack, HarborWorkspaceOperationEnum.WriteText, "conflict-request", "write-key");
                    conflict.ExpectedHead = baseCommit;
                    conflict.RelativePath = "result.txt";
                    conflict.Text = "different content";
                    HarborWorkspaceOperationResult keyConflict = await workspace.ExecuteAsync(conflict, ack).ConfigureAwait(false);
                    AssertFalse(keyConflict.Accepted, "a replay key cannot name different mutation content");
                    AssertEqual("workspace_operation_key_conflict", keyConflict.Reason, "semantic key conflicts have a stable refusal");

                    HarborWorkspaceOperationRequest staleBinding = WorkspaceRequest(settings, grant, binding, ack, HarborWorkspaceOperationEnum.InspectCheckout, "stale-binding-request", "unused-stale-binding-key");
                    staleBinding.Binding = new Armada.Core.Models.HarborCheckoutBinding
                    {
                        RunnerId = binding.RunnerId,
                        EnrollmentGeneration = binding.EnrollmentGeneration,
                        TenantId = binding.TenantId,
                        UserId = binding.UserId,
                        RepositoryGrantId = binding.RepositoryGrantId,
                        RepositoryFingerprint = binding.RepositoryFingerprint,
                        CheckoutId = binding.CheckoutId,
                        BaseCommit = binding.BaseCommit,
                        BranchName = binding.BranchName,
                        Revision = "revision-2"
                    };
                    HarborWorkspaceOperationResult staleBindingResult = await workspace.ExecuteAsync(staleBinding, ack).ConfigureAwait(false);
                    AssertFalse(staleBindingResult.Accepted, "a stale checkout revision cannot inspect the current checkout as its own");
                    AssertEqual("workspace_checkout_not_ready", staleBindingResult.Reason, "a stale checkout revision is refused by name");

                    HarborWorkspaceOperationRequest staleHead = WorkspaceRequest(settings, grant, binding, ack, HarborWorkspaceOperationEnum.WriteText, "stale-head-request", "stale-head-key");
                    staleHead.ExpectedHead = new string('0', baseCommit.Length);
                    staleHead.RelativePath = "stale.txt";
                    staleHead.Text = "must not write";
                    HarborWorkspaceOperationResult staleHeadResult = await workspace.ExecuteAsync(staleHead, ack).ConfigureAwait(false);
                    AssertFalse(staleHeadResult.Accepted, "a stale checkout head refuses mutation");
                    AssertEqual("workspace_checkout_head_conflict", staleHeadResult.Reason, "stale-head refusal is named");
                    AssertFalse(File.Exists(Path.Combine(checkoutPath, "stale.txt")), "stale-head refusal leaves the checkout unchanged");
                    HarborWorkspaceOperationResult staleHeadRetry = await workspace.ExecuteAsync(staleHead, ack).ConfigureAwait(false);
                    AssertFalse(staleHeadRetry.Accepted, "an operation without a durable completed result is never replayed as success");
                    AssertEqual("workspace_operation_uncertain", staleHeadRetry.Reason, "a retained pending operation has a named hold");

                    HarborWorkspaceOperationRequest escapedPath = WorkspaceRequest(settings, grant, binding, ack, HarborWorkspaceOperationEnum.WriteText, "escaped-path-request", "escaped-path-key");
                    escapedPath.ExpectedHead = baseCommit;
                    escapedPath.RelativePath = "../outside.txt";
                    escapedPath.Text = "must not escape";
                    HarborWorkspaceOperationResult escaped = await workspace.ExecuteAsync(escapedPath, ack).ConfigureAwait(false);
                    AssertFalse(escaped.Accepted, "a relative path cannot escape the owned checkout");
                    AssertFalse(File.Exists(Path.Combine(checkouts, "outside.txt")), "path refusal leaves the checkout root unchanged");

                    await RunGitFixtureAsync(checkoutPath, "config", "user.name", "Harbor test").ConfigureAwait(false);
                    await RunGitFixtureAsync(checkoutPath, "config", "user.email", "harbor-test@example.invalid").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(checkoutPath, "README.md"), "owned checkout advanced\n").ConfigureAwait(false);
                    await RunGitFixtureAsync(checkoutPath, "add", "README.md").ConfigureAwait(false);
                    await RunGitFixtureAsync(checkoutPath, "commit", "-m", "advance owned checkout").ConfigureAwait(false);
                    string ownedHead = (await RunGitFixtureAsync(checkoutPath, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    HarborWorkspaceOperationRequest repositoryHeadRead = WorkspaceRequest(settings, grant, binding, ack, HarborWorkspaceOperationEnum.Git, "inspect-repository-head", "unused-repository-head-key");
                    repositoryHeadRead.Git = new HarborGitOperationRequest { Operation = HarborGitOperationEnum.GetHeadCommitHash, UseRepository = true };
                    HarborWorkspaceOperationResult repositoryHeadResult = await workspace.ExecuteAsync(repositoryHeadRead, ack).ConfigureAwait(false);
                    AssertTrue(repositoryHeadResult.Accepted, "the repository suffix selects the configured repository path");
                    AssertEqual(baseCommit, repositoryHeadResult.Git?.Text, "repository-scoped reads do not inspect the advanced owned checkout");
                    AssertFalse(String.Equals(ownedHead, repositoryHeadResult.Git?.Text, StringComparison.OrdinalIgnoreCase), "the repository and source checkout are distinct test targets");
                    HarborWorkspaceOperationRequest repositoryHeadRefRead = WorkspaceRequest(settings, grant, binding, ack,
                        HarborWorkspaceOperationEnum.Git, "inspect-repository-head-ref", "unused-repository-head-ref-key");
                    repositoryHeadRefRead.Git = new HarborGitOperationRequest
                    {
                        Operation = HarborGitOperationEnum.GetRepositoryHeadRef,
                        UseRepository = true
                    };
                    HarborWorkspaceOperationResult repositoryHeadRefResult = await workspace.ExecuteAsync(repositoryHeadRefRead, ack).ConfigureAwait(false);
                    AssertTrue(repositoryHeadRefResult.Accepted && repositoryHeadRefResult.Git?.Text?.StartsWith("refs/heads/", StringComparison.Ordinal) == true,
                        "the bare repository exposes its symbolic branch for a safe no-op head mutation");
                    string repositoryBranch = repositoryHeadRefResult.Git!.Text!.Substring("refs/heads/".Length);

                    HarborWorkspaceOperationRequest staleRepositoryHead = WorkspaceRequest(settings, grant, binding, ack,
                        HarborWorkspaceOperationEnum.Git, "stale-repository-head", "stale-repository-head-key");
                    staleRepositoryHead.ExpectedHead = ownedHead;
                    staleRepositoryHead.Git = new HarborGitOperationRequest
                    {
                        Operation = HarborGitOperationEnum.SetRepositoryHead,
                        BranchName = repositoryBranch,
                        UseRepository = true
                    };
                    HarborWorkspaceOperationResult staleRepositoryResult = await workspace.ExecuteAsync(staleRepositoryHead, ack).ConfigureAwait(false);
                    AssertFalse(staleRepositoryResult.Accepted, "repository mutation compares against repository HEAD, not the advanced checkout HEAD");
                    AssertEqual("workspace_checkout_head_conflict", staleRepositoryResult.Reason, "a mismatched selected-target head has a stable refusal");

                    HarborWorkspaceOperationRequest currentRepositoryHead = WorkspaceRequest(settings, grant, binding, ack,
                        HarborWorkspaceOperationEnum.Git, "current-repository-head", "current-repository-head-key");
                    currentRepositoryHead.ExpectedHead = baseCommit;
                    currentRepositoryHead.Git = new HarborGitOperationRequest
                    {
                        Operation = HarborGitOperationEnum.SetRepositoryHead,
                        BranchName = repositoryBranch,
                        UseRepository = true
                    };
                    HarborWorkspaceOperationResult currentRepositoryResult = await workspace.ExecuteAsync(currentRepositoryHead, ack).ConfigureAwait(false);
                    AssertTrue(currentRepositoryResult.Accepted, "repository mutation succeeds when pinned to repository HEAD: " + currentRepositoryResult.Reason);

                    HarborWorkspaceOperationRequest stalePrimaryHead = WorkspaceRequest(settings, grant, binding, ack,
                        HarborWorkspaceOperationEnum.Git, "stale-primary-head", "stale-primary-head-key");
                    stalePrimaryHead.ExpectedHead = ownedHead;
                    stalePrimaryHead.Git = new HarborGitOperationRequest
                    {
                        Operation = HarborGitOperationEnum.Fetch,
                        UseWorkingDirectory = true
                    };
                    HarborWorkspaceOperationResult stalePrimaryResult = await workspace.ExecuteAsync(stalePrimaryHead, ack).ConfigureAwait(false);
                    AssertFalse(stalePrimaryResult.Accepted, "primary-target mutation compares against the configured working-directory HEAD");
                    AssertEqual("workspace_checkout_head_conflict", stalePrimaryResult.Reason, "a stale primary-target head has a stable refusal");

                    HarborWorkspaceOperationRequest currentPrimaryHead = WorkspaceRequest(settings, grant, binding, ack,
                        HarborWorkspaceOperationEnum.Git, "current-primary-head", "current-primary-head-key");
                    currentPrimaryHead.ExpectedHead = primaryHead;
                    currentPrimaryHead.Git = new HarborGitOperationRequest
                    {
                        Operation = HarborGitOperationEnum.Fetch,
                        UseWorkingDirectory = true
                    };
                    HarborWorkspaceOperationResult currentPrimaryResult = await workspace.ExecuteAsync(currentPrimaryHead, ack).ConfigureAwait(false);
                    AssertTrue(currentPrimaryResult.Accepted, "primary-target mutation succeeds when pinned to its current HEAD: " + currentPrimaryResult.Reason);

                    HarborWorkspaceOperationRequest mergedPullRequest = WorkspaceRequest(settings, grant, binding, ack, HarborWorkspaceOperationEnum.Git, "inspect-pr-merge", "unused-pr-merge-key");
                    mergedPullRequest.Git = new HarborGitOperationRequest
                    {
                        Operation = HarborGitOperationEnum.IsPrMerged,
                        PullRequestUrl = "https://github.com/armada-tests/pr-base/pull/17",
                        UseRepository = true
                    };
                    HarborWorkspaceOperationResult mergedResult = await workspace.ExecuteAsync(mergedPullRequest, ack).ConfigureAwait(false);
                    AssertTrue(mergedResult.Accepted && mergedResult.Git?.Flag == true, "PR reconciliation can query the remote repository from its repository suffix");
                    AssertEqual(bare, pullRequests.WorkingDirectory, "PR status uses the bare repository, not a checkout or local path");

                    HarborJobJournal jobs = new HarborJobJournal(options);
                    HarborLaunchRequest heldLaunch = Request(area, "owned-checkout-held-job", "owned-checkout-held-key", "hold");
                    heldLaunch.WorkingDirectory = checkoutPath;
                    heldLaunch.DockId = "dock-test";
                    heldLaunch.CheckoutBinding = binding;
                    Task<int> heldRun = HarborJobSupervisor.RunAsync(new HarborSupervisorStart(options, heldLaunch, grant));
                    HarborLocalJobRecord? heldRecord = null;
                    try
                    {
                        heldRecord = await WaitForRecordAsync(jobs, heldLaunch.JobId, value => value.IsRunning && value.ProcessId.HasValue).ConfigureAwait(false);
                        HarborWorkspaceOperationRequest blockedJobWrite = WorkspaceRequest(settings, grant, binding, ack, HarborWorkspaceOperationEnum.WriteText, "blocked-write-request", "blocked-write-key");
                        blockedJobWrite.ExpectedHead = ownedHead;
                        blockedJobWrite.RelativePath = "blocked.txt";
                        blockedJobWrite.Text = "agent still owns checkout";
                        HarborWorkspaceOperationResult blocked = await workspace.ExecuteAsync(blockedJobWrite, ack).ConfigureAwait(false);
                        AssertFalse(blocked.Accepted, "workspace mutations are refused while a bound writer has no confirmed stop");
                        AssertEqual("checkout_job_unconfirmed", blocked.Reason, "the live-writer hold is named");
                        AssertFalse(File.Exists(Path.Combine(checkoutPath, "blocked.txt")), "the live-writer refusal does not change checkout files");
                    }
                    finally
                    {
                        if (!heldRun.IsCompleted)
                        {
                            try { jobs.WriteStopRequest(heldLaunch.JobId, heldLaunch.LaunchKey, 0); }
                            catch (HarborJobJournalException exception)
                            {
                                Trace.TraceWarning("Harbor workspace test could not request child stop: " + exception.ReasonCode);
                            }
                            Task completed = await Task.WhenAny(heldRun, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                            if (Object.ReferenceEquals(completed, heldRun)) await heldRun.ConfigureAwait(false);
                            else area.PreserveForDiagnosis = true;
                        }
                        if (heldRun.IsCompleted) await heldRun.ConfigureAwait(false);
                    }
                    AssertTrue(jobs.Read(heldLaunch.JobId).StopConfirmed, "checkout mutation resumes only after durable stop confirmation");
                    AssertNotNull(heldRecord?.CheckoutId, "the child journal retains its checkout identity");
                    AssertEqual(binding.Revision, heldRecord?.CheckoutRevision, "the child journal retains the immutable checkout revision");
                    HarborWorkspaceOperationRequest afterStop = WorkspaceRequest(settings, grant, binding, ack, HarborWorkspaceOperationEnum.WriteText, "after-stop-request", "after-stop-key");
                    afterStop.ExpectedHead = ownedHead;
                    afterStop.RelativePath = "after-stop.txt";
                    afterStop.Text = "writer confirmed stopped";
                    HarborWorkspaceOperationResult afterStopResult = await workspace.ExecuteAsync(afterStop, ack).ConfigureAwait(false);
                    AssertTrue(afterStopResult.Accepted, "a new operation can mutate after the exact child exit is confirmed");
                }
            }).ConfigureAwait(false);

            await RunTest("HarborCheckoutService provisions one persisted binding and resumes its exact runtime checkout", ProvisionFacade_PinsGeneratedIdentityAndResumes);
        }

        private async Task ProvisionFacade_PinsGeneratedIdentityAndResumes()
        {
            using (TestArea area = CreateArea())
            using (TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                string source = Path.Combine(area.Root, "facade-source");
                string bare = Path.Combine(area.Root, "facade-repository.git");
                string checkoutRoot = Path.Combine(area.WorkingRoot, "facade-checkouts");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(checkoutRoot);
                await RunGitFixtureAsync(source, "init").ConfigureAwait(false);
                await RunGitFixtureAsync(source, "config", "user.name", "Harbor facade test").ConfigureAwait(false);
                await RunGitFixtureAsync(source, "config", "user.email", "harbor-facade@example.invalid").ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(source, "README.md"), "facade checkout fixture\n").ConfigureAwait(false);
                await RunGitFixtureAsync(source, "add", "README.md").ConfigureAwait(false);
                await RunGitFixtureAsync(source, "commit", "-m", "facade fixture").ConfigureAwait(false);
                await RunGitFixtureAsync(source, "branch", "fixture/facade").ConfigureAwait(false);
                await RunGitFixtureAsync(area.Root, "clone", "--bare", source, bare).ConfigureAwait(false);
                string baseCommit = (await RunGitFixtureAsync(source, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

                string tenantId = Armada.Core.Constants.DefaultTenantId;
                string userId = Armada.Core.Constants.DefaultUserId;
                Vessel vessel = await database.Driver.Vessels.CreateAsync(new Vessel("Harbor facade vessel", source)
                {
                    TenantId = tenantId,
                    UserId = userId,
                    DefaultBranch = "fixture/facade"
                }).ConfigureAwait(false);
                string runnerId = "runner-facade-" + Guid.NewGuid().ToString("N");
                string grantId = "grant-facade-" + Guid.NewGuid().ToString("N");
                AssertTrue(await database.Driver.HarborRunnerEnrollments.TryEnrollAsync(new HarborRunnerEnrollment
                {
                    RunnerId = runnerId,
                    TenantId = tenantId,
                    UserId = userId,
                    AuthMethod = "unit-test",
                    Generation = 1,
                    Active = true
                }, 0).ConfigureAwait(false), "the test runner enrollment is durable");

                HarborSupervisorOptions options = Options(area);
                HarborRepositoryGrant grant = new HarborRepositoryGrant
                {
                    Id = grantId,
                    VesselId = vessel.Id,
                    TenantId = tenantId,
                    UserId = userId,
                    RepositoryPath = bare,
                    CheckoutRoot = checkoutRoot,
                    RepositoryUrl = vessel.RepoUrl!
                };
                HarborRunnerSettings runnerSettings = new HarborRunnerSettings
                {
                    RunnerId = runnerId,
                    Name = "Harbor facade test",
                    LinkUri = "ws://localhost",
                    Credential = "private-facade-test-credential",
                    Supervisor = options,
                    RepositoryGrants = new List<HarborRepositoryGrant> { grant }
                };
                HarborRunnerWorkspace workspace = new HarborRunnerWorkspace(runnerSettings, new GitService(new LoggingModule()));
                HarborHandshakeAck ack = new HarborHandshakeAck
                {
                    Accepted = true,
                    TenantId = tenantId,
                    UserId = userId,
                    EnrollmentGeneration = 1,
                    ConnectionGeneration = 1
                };
                LocalWorkspaceChannel channel = new LocalWorkspaceChannel(workspace, ack);
                ArmadaSettings settings = new ArmadaSettings();
                settings.Harbor.Enabled = true;
                HarborCheckoutService service = new HarborCheckoutService(database.Driver, settings);
                service.SetChannel(channel);
                LoggingModule logging = new LoggingModule();
                HostAwareGitService hostGit = new HostAwareGitService(new GitService(logging), service);
                DockService dockLifecycle = new DockService(logging, database.Driver, settings, hostGit, service);
                service.SetDockLifecycle(dockLifecycle);
                string attemptId = "mission-attempt-" + Guid.NewGuid().ToString("N");

                Task<Dock> first = service.ProvisionAuxiliaryAsync(vessel, "agent/facade", runnerId, grantId,
                    "fixture/facade", false, attemptId);
                Task<Dock> second = service.ProvisionAuxiliaryAsync(vessel, "agent/facade", runnerId, grantId,
                    "fixture/facade", false, attemptId);
                Dock[] raced = await Task.WhenAll(first, second).ConfigureAwait(false);
                AssertEqual(raced[0].Id, raced[1].Id, "concurrent same-attempt callers receive one durable dock");
                AssertEqual(raced[0].HostCheckout!.CheckoutId, raced[1].HostCheckout!.CheckoutId, "the losing insert adopts the exact persisted runner checkout identity");
                AssertTrue(System.Text.RegularExpressions.Regex.IsMatch(raced[0].HostCheckout.CheckoutId,
                    @"\Ahco_[0-9a-f]{32}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                    "the facade emits the runtime journal's canonical opaque checkout ID");
                AssertTrue(raced[0].HostCheckout.IsComplete, "the persisted binding is complete under the runtime contract");
                Dock? persisted = await database.Driver.Docks.ReadByProvisionAttemptAsync(attemptId).ConfigureAwait(false);
                AssertNotNull(persisted?.HostCheckout);
                AssertEqual(raced[0].HostCheckout.CheckoutId, persisted!.HostCheckout!.CheckoutId, "the exact facade binding survives a database read");
                AssertTrue(Directory.Exists(Path.Combine(checkoutRoot, persisted.HostCheckout.CheckoutId)), "the real runner workspace created the bound checkout");

                int inspections = channel.Count(HarborWorkspaceOperationEnum.InspectRepository);
                Dock resumed = await service.ProvisionAuxiliaryAsync(vessel, "agent/facade", runnerId, grantId,
                    "must-not-be-resolved-after-attempt", false, attemptId).ConfigureAwait(false);
                AssertEqual(persisted.Id, resumed.Id, "restart-style retry reuses the stored dock");
                AssertEqual(persisted.HostCheckout.BaseCommit, resumed.HostCheckout!.BaseCommit, "retry keeps the original verified base");
                AssertEqual(inspections, channel.Count(HarborWorkspaceOperationEnum.InspectRepository), "retry does not resolve a new repository revision");

                InvalidOperationException modeMismatch = null!;
                try
                {
                    await service.ProvisionAuxiliaryAsync(vessel, "agent/facade", runnerId, grantId,
                        "must-not-be-resolved-after-attempt", true, attemptId).ConfigureAwait(false);
                }
                catch (InvalidOperationException exception) { modeMismatch = exception; }
                AssertNotNull(modeMismatch, "retry cannot silently change the persisted attached checkout into a detached checkout");
                AssertEqual("harbor_checkout_provision_mode_mismatch", modeMismatch.Message, "mode mismatch is a named held outcome");
                AssertEqual("agent/facade", (await RunGitFixtureAsync(Path.Combine(checkoutRoot, persisted.HostCheckout.CheckoutId),
                    "symbolic-ref", "--short", "-q", "HEAD").ConfigureAwait(false)).Trim(), "mode mismatch did not mutate the actual branch");

                await service.ReleaseAuxiliaryAsync(persisted, attemptId).ConfigureAwait(false);
                Dock? reclaimed = await database.Driver.Docks.ReadByProvisionAttemptAsync(attemptId).ConfigureAwait(false);
                AssertNotNull(reclaimed, "the cleanup keeps the durable dock lifecycle receipt");
                AssertFalse(reclaimed!.Active, "the real DockService confirms the auxiliary dock is inactive");
                AssertEqual(persisted.HostCheckout.CheckoutId, reclaimed.HostCheckout!.CheckoutId,
                    "cleanup preserves the immutable runner checkout receipt");
                AssertFalse(Directory.Exists(Path.Combine(checkoutRoot, persisted.HostCheckout.CheckoutId)),
                    "the production lifecycle removes the exact runner checkout after its durable phases complete");
            }
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

        private static HarborVerificationRequest Verification(string commandKey, string checkId, int outputLimitBytes)
        {
            return new HarborVerificationRequest
            {
                CommandKey = commandKey,
                ExpectedCommandSha256 = HarborVerificationProtocol.CommandSha256(commandKey == "large"
                    ? RunnerChildCommand("verify-large", "5242880")
                    : commandKey == "hold" ? RunnerChildCommand("verify-hold")
                    : commandKey == "fail" ? RunnerChildCommand("verify-fail")
                    : RunnerChildCommand("verify-rerun")),
                CheckId = checkId,
                TimeoutSeconds = 30,
                OutputLimitBytes = outputLimitBytes,
                AcceptedExitCodes = new List<int> { 0 }
            };
        }

        private static string RunnerChildCommand(params string[] arguments)
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The unit process path is unavailable.");
            List<string> commandParts = new List<string> { QuoteCommandPart(executable) };
            if (String.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                commandParts.Add(QuoteCommandPart(typeof(Program).Assembly.Location));
            commandParts.Add("--harbor-runner-child");
            foreach (string argument in arguments) commandParts.Add(QuoteCommandPart(argument));
            return String.Join(' ', commandParts);
        }

        private static string QuoteCommandPart(string value)
        {
            if (OperatingSystem.IsWindows()) return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
            return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        }

        private static string CommandHash(string command)
        {
            return HarborVerificationProtocol.CommandSha256(command);
        }

        private async Task<byte[]> ReadVerificationStreamAsync(
            HarborRunnerWorkspace workspace,
            HarborRunnerSettings settings,
            HarborRepositoryGrant grant,
            HarborCheckoutBinding binding,
            HarborHandshakeAck ack,
            HarborWorkspaceOperationResult completed,
            string expectedHead,
            string checkId,
            bool standardError)
        {
            using (MemoryStream output = new MemoryStream())
            {
                long offset = 0;
                long retained = standardError ? completed.StandardErrorRetainedBytes : completed.StandardOutputRetainedBytes;
                while (offset < retained)
                {
                    HarborWorkspaceOperationRequest read = WorkspaceRequest(settings, grant, binding, ack,
                        HarborWorkspaceOperationEnum.ReadVerificationOutput, "read-output-" + standardError + "-" + offset,
                        "unused-read-output-key");
                    read.ExpectedHead = expectedHead;
                    read.VerificationOutput = new HarborVerificationOutputRequest
                    {
                        OutputId = completed.VerificationOutputId!,
                        CheckId = checkId,
                        StandardError = standardError,
                        Offset = offset,
                        MaxBytes = 1024 * 1024
                    };
                    HarborWorkspaceOperationResult chunk = await workspace.ExecuteAsync(read, ack).ConfigureAwait(false);
                    AssertTrue(chunk.Accepted, "an authenticated owner can read its retained output chunk: " + chunk.Reason);
                    AssertEqual(completed.VerificationOutputId, chunk.VerificationOutputId, "the output id is bound to each chunk");
                    AssertEqual(checkId, chunk.VerificationCheckId, "the check id is bound to each chunk");
                    AssertEqual(standardError ? completed.StandardErrorLength : completed.StandardOutputLength, chunk.Length,
                        "each chunk names the full stream length");
                    byte[] bytes = Convert.FromBase64String(chunk.BytesBase64!);
                    AssertTrue(bytes.Length > 0 && bytes.Length <= 1024 * 1024, "each response stays within the chunk bound");
                    output.Write(bytes, 0, bytes.Length);
                    offset += bytes.Length;
                }
                return output.ToArray();
            }
        }

        private static async Task WaitForFileAsync(string path)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (File.Exists(path)) return;
                await Task.Delay(20).ConfigureAwait(false);
            }
            throw new TimeoutException("The verification child did not publish its ready marker within five seconds.");
        }

        private static HarborWorkspaceOperationRequest WorkspaceRequest(
            HarborRunnerSettings settings,
            HarborRepositoryGrant grant,
            Armada.Core.Models.HarborCheckoutBinding binding,
            HarborHandshakeAck ack,
            HarborWorkspaceOperationEnum operation,
            string requestId,
            string operationKey)
        {
            return new HarborWorkspaceOperationRequest
            {
                RequestId = requestId,
                OperationKey = operationKey,
                Operation = operation,
                Binding = binding,
                DockId = "dock-test",
                VesselId = grant.VesselId,
                RepositoryGrantId = grant.Id,
                TenantId = ack.TenantId!,
                UserId = ack.UserId!,
                RunnerId = settings.RunnerId,
                EnrollmentGeneration = ack.EnrollmentGeneration,
                ConnectionGeneration = ack.ConnectionGeneration,
                MaxBytes = 1024 * 1024,
                MaxEntries = 100
            };
        }

        private static async Task<string> RunGitFixtureAsync(string workingDirectory, params string[] arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
            using Process process = new Process { StartInfo = startInfo };
            if (!process.Start()) throw new InvalidOperationException("The Git fixture command did not start.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); }
                catch (Exception exception) when (exception is InvalidOperationException || exception is System.ComponentModel.Win32Exception || exception is NotSupportedException)
                {
                    Trace.TraceWarning("Harbor Git fixture cleanup could not stop its child: " + exception.GetType().Name);
                }
                throw new TimeoutException("The Git fixture command exceeded its deadline.");
            }
            string output = await stdout.ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("The Git fixture command failed: " + String.Join(' ', arguments) + " " + error);
            return output;
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

        private sealed class LocalWorkspaceChannel : IHarborWorkspaceOperations
        {
            private readonly HarborRunnerWorkspace _Workspace;
            private readonly HarborHandshakeAck _Ack;
            private readonly ConcurrentQueue<HarborWorkspaceOperationEnum> _Operations = new ConcurrentQueue<HarborWorkspaceOperationEnum>();

            public LocalWorkspaceChannel(HarborRunnerWorkspace workspace, HarborHandshakeAck ack)
            {
                _Workspace = workspace;
                _Ack = ack;
            }

            public int Count(HarborWorkspaceOperationEnum operation)
            {
                int count = 0;
                foreach (HarborWorkspaceOperationEnum item in _Operations)
                    if (item == operation) count++;
                return count;
            }

            public Task<HarborWorkspaceOperationResult> ExecuteAsync(AuthContext auth,
                HarborWorkspaceOperationRequest request, CancellationToken token = default)
            {
                if (!auth.IsAuthenticated || auth.TenantId != _Ack.TenantId || auth.UserId != _Ack.UserId)
                    throw new InvalidOperationException("The facade supplied an unexpected authenticated owner.");
                if (String.IsNullOrWhiteSpace(request.RequestId))
                {
                    request.RequestId = Guid.NewGuid().ToString("N");
                    request.CorrelationId = request.RequestId;
                }
                request.ConnectionGeneration = _Ack.ConnectionGeneration;
                _Operations.Enqueue(request.Operation);
                return _Workspace.ExecuteAsync(request, _Ack, token);
            }
        }

        private sealed class CapturingHarborPullRequestService : IPullRequestService
        {
            public string? WorkingDirectory { get; set; }

            public Task<string> CreateAsync(string branch, string baseBranch, string title, string body, CancellationToken token = default)
            {
                return Task.FromResult("https://github.com/armada-tests/pr-base/pull/18");
            }

            public Task<bool> IsMergedAsync(string prUrl, CancellationToken token = default)
            {
                return Task.FromResult(true);
            }
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
