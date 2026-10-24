namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    /// <summary>Filesystem validation and private path operations for the job journal.</summary>
    public sealed partial class HarborJobJournal
    {
        private static void ValidateOptions(HarborSupervisorOptions options)
        {
            if (String.IsNullOrWhiteSpace(options.StateDirectory)) throw Failure("state_directory_missing", "A state directory is required.");
            try { HarborSupervisorStoragePolicy.Validate(options); }
            catch (ArgumentException exception) { throw Failure("storage_policy_invalid", "The spool reservation is outside its allowed range.", exception); }
            if (options.WorkingRoots == null || options.WorkingRoots.Count == 0) throw Failure("working_roots_missing", "At least one working root is required.");
            if (options.Commands == null || options.Commands.Count == 0) throw Failure("runtime_commands_missing", "At least one configured runtime command is required.");
        }

        private static string EnsurePrivateRoot(string path)
        {
            string full = Path.GetFullPath(path);
            string? parent = Path.GetPathRoot(full);
            if (String.IsNullOrEmpty(parent)) throw Failure("state_path_invalid", "The state directory path is invalid.");
            if (String.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw Failure("state_path_root_refused", "A filesystem root cannot be used as the job state directory.");
            string current = parent;
            string remainder = full.Substring(parent.Length);
            foreach (string segment in remainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (File.Exists(current) && !Directory.Exists(current)) throw Failure("state_path_not_directory", "A state path component is a file.");
                if (!Directory.Exists(current)) Directory.CreateDirectory(current);
                EnsureNoLink(current);
            }
            HarborPrivateStorage.EnsurePrivateDirectory(full);
            return full;
        }

        private static void EnsureNoLink(string path)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (Exception exception) when (exception is FileNotFoundException || exception is DirectoryNotFoundException)
            {
                throw Failure("journal_path_missing", "A required journal path is missing.", exception);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                throw Failure("journal_path_unavailable", "A required journal path cannot be inspected.", exception);
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw Failure("filesystem_link_refused", "A journal path contains a symbolic link or reparse point.");
        }

        private static void EnsureFile(string path)
        {
            if (!File.Exists(path)) throw Failure("journal_file_missing", "A required journal file is missing.");
            EnsureNoLink(path);
            try { HarborPrivateStorage.ValidatePrivateFile(path); }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException || exception is System.Security.SecurityException)
            {
                throw Failure("journal_file_permissions_invalid", "A journal file does not have private permissions.", exception);
            }
        }

        private FileStream AcquireAdmissionLock()
        {
            string path = Path.Combine(_Root, ".admission.lock");
            bool created = false;
            try
            {
                using (FileStream creation = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    created = true;
                }
            }
            catch (IOException exception) when (File.Exists(path))
            {
                created = false;
            }

            if (created) HarborPrivateStorage.EnsurePrivateFile(path);
            else ValidateAdmissionLockFile(path);
            try { return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception)
            {
                throw Failure("journal_busy", "Another process is creating a local job.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw Failure("journal_lock_unavailable", "The journal admission lock cannot be opened.", exception);
            }
        }

        private void ValidateAdmissionLockFile(string path)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    EnsureFile(path);
                    return;
                }
                catch (HarborJobJournalException exception) when (exception.ReasonCode == "journal_file_permissions_invalid")
                {
                    System.Threading.Thread.Sleep(25);
                }
            }
            EnsureFile(path);
        }
    }
}
