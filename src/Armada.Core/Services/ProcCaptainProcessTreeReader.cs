namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Reads a captain's descendant processes from a Linux <c>/proc</c> table. Each <c>/proc/&lt;pid&gt;/stat</c>
    /// names the parent and the start time, so the tree is the transitive set of processes whose parent chain
    /// reaches the agent process. A host without a readable process table answers with a named reason, never
    /// with an empty tree.
    /// </summary>
    public sealed class ProcCaptainProcessTreeReader : ICaptainProcessTreeReader
    {
        #region Public-Members

        /// <summary>
        /// Clock ticks per second that <c>/proc</c> reports start times in. Linux exposes these in USER_HZ, which
        /// is 100 on every supported architecture regardless of the kernel's internal tick rate.
        /// </summary>
        public const int UserHz = 100;

        #endregion

        #region Private-Members

        private readonly string _ProcRoot;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="procRoot">Process table root; the host's <c>/proc</c> by default.</param>
        public ProcCaptainProcessTreeReader(string procRoot = "/proc")
        {
            _ProcRoot = String.IsNullOrWhiteSpace(procRoot) ? "/proc" : procRoot;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public CaptainProcessTreeRead ReadDescendants(int rootProcessId)
        {
            string statPath = Path.Combine(_ProcRoot, "stat");
            if (!Directory.Exists(_ProcRoot) || !File.Exists(statPath))
                return CaptainProcessTreeRead.Unavailable(CaptainProcessTreeRead.ProcessTableUnavailableReason,
                    "no process table at " + _ProcRoot);

            try
            {
                DateTime? bootUtc = ReadBootTimeUtc(statPath);
                if (!bootUtc.HasValue)
                    return CaptainProcessTreeRead.Unavailable(CaptainProcessTreeRead.ReadFailedReason,
                        "the boot time is missing from " + statPath);

                Dictionary<int, ProcEntry> entries = new Dictionary<int, ProcEntry>();
                foreach (string directory in Directory.EnumerateDirectories(_ProcRoot))
                {
                    if (!Int32.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                        continue;
                    ProcEntry? entry = TryReadEntry(Path.Combine(directory, "stat"), pid, bootUtc.Value);
                    if (entry != null) entries[pid] = entry;
                }

                if (!entries.ContainsKey(rootProcessId))
                    return CaptainProcessTreeRead.Unavailable(CaptainProcessTreeRead.RootNotFoundReason,
                        "process " + rootProcessId + " is not in the process table");

                Dictionary<int, List<ProcEntry>> children = new Dictionary<int, List<ProcEntry>>();
                foreach (ProcEntry entry in entries.Values)
                {
                    if (!children.TryGetValue(entry.ParentProcessId, out List<ProcEntry>? list))
                    {
                        list = new List<ProcEntry>();
                        children[entry.ParentProcessId] = list;
                    }
                    list.Add(entry);
                }

                List<CaptainChildProcess> descendants = new List<CaptainChildProcess>();
                HashSet<int> visited = new HashSet<int> { rootProcessId };
                Queue<int> pending = new Queue<int>();
                pending.Enqueue(rootProcessId);
                while (pending.Count > 0)
                {
                    int parent = pending.Dequeue();
                    if (!children.TryGetValue(parent, out List<ProcEntry>? direct)) continue;
                    foreach (ProcEntry child in direct)
                    {
                        if (!visited.Add(child.ProcessId)) continue;
                        pending.Enqueue(child.ProcessId);
                        // A zombie has finished; it is waiting to be reaped and does no work.
                        if (child.State == 'Z' || child.State == 'X') continue;
                        descendants.Add(new CaptainChildProcess(child.ProcessId, child.ParentProcessId, child.Name, child.StartedUtc));
                    }
                }

                return CaptainProcessTreeRead.Read(descendants, entries[rootProcessId].StartedUtc);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return CaptainProcessTreeRead.Unavailable(CaptainProcessTreeRead.ReadFailedReason, ex.Message);
            }
        }

        #endregion

        #region Private-Methods

        private static DateTime? ReadBootTimeUtc(string statPath)
        {
            foreach (string line in File.ReadLines(statPath))
            {
                if (!line.StartsWith("btime ", StringComparison.Ordinal)) continue;
                if (Int64.TryParse(line.Substring(6).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds))
                    return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            }
            return null;
        }

        // A process can exit between the directory listing and the read, so an unreadable entry is skipped.
        private static ProcEntry? TryReadEntry(string path, int pid, DateTime bootUtc)
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }

            // The command name sits in parentheses and may itself hold spaces or parentheses, so the
            // fields after it start at the LAST closing parenthesis.
            int open = text.IndexOf('(');
            int close = text.LastIndexOf(')');
            if (open < 0 || close < open || close + 2 > text.Length) return null;

            string name = text.Substring(open + 1, close - open - 1);
            string[] fields = text.Substring(close + 2).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // After the name: [0] state, [1] parent pid, ... [19] start time in clock ticks since boot.
            if (fields.Length < 20 || fields[0].Length == 0) return null;
            if (!Int32.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parent)) return null;
            if (!Int64.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out long startTicks)) return null;

            return new ProcEntry
            {
                ProcessId = pid,
                ParentProcessId = parent,
                Name = name,
                State = fields[0][0],
                StartedUtc = bootUtc.AddSeconds((double)startTicks / UserHz)
            };
        }

        private sealed class ProcEntry
        {
            public int ProcessId { get; set; }

            public int ParentProcessId { get; set; }

            public string Name { get; set; } = String.Empty;

            public char State { get; set; }

            public DateTime StartedUtc { get; set; }
        }

        #endregion
    }
}
