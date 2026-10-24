namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;

    /// <summary>Atomic metadata writes and record path helpers.</summary>
    public sealed partial class HarborJobJournal
    {
        private void AtomicWrite(string path, string content)
        {
            if (File.Exists(path)) EnsureFile(path);
            string temporaryPath = path + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                if (bytes.Length > 64 * 1024) throw Failure("journal_metadata_too_large", "The journal metadata exceeds its size limit.");
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
                {
                    HarborPrivateStorage.EnsurePrivateFile(temporaryPath);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                File.Move(temporaryPath, path, true);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is System.Security.SecurityException || exception is ArgumentException || exception is NotSupportedException)
            {
                throw Failure("journal_write_failed", "The local job journal could not be updated.", exception);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private void ValidateRecord(HarborLocalJobRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            ValidateJobId(record.JobId);
            ValidateLaunchKey(record.LaunchKey);
            if (record.SupervisorProcessId <= 0 || record.NextOutputSequence < record.FirstOutputSequence) throw Failure("job_metadata_invalid", "The job record failed validation.");
        }

        private string JobDirectory(string jobId) => Path.Combine(_Root, jobId);
        private string RecordPath(string jobId) => Path.Combine(JobDirectory(jobId), "job.json");
        private string OutputPath(string jobId) => Path.Combine(JobDirectory(jobId), "output.jsonl");
        private string StopPath(string jobId) => Path.Combine(JobDirectory(jobId), "stop.json");
        private static DateTime NormalizeUtc(DateTime value) => new DateTime(value.ToUniversalTime().Ticks - (value.ToUniversalTime().Ticks % 10), DateTimeKind.Utc);
        private static HarborJobJournalException Failure(string reason, string message, Exception? inner = null) => new HarborJobJournalException(reason, message, inner);
    }
}
