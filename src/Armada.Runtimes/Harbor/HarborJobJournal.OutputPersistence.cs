namespace Armada.Runtimes.Harbor
{
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Harbor;

    /// <summary>Append-only output spool operations for the job journal.</summary>
    public sealed partial class HarborJobJournal
    {
        internal bool AppendOutput(HarborLocalJobRecord record, HarborOutputStreamEnum outputStream, string data)
        {
            lock (_Sync)
            {
                if (record.OutputGap) return false;
                string path = OutputPath(record.JobId);
                EnsureFile(path);
                HarborOutput output = new HarborOutput
                {
                    JobId = record.JobId,
                    Sequence = record.NextOutputSequence,
                    Stream = outputStream,
                    Data = data
                };
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(output, _JsonOptions) + "\n");
                FileInfo info = new FileInfo(path);
                if (info.Length + bytes.Length > _Options.MaxSpoolBytes)
                {
                    record.OutputGap = true;
                    record.FailureReason = "harbor_output_limit_exceeded";
                    PersistRecord(record);
                    return false;
                }
                using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                record.NextOutputSequence++;
                PersistRecord(record);
                return true;
            }
        }
    }
}
