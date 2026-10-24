namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Harbor;

    /// <summary>Local job journal read operations.</summary>
    public sealed partial class HarborJobJournal
    {
        /// <summary>Read one retained record. Missing records fail with a named error.</summary>
        /// <param name="jobId">Job identifier.</param>
        /// <returns>The durable record.</returns>
        public HarborLocalJobRecord Read(string jobId) => ReadRecord(jobId);

        /// <summary>Read every retained record; malformed entries fail the whole enumeration.</summary>
        /// <returns>Records ordered by job identifier.</returns>
        public List<HarborLocalJobRecord> Enumerate()
        {
            lock (_Sync)
            {
                List<HarborLocalJobRecord> records = new List<HarborLocalJobRecord>();
                foreach (string directory in Directory.GetDirectories(_Root).OrderBy(path => path, StringComparer.Ordinal))
                {
                    EnsureNoLink(directory);
                    if (Path.GetFileName(directory).StartsWith(".pending-", StringComparison.Ordinal)) continue;
                    string recordPath = Path.Combine(directory, "job.json");
                    if (!File.Exists(recordPath)) throw Failure("job_metadata_missing", "A retained job has no metadata file.");
                    string jobId = Path.GetFileName(directory);
                    HarborLocalJobRecord record = ReadRecord(jobId);
                    records.Add(record);
                }
                if (records.Count > _Options.MaxRetainedJobs) throw Failure("job_limit_exceeded", "The journal contains more jobs than configured.");
                return records;
            }
        }
    }
}
