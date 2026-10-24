namespace Armada.Runtimes.Harbor
{
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Persists child output before exposing its sequence cursor.</summary>
    public sealed partial class HarborJobSupervisor
    {
        private static async Task PumpOutputAsync(HarborJobJournal journal, HarborLocalJobRecord record, StreamReader reader, HarborOutputStreamEnum stream)
        {
            char[] buffer = new char[4096];
            string? failureReason = null;
            char? trailingHighSurrogate = null;
            while (true)
            {
                int count = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                if (count == 0)
                {
                    if (trailingHighSurrogate.HasValue)
                        failureReason ??= MarkInvalidOutput(journal, record, "harbor_output_unicode_invalid");
                    if (failureReason == null) return;
                    throw new HarborJobJournalException(failureReason, "The child output could not be retained completely.");
                }
                StringBuilder chunk = new StringBuilder(count + 1);
                int offset = 0;
                if (trailingHighSurrogate.HasValue)
                {
                    if (Char.IsLowSurrogate(buffer[0]))
                    {
                        chunk.Append(trailingHighSurrogate.Value);
                        chunk.Append(buffer[0]);
                        offset = 1;
                    }
                    else
                    {
                        failureReason ??= MarkInvalidOutput(journal, record, "harbor_output_unicode_invalid");
                    }
                    trailingHighSurrogate = null;
                }
                int available = count - offset;
                if (available > 0 && Char.IsHighSurrogate(buffer[count - 1]))
                {
                    trailingHighSurrogate = buffer[count - 1];
                    available--;
                }
                if (available > 0) chunk.Append(buffer, offset, available);
                string data = chunk.ToString();
                if (failureReason == null && data.Length > 0) failureReason = TryAppendOutput(journal, record, stream, data);
            }
        }

        private static string MarkInvalidOutput(HarborJobJournal journal, HarborLocalJobRecord record, string reason)
        {
            record.OutputGap = true;
            if (String.IsNullOrWhiteSpace(record.FailureReason)) record.FailureReason = reason;
            try { journal.MarkOutputGap(record, reason); }
            catch (HarborJobJournalException) { return "harbor_output_state_unconfirmed"; }
            return reason;
        }

        private static string? TryAppendOutput(HarborJobJournal journal, HarborLocalJobRecord record, HarborOutputStreamEnum stream, string data)
        {
            try
            {
                if (journal.AppendOutput(record, stream, data)) return null;
                return "output_limit_exceeded";
            }
            catch (Exception exception) when (exception is HarborJobJournalException || exception is IOException || exception is UnauthorizedAccessException)
            {
                string reason = exception is HarborJobJournalException journalException
                    ? journalException.ReasonCode
                    : "harbor_output_capture_failed";
                record.OutputGap = true;
                if (String.IsNullOrWhiteSpace(record.FailureReason)) record.FailureReason = reason;
                try { journal.MarkOutputGap(record, reason); }
                catch (HarborJobJournalException) { return "harbor_output_state_unconfirmed"; }
                return reason;
            }
        }

        private static async Task WritePromptAndCloseAsync(Process process, HarborLaunchRequest request)
        {
            try
            {
                if (request.PromptViaStdin && !String.IsNullOrEmpty(request.Prompt))
                    await process.StandardInput.WriteAsync(request.Prompt.AsMemory()).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                if (!process.HasExited) throw new HarborJobJournalException("prompt_delivery_failed", "The child did not accept its prompt.", exception);
            }
            finally
            {
                process.StandardInput.Close();
            }
        }
    }
}
