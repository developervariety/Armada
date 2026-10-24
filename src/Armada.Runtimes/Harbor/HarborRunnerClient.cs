namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core.Harbor;

    /// <summary>Maintains an authenticated Harbor link and supervises jobs on the local host.</summary>
    public sealed partial class HarborRunnerClient
    {
        private const int MaximumFrameBytes = 8 * 1024 * 1024;
        private readonly HarborRunnerSettings _Settings;
        private readonly string _SupervisorExecutable;
        private readonly IReadOnlyList<string> _SupervisorArguments;
        private readonly Action<string> _Log;
        private readonly HarborJobJournal _Journal;

        /// <summary>Create a headless Harbor runner client.</summary>
        /// <param name="settings">Validated runner settings.</param>
        /// <param name="supervisorExecutable">Executable used to start this application in supervisor mode.</param>
        /// <param name="supervisorArguments">Arguments that start this application before the supervisor switch.</param>
        /// <param name="log">Optional safe, reason-only log callback.</param>
        public HarborRunnerClient(
            HarborRunnerSettings settings,
            string supervisorExecutable,
            IReadOnlyList<string> supervisorArguments,
            Action<string>? log = null)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Settings.Validate();
            if (String.IsNullOrWhiteSpace(supervisorExecutable)) throw new ArgumentException("A supervisor executable is required.", nameof(supervisorExecutable));
            _SupervisorExecutable = supervisorExecutable;
            _SupervisorArguments = supervisorArguments ?? throw new ArgumentNullException(nameof(supervisorArguments));
            _Log = log ?? (_ => { });
            _Journal = new HarborJobJournal(_Settings.Supervisor);
        }

        private FileStream AcquireRunnerLock()
        {
            string lockPath = Path.Combine(Path.GetFullPath(_Settings.Supervisor.StateDirectory), "runner.lock");
            bool existing = File.Exists(lockPath);
            FileStream probe;
            try
            {
                probe = new FileStream(lockPath, existing ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception)
            {
                throw new HarborJobJournalException("runner_already_running", "Another runner process owns this state directory.", exception);
            }
            probe.Dispose();
            if (existing)
            {
                try { HarborPrivateStorage.ValidatePrivateFile(lockPath); }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException || exception is System.Security.SecurityException)
                {
                    throw new HarborJobJournalException("runner_lock_invalid", "The runner lock file is not private or contains a link.", exception);
                }
            }
            else
            {
                try { HarborPrivateStorage.EnsurePrivateFile(lockPath); }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException || exception is System.Security.SecurityException)
                {
                    throw new HarborJobJournalException("runner_lock_invalid", "The runner lock file is not private or contains a link.", exception);
                }
            }
            FileStream stream;
            try
            {
                stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception)
            {
                throw new HarborJobJournalException("runner_already_running", "Another runner process owns this state directory.", exception);
            }
            return stream;
        }

        private static string SafeJobId(string jobId)
        {
            if (jobId.Length != 37 || !jobId.StartsWith("hjob_", StringComparison.Ordinal)) return "unknown";
            for (int index = 5; index < jobId.Length; index++)
                if (!(jobId[index] >= '0' && jobId[index] <= '9') && !(jobId[index] >= 'a' && jobId[index] <= 'f')) return "unknown";
            return jobId;
        }
    }
}
