namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;

    /// <summary>
    /// Lists the command lines of the processes in one process group, so a run stopped at its time limit can say what
    /// it was still doing: a test host still running, or a lock the command took itself (for example
    /// <c>flock</c>) that it was still waiting on.
    /// </summary>
    /// <remarks>
    /// Linux only: it reads the group of each process from <c>/proc/&lt;pid&gt;/stat</c> and its command line from
    /// <c>/proc/&lt;pid&gt;/cmdline</c>. Other platforms, and any read failure, give an empty list. A process that exits
    /// during the read is skipped.
    /// </remarks>
    public static class ProcessGroupSnapshot
    {
        #region Public-Members

        /// <summary>Most command lines one snapshot keeps.</summary>
        public const int MaxProcesses = 20;

        /// <summary>Most characters kept of one command line.</summary>
        public const int MaxCommandLineChars = 200;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read the command lines of the processes whose process group is <paramref name="processGroup"/>, in process
        /// identifier order, at most <see cref="MaxProcesses"/> of them.
        /// </summary>
        /// <param name="processGroup">Process group identifier.</param>
        /// <returns>Command lines; empty when none are found or the platform has no <c>/proc</c>.</returns>
        public static IReadOnlyList<string> Read(int processGroup)
        {
            List<string> lines = new List<string>();
            if (processGroup <= 0 || !OperatingSystem.IsLinux()) return lines;

            List<int> members = new List<int>();
            try
            {
                foreach (string directory in Directory.EnumerateDirectories("/proc"))
                {
                    if (!Int32.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) continue;
                    int? group = ReadProcessGroup(pid);
                    if (group == processGroup) members.Add(pid);
                }
            }
            catch (Exception)
            {
                // An unreadable /proc gives what was read so far: the snapshot only describes, it never decides.
            }

            members.Sort();
            foreach (int pid in members)
            {
                if (lines.Count >= MaxProcesses) break;
                string? commandLine = ReadCommandLine(pid);
                if (!String.IsNullOrEmpty(commandLine)) lines.Add(commandLine);
            }

            return lines;
        }

        /// <summary>
        /// Read the process group field from the text of <c>/proc/&lt;pid&gt;/stat</c>. The command name in parentheses
        /// can hold spaces and parentheses, so the fields are read after its last closing parenthesis.
        /// </summary>
        /// <param name="statText">The file's text, for example <c>42 (sleep) S 41 42 42 0 ...</c>.</param>
        /// <returns>The process group, or null when the text cannot be parsed.</returns>
        public static int? ParseProcessGroup(string? statText)
        {
            if (String.IsNullOrEmpty(statText)) return null;
            int close = statText.LastIndexOf(')');
            if (close < 0 || close + 1 >= statText.Length) return null;
            string[] fields = statText.Substring(close + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // After the name: state, parent, process group.
            if (fields.Length < 3) return null;
            if (!Int32.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int group)) return null;
            return group;
        }

        #endregion

        #region Private-Methods

        private static int? ReadProcessGroup(int pid)
        {
            try
            {
                return ParseProcessGroup(File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat"));
            }
            catch (Exception)
            {
                // The process exited or hides its stat; it is not counted.
                return null;
            }
        }

        private static string? ReadCommandLine(int pid)
        {
            try
            {
                string raw = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/cmdline");
                string line = raw.Replace('\0', ' ').Trim();
                if (line.Length > MaxCommandLineChars) line = line.Substring(0, MaxCommandLineChars) + "...";
                return line;
            }
            catch (Exception)
            {
                // The process exited during the read; it is skipped.
                return null;
            }
        }

        #endregion
    }
}
