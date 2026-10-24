namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Threading.Tasks;

    /// <summary>Drains both redirected streams before the supervisor writes terminal metadata.</summary>
    public sealed partial class HarborJobSupervisor
    {
        private static async Task DrainOutputTasksAsync(HarborJobJournal journal, HarborLocalJobRecord record, Task? stdoutReader, Task? stderrReader, Task? promptWriter)
        {
            if (stdoutReader == null || stderrReader == null)
                journal.MarkOutputGap(record, "harbor_output_capture_incomplete");
            await ObserveOutputTaskAsync(journal, record, stdoutReader).ConfigureAwait(false);
            await ObserveOutputTaskAsync(journal, record, stderrReader).ConfigureAwait(false);
            await ObserveOutputTaskAsync(journal, record, promptWriter).ConfigureAwait(false);
        }

        private static async Task ObserveOutputTaskAsync(HarborJobJournal journal, HarborLocalJobRecord record, Task? reader)
        {
            if (reader == null) return;
            try
            {
                await reader.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                string reason = exception is HarborJobJournalException journalException
                    ? journalException.ReasonCode
                    : "harbor_output_capture_failed";
                journal.MarkOutputGap(record, reason);
            }
        }
    }
}
