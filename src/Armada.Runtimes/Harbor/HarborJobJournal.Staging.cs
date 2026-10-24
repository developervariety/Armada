namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;

    /// <summary>Removes only unpublished staging directories while holding the admission lock.</summary>
    public sealed partial class HarborJobJournal
    {
        private void CleanupUnpublishedStagingDirectories()
        {
            foreach (string directory in Directory.GetDirectories(_Root, ".pending-*", SearchOption.TopDirectoryOnly))
            {
                EnsureNoLink(directory);
                HarborPrivateStorage.EnsurePrivateDirectory(directory);
                string name = Path.GetFileName(directory);
                if (name.Length != 41 || !name.StartsWith(".pending-", StringComparison.Ordinal))
                    throw Failure("pending_staging_name_invalid", "An unpublished staging directory has an invalid name.");
                for (int index = 9; index < name.Length; index++)
                    if (!Uri.IsHexDigit(name[index])) throw Failure("pending_staging_name_invalid", "An unpublished staging directory has an invalid name.");

                string? outputPath = null;
                string? recordPath = null;
                string? temporaryRecordPath = null;
                foreach (string entry in Directory.GetFileSystemEntries(directory))
                {
                    EnsureNoLink(entry);
                    string entryName = Path.GetFileName(entry);
                    if (Directory.Exists(entry)) throw Failure("pending_staging_unexpected_entry", "An unpublished staging directory contains an unexpected directory.");
                    if (String.Equals(entryName, "output.jsonl", StringComparison.Ordinal)) outputPath = entry;
                    else if (String.Equals(entryName, "job.json", StringComparison.Ordinal)) recordPath = entry;
                    else if (IsRecordTemporaryFile(entryName) && temporaryRecordPath == null) temporaryRecordPath = entry;
                    else throw Failure("pending_staging_unexpected_entry", "An unpublished staging directory contains an unexpected file.");
                }
                if (outputPath != null)
                {
                    EnsureFile(outputPath);
                    if (new FileInfo(outputPath).Length != 0)
                        throw Failure("pending_staging_has_output", "An unpublished staging directory contains output data.");
                }
                if (recordPath != null)
                {
                    EnsureFile(recordPath);
                    if (new FileInfo(recordPath).Length > 64 * 1024)
                        throw Failure("pending_staging_metadata_too_large", "Unpublished staging metadata exceeds its size limit.");
                }
                if (temporaryRecordPath != null)
                {
                    EnsureFile(temporaryRecordPath);
                    if (new FileInfo(temporaryRecordPath).Length > 64 * 1024)
                        throw Failure("pending_staging_metadata_too_large", "Unpublished staging metadata exceeds its size limit.");
                }
                if (outputPath != null) File.Delete(outputPath);
                if (recordPath != null) File.Delete(recordPath);
                if (temporaryRecordPath != null) File.Delete(temporaryRecordPath);
                Directory.Delete(directory, false);
            }
        }

        private static bool IsRecordTemporaryFile(string name)
        {
            if (name.Length != 29 || !name.StartsWith("job.json.", StringComparison.Ordinal) || !name.EndsWith(".tmp", StringComparison.Ordinal)) return false;
            for (int index = 9; index < 25; index++)
                if (!Uri.IsHexDigit(name[index])) return false;
            return true;
        }
    }
}
