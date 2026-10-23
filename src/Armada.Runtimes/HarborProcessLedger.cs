namespace Armada.Runtimes
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using SyslogLogging;

    /// <summary>
    /// On-disk record of the agent processes a Harbor started, so a Harbor that crashed or was killed can stop the ones
    /// that outlived it before it takes work again. Found live: a CLI agent (mux) kept running after its Harbor was killed,
    /// and the Admiral relaunched the mission into the same dock, so two agents edited one worktree. A process is stopped
    /// only when its id and start time both match the record, so a reused process id is never touched. In-process agent
    /// loops (API captains) have no operating-system process and end with the Harbor, so they are never recorded.
    /// </summary>
    public sealed class HarborProcessLedger
    {
        #region Private-Members

        private readonly string _Path;
        private readonly LoggingModule _Logging;
        private readonly object _Lock = new object();
        private readonly Dictionary<string, HarborProcessLedgerEntry> _Entries = new Dictionary<string, HarborProcessLedgerEntry>(StringComparer.Ordinal);
        private const string _Header = "[HarborProcessLedger] ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="path">The ledger file.</param>
        /// <param name="logging">Logging module.</param>
        /// <exception cref="ArgumentNullException">Thrown when path or logging is null.</exception>
        public HarborProcessLedger(string path, LoggingModule logging)
        {
            _Path = String.IsNullOrWhiteSpace(path) ? throw new ArgumentNullException(nameof(path)) : path;
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Stop every recorded process that is still running from an earlier Harbor run, then start an empty ledger. Call
        /// once at startup, before taking work.
        /// </summary>
        /// <returns>The number of processes stopped.</returns>
        public int StopOrphans()
        {
            List<HarborProcessLedgerEntry> previous = Load();
            int stopped = 0;
            foreach (HarborProcessLedgerEntry entry in previous)
            {
                Process? process = TryFind(entry);
                if (process == null) continue;
                try
                {
                    _Logging.Warn(_Header + "stopping agent process " + entry.ProcessId + " left running by an earlier Harbor run (job " + entry.JobId + ")");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    stopped++;
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
                {
                    _Logging.Warn(_Header + "could not stop process " + entry.ProcessId + ": " + ex.Message);
                }
                finally
                {
                    process.Dispose();
                }
            }

            lock (_Lock)
            {
                _Entries.Clear();
                Save();
            }
            return stopped;
        }

        /// <summary>
        /// Record a job's operating-system process. Ids that are not running processes (in-process loops) are ignored.
        /// </summary>
        /// <param name="jobId">The job.</param>
        /// <param name="processId">Its process id.</param>
        public void Record(string jobId, int processId)
        {
            if (String.IsNullOrEmpty(jobId) || processId <= 0) return;
            DateTime? started = StartTimeOf(processId);
            if (!started.HasValue) return;
            lock (_Lock)
            {
                _Entries[jobId] = new HarborProcessLedgerEntry { JobId = jobId, ProcessId = processId, StartedUtc = started.Value };
                Save();
            }
        }

        /// <summary>
        /// Forget a job whose process exited.
        /// </summary>
        /// <param name="jobId">The job.</param>
        public void Remove(string jobId)
        {
            if (String.IsNullOrEmpty(jobId)) return;
            lock (_Lock)
            {
                if (_Entries.Remove(jobId)) Save();
            }
        }

        #endregion

        #region Private-Methods

        private List<HarborProcessLedgerEntry> Load()
        {
            try
            {
                if (!File.Exists(_Path)) return new List<HarborProcessLedgerEntry>();
                return JsonSerializer.Deserialize<List<HarborProcessLedgerEntry>>(File.ReadAllText(_Path)) ?? new List<HarborProcessLedgerEntry>();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                _Logging.Warn(_Header + "could not read " + _Path + ": " + ex.Message);
                return new List<HarborProcessLedgerEntry>();
            }
        }

        private void Save()
        {
            try
            {
                string? directory = System.IO.Path.GetDirectoryName(_Path);
                if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string temp = _Path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_Entries.Values.ToList()));
                File.Move(temp, _Path, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _Logging.Warn(_Header + "could not write " + _Path + ": " + ex.Message);
            }
        }

        private static Process? TryFind(HarborProcessLedgerEntry entry)
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById(entry.ProcessId);
                if (process.HasExited) { process.Dispose(); return null; }
                // The same id with a different start time is an unrelated process that reused the id.
                if (Math.Abs((process.StartTime.ToUniversalTime() - entry.StartedUtc).TotalSeconds) > 2) { process.Dispose(); return null; }
                return process;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
            {
                process?.Dispose();
                return null;
            }
        }

        private static DateTime? StartTimeOf(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return process.StartTime.ToUniversalTime();
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
            {
                return null;
            }
        }

        #endregion
    }
}
