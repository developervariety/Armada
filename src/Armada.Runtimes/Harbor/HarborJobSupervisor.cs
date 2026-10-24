namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Services;

    /// <summary>Owns the child launch, stream tasks, and confirmed terminal metadata.</summary>
    public sealed partial class HarborJobSupervisor
    {
        /// <summary>Run one supervisor job until its child exits or its stop request is confirmed.</summary>
        /// <param name="start">In-memory start envelope. Its request is never written to disk or logged.</param>
        /// <param name="token">Cancellation before child creation; cancellation does not kill a started child.</param>
        /// <returns>The child exit code, or a nonzero code when launch or supervision fails.</returns>
        public static async Task<int> RunAsync(HarborSupervisorStart start, CancellationToken token = default)
        {
            if (start == null) throw new ArgumentNullException(nameof(start));
            if (start.Options == null || start.Request == null) throw new HarborJobJournalException("supervisor_request_invalid", "The supervisor start envelope is incomplete.");
            token.ThrowIfCancellationRequested();
            HarborJobJournal journal = new HarborJobJournal(start.Options);
            HarborLaunchRequest request = start.Request;
            HarborLocalJobRecord record = journal.CreateIntent(request);
            Process? childProcess = null;
            bool childStarted = false;
            Task? stdoutReader = null;
            Task? stderrReader = null;
            Task? promptWriter = null;
            try
            {
                ProcessStartInfo startInfo = BuildStartInfo(start.Options, request);
                childProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                token.ThrowIfCancellationRequested();
                if (!childProcess.Start()) throw new HarborJobJournalException("child_start_failed", "The configured runtime did not start.");
                childStarted = true;
                try
                {
                    record.ProcessId = childProcess.Id;
                    record.ProcessStartedUtc = NormalizeUtc(childProcess.StartTime.ToUniversalTime());
                    if (OperatingSystem.IsLinux())
                    {
                        HarborLinuxProcessIdentity childIdentity = HarborLinuxProcessIdentity.Read(childProcess.Id);
                        record.ProcessLinuxStartTicks = childIdentity.StartTicks;
                        record.ProcessLinuxBootId = childIdentity.BootId;
                    }
                    record.IsRunning = true;
                }
                catch (Exception exception) when (exception is InvalidOperationException || exception is System.ComponentModel.Win32Exception || exception is NotSupportedException)
                {
                    record.IsRunning = true;
                    journal.MarkOutputGap(record, "child_start_identity_unavailable");
                    throw new HarborJobJournalException("child_start_identity_unavailable", "The child started but its process identity is incomplete.", exception);
                }
                journal.PersistRecord(record);
                stdoutReader = PumpOutputAsync(journal, record, childProcess.StandardOutput, HarborOutputStreamEnum.Stdout);
                stderrReader = PumpOutputAsync(journal, record, childProcess.StandardError, HarborOutputStreamEnum.Stderr);
                promptWriter = WritePromptAndCloseAsync(childProcess, request);
                await WaitForExitOrStopAsync(journal, record, childProcess, stdoutReader, stderrReader, promptWriter).ConfigureAwait(false);
                await promptWriter.ConfigureAwait(false);
                await Task.WhenAll(stdoutReader, stderrReader).ConfigureAwait(false);
                record.ExitCode = childProcess.ExitCode;
                record.IsRunning = false;
                record.StopConfirmed = true;
                journal.PersistRecord(record);
                return childProcess.ExitCode;
            }
            catch (Exception exception)
            {
                if (childStarted && childProcess != null)
                {
                    await ConfirmOrRecordUncertainExitAsync(journal, childProcess, record).ConfigureAwait(false);
                    await DrainOutputTasksAsync(journal, record, stdoutReader, stderrReader, promptWriter).ConfigureAwait(false);
                    if (stdoutReader == null || stderrReader == null)
                        journal.MarkOutputGap(record, "harbor_output_capture_incomplete");
                }
                else
                {
                    record.IsRunning = false;
                    record.StopConfirmed = true;
                }
                record.FailureReason = exception is HarborJobJournalException journalException
                    ? journalException.ReasonCode
                    : "harbor_supervisor_failed";
                try { journal.PersistRecord(record); }
                catch (HarborJobJournalException persistFailure)
                {
                    throw new HarborJobJournalException("supervisor_state_unconfirmed", "The supervisor could not persist the final job state.", persistFailure);
                }
                if (exception is OperationCanceledException) throw;
                throw new HarborJobJournalException(record.FailureReason, "The supervisor could not complete the job.", exception);
            }
            finally
            {
                childProcess?.Dispose();
            }
        }

    }
}
