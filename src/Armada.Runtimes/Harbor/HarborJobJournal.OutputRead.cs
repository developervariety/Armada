namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Harbor;

    /// <summary>Validates and selects a contiguous output range from the local spool.</summary>
    public sealed partial class HarborJobJournal
    {
        private List<HarborOutput> ReadOutputEntries(string path, HarborLocalJobRecord record, string jobId, long fromSequence, int maxRecords)
        {
            List<HarborOutput> output = new List<HarborOutput>();
            long expected = record.FirstOutputSequence;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false, true), false))
                {
                    ReadOutputLines(reader, record, jobId, fromSequence, maxRecords, output, ref expected);
                    if (record.StopConfirmed && reader.ReadLine() != null)
                        throw Failure("output_spool_trailing_data", "The terminal output spool has data beyond its committed sequence range.");
                }
            }
            catch (HarborJobJournalException)
            {
                throw;
            }
            catch (Exception exception) when (exception is DecoderFallbackException || exception is IOException || exception is UnauthorizedAccessException)
            {
                throw Failure("output_spool_corrupt", "The output spool cannot be read as complete UTF-8 data.", exception);
            }
            if (expected != record.NextOutputSequence)
                throw Failure("output_sequence_missing", "The spool and metadata do not prove a contiguous output range.");
            return output;
        }

        private void ReadOutputLines(StreamReader reader, HarborLocalJobRecord record, string jobId, long fromSequence, int maxRecords,
            List<HarborOutput> output, ref long expected)
        {
            string? line;
            while (expected < record.NextOutputSequence && (line = reader.ReadLine()) != null)
            {
                HarborOutput item = DeserializeOutputLine(line);
                if (item.Sequence != expected || !String.Equals(item.JobId, jobId, StringComparison.Ordinal))
                    throw Failure("output_sequence_invalid", "The output spool does not contain a contiguous sequence.");
                expected++;
                if (item.Sequence >= fromSequence && output.Count < maxRecords) output.Add(item);
            }
        }

        private HarborOutput DeserializeOutputLine(string line)
        {
            try
            {
                return JsonSerializer.Deserialize<HarborOutput>(line, _JsonOptions)
                    ?? throw Failure("output_sequence_invalid", "The output spool contains an empty record.");
            }
            catch (JsonException exception)
            {
                throw Failure("output_spool_corrupt", "The output spool contains invalid data.", exception);
            }
        }
    }
}
