namespace Armada.Runtimes.Harbor
{
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core.Harbor;

    /// <summary>Local job journal output operations.</summary>
    public sealed partial class HarborJobJournal
    {
        /// <summary>Read a bounded contiguous range of output records.</summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="fromSequence">First requested sequence.</param>
        /// <param name="maxRecords">Maximum number of records to return.</param>
        /// <returns>Output records in sequence order.</returns>
        public List<HarborOutput> ReadOutput(string jobId, long fromSequence, int maxRecords = 256)
        {
            if (fromSequence < 0 || maxRecords < 1 || maxRecords > 4096) throw Failure("output_range_invalid", "The output range is invalid.");
            HarborLocalJobRecord record = ReadRecord(jobId);
            if (record.OutputGap) throw Failure("output_gap", "The job output spool is incomplete.");
            if (fromSequence < record.FirstOutputSequence || fromSequence > record.NextOutputSequence)
                throw Failure("output_range_invalid", "The requested output sequence is outside the retained range.");
            string path = OutputPath(record.JobId);
            EnsureFile(path);
            if (new FileInfo(path).Length > _Options.MaxSpoolBytes)
                throw Failure("output_limit_exceeded", "The output spool exceeds its configured limit.");
            List<HarborOutput> output = ReadOutputEntries(path, record, jobId, fromSequence, maxRecords);
            if (fromSequence < record.NextOutputSequence && output.Count == 0)
                throw Failure("output_sequence_missing", "The requested output sequence is not retained.");
            return output;
        }
    }
}
