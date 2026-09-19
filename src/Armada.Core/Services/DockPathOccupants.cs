namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;

    /// <summary>
    /// Processes whose working directory sits inside a dock. An orphaned child a captain launched
    /// with <c>&amp;</c> keeps git from removing that worktree after the captain has gone Idle; listing
    /// and releasing those processes is how reclaim takes the path back.
    /// </summary>
    public static class DockPathOccupants
    {
        /// <summary>Linux PIDs whose cwd is <paramref name="path"/> or a directory under it.</summary>
        public static List<int> ListPids(string path)
        {
            List<int> pids = new List<int>();
            if (String.IsNullOrWhiteSpace(path) || !OperatingSystem.IsLinux()) return pids;
            if (!Directory.Exists("/proc")) return pids;

            string root;
            try { root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (Exception) { return pids; }
            if (root.Length < 8) return pids;

            int self = Environment.ProcessId;
            foreach (string entry in Directory.EnumerateDirectories("/proc"))
            {
                string name = Path.GetFileName(entry);
                if (!Int32.TryParse(name, out int pid) || pid <= 1 || pid == self) continue;
                string? cwd = ReadCwd(pid);
                if (String.IsNullOrEmpty(cwd)) continue;
                if (cwd.Equals(root, StringComparison.Ordinal) || cwd.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    pids.Add(pid);
            }
            return pids;
        }

        /// <summary>
        /// SIGKILL every occupant of <paramref name="path"/>, including each process tree.
        /// Returns the PIDs that were signalled.
        /// </summary>
        public static List<int> Release(string path)
        {
            List<int> pids = ListPids(path);
            foreach (int pid in pids)
            {
                try
                {
                    Process process = Process.GetProcessById(pid);
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }
            return pids;
        }

        /// <summary>
        /// The worktree path git named in an "already used by worktree" refusal, or null.
        /// </summary>
        public static string? ParseHolderPath(string? message)
        {
            if (String.IsNullOrEmpty(message)) return null;
            int at = IndexAfter(message, "already used by worktree at");
            if (at < 0) at = IndexAfter(message, "already checked out at");
            if (at < 0) return null;

            string rest = message.Substring(at).Trim();
            if (rest.StartsWith("'", StringComparison.Ordinal) || rest.StartsWith("`", StringComparison.Ordinal))
            {
                char q = rest[0];
                int end = rest.IndexOf(q, 1);
                if (end > 1) return rest.Substring(1, end - 1).Trim();
            }
            int line = rest.IndexOf('\n');
            if (line >= 0) rest = rest.Substring(0, line);
            return rest.Trim().TrimEnd('.');
        }

        private static int IndexAfter(string message, string marker)
        {
            int at = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? -1 : at + marker.Length;
        }

        private static string? ReadCwd(int pid)
        {
            try
            {
                string link = "/proc/" + pid.ToString() + "/cwd";
                FileSystemInfo? target = File.ResolveLinkTarget(link, true);
                if (target == null) return null;
                return Path.GetFullPath(target.FullName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
