namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Replays durable job output before sending a confirmed exit.</summary>
    public sealed partial class HarborRunnerClient
    {
        private void StartJobPump(
            string jobId,
            long fromSequence,
            Func<HarborMessage, Task> send,
            HashSet<string> exitSent,
            List<Task> background,
            CancellationToken token)
        {
            lock (exitSent) exitSent.Remove(jobId);
            background.Add(PumpJobAsync(jobId, fromSequence, send, exitSent, token));
        }

        private async Task PumpJobAsync(string jobId, long sequence, Func<HarborMessage, Task> send, HashSet<string> exitSent, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    HarborLocalJobRecord record = _Journal.Read(jobId);
                    if (await SendJobFailureAsync(record, jobId, send).ConfigureAwait(false)) return;
                    if (sequence > record.NextOutputSequence)
                        throw new HarborJobJournalException("output_sequence_invalid", "Replay starts beyond retained output.");
                    List<HarborOutput> output = _Journal.ReadOutput(jobId, sequence, 256);
                    foreach (HarborOutput item in output)
                    {
                        if (item.Sequence != sequence) throw new HarborJobJournalException("output_sequence_invalid", "The retained output sequence is not contiguous.");
                        await send(item).ConfigureAwait(false);
                        sequence++;
                    }
                    if (await SendTerminalAsync(record, jobId, sequence, output.Count, send, exitSent).ConfigureAwait(false)) return;
                    await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (HarborJobJournalException exception)
            {
                _Log("Harbor job replay fenced: " + jobId + " " + exception.ReasonCode);
                try { await send(new HarborError { JobId = jobId, Message = exception.ReasonCode }).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    _Log("Harbor job replay stopped: " + SafeJobId(jobId) + " harbor_link_closed");
                    return;
                }
            }
        }

        private async Task<bool> SendJobFailureAsync(HarborLocalJobRecord record, string jobId, Func<HarborMessage, Task> send)
        {
            if (!record.OutputGap && String.IsNullOrWhiteSpace(record.FailureReason)) return false;
            string reason = record.OutputGap ? "harbor_output_gap" : SafeReason(record.FailureReason);
            await send(new HarborError { JobId = jobId, Message = reason }).ConfigureAwait(false);
            _Log("Harbor job replay fenced: " + SafeJobId(jobId) + " " + reason);
            return true;
        }

        private async Task<bool> SendTerminalAsync(
            HarborLocalJobRecord record,
            string jobId,
            long sequence,
            int batchCount,
            Func<HarborMessage, Task> send,
            HashSet<string> exitSent)
        {
            if (!record.StopConfirmed) return false;
            if (sequence < record.NextOutputSequence && batchCount > 0) return false;
            if (sequence != record.NextOutputSequence || !record.ExitCode.HasValue)
            {
                await send(new HarborError { JobId = jobId, Message = "harbor_exit_proof_incomplete" }).ConfigureAwait(false);
                return true;
            }
            await send(new HarborExited { JobId = jobId, ExitCode = record.ExitCode.Value }).ConfigureAwait(false);
            lock (exitSent) exitSent.Add(jobId);
            return true;
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            return new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
        }
    }
}
