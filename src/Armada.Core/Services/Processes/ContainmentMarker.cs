namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;

    /// <summary>
    /// Containment for a descendant that leaves both the process tree and the process group of a group-owning run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On Linux every process a group-owning run starts carries a run-unique identifier in the
    /// <see cref="VariableName"/> environment variable. The environment is inherited through fork, exec, setsid,
    /// setpgid and a double fork, so a descendant that has left the tree and the group still carries it. After the
    /// group kill and the tree kill, <see cref="Sweep"/> reads <c>/proc/*/environ</c> for processes of the same
    /// real user and kills the ones that carry the identifier.
    /// </para>
    /// <para>
    /// A run inside a run appends its identifier to the inherited value, separated by a colon, so a sweep for the
    /// outer run still reaches the inner run's descendants. A process without the identifier, or owned by another
    /// user, is never signalled. The kernel shows the environment a process was exec'd with, so unsetting the variable
    /// in-process does not hide it; a descendant that execs with an environment lacking the variable, or makes itself
    /// not dumpable so the kernel refuses the read, is not found, and the second case is counted in the outcome.
    /// </para>
    /// <para>
    /// Other platforms carry no identifier: macOS does not expose another process's environment to an unprivileged
    /// caller, and Windows has no <c>/proc</c>.
    /// </para>
    /// </remarks>
    public static class ContainmentMarker
    {
        #region Public-Members

        /// <summary>Environment variable that carries the identifiers of the group-owning runs a process belongs to.</summary>
        public const string VariableName = "ARMADA_CONTAINMENT_ID";

        /// <summary>Whether this host tags and sweeps group-owning runs: Linux with a readable <c>/proc</c>.</summary>
        public static bool Supported => _Supported.Value;

        #endregion

        #region Private-Members

        private const int SignalKill = 9;
        private const int MaxPasses = 4;
        private const int EnvironmentReadLimitBytes = 4 * 1024 * 1024;
        private const string ProcRoot = "/proc";
        private static readonly TimeSpan _SweepBudget = TimeSpan.FromSeconds(2);
        private static readonly byte[] _Prefix = Encoding.ASCII.GetBytes(VariableName + "=");
        private static readonly Lazy<bool> _Supported = new Lazy<bool>(() => OperatingSystem.IsLinux() && File.Exists(Path.Combine(ProcRoot, "self", "environ")));

        private enum CarrierState
        {
            NotCarrier,
            Carrier,
            Unreadable
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Tag a start environment with a new run identifier, appended to any identifier the environment already
        /// carries. Returns the new identifier, or null when this host does not sweep.
        /// </summary>
        /// <param name="startInfo">Start settings of the group-owning process.</param>
        /// <returns>The run identifier, or null.</returns>
        public static string? Apply(ProcessStartInfo startInfo)
        {
            if (startInfo == null) throw new ArgumentNullException(nameof(startInfo));
            if (!Supported) return null;

            string id = Guid.NewGuid().ToString("N");
            startInfo.Environment.TryGetValue(VariableName, out string? inherited);
            startInfo.Environment[VariableName] = String.IsNullOrEmpty(inherited) ? id : inherited + ":" + id;
            return id;
        }

        /// <summary>
        /// Kill every process of this real user that carries the run identifier. Passes repeat until one finds no
        /// new carrier, within a fixed pass count and time budget, so a carrier that forks during the sweep is still
        /// reached. Never throws.
        /// </summary>
        /// <param name="id">Run identifier returned by <see cref="Apply"/>.</param>
        /// <returns>What the sweep killed and what it could not check.</returns>
        public static ContainmentSweepOutcome Sweep(string id)
        {
            ContainmentSweepOutcome outcome = new ContainmentSweepOutcome();
            if (String.IsNullOrEmpty(id) || !Supported) return outcome;

            uint uid;
            try
            {
                uid = getuid();
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                outcome.Error = "no libc getuid on this host";
                return outcome;
            }

            byte[] idBytes = Encoding.ASCII.GetBytes(id);
            int self = Environment.ProcessId;
            HashSet<int> signalled = new HashSet<int>();
            HashSet<int> unreadable = new HashSet<int>();
            Stopwatch clock = Stopwatch.StartNew();

            for (int pass = 0; pass < MaxPasses; pass++)
            {
                int found = 0;
                try
                {
                    foreach (string entry in Directory.EnumerateDirectories(ProcRoot))
                    {
                        if (clock.Elapsed > _SweepBudget)
                        {
                            outcome.Error = "sweep stopped at its " + _SweepBudget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s budget";
                            break;
                        }

                        if (!Int32.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) continue;
                        if (pid == self) continue;
                        if (!OwnedBy(entry, uid)) continue;

                        CarrierState state = ReadCarrierState(entry, idBytes);
                        if (state == CarrierState.Unreadable)
                        {
                            unreadable.Add(pid);
                            continue;
                        }

                        if (state != CarrierState.Carrier) continue;

                        // SIGKILL cannot be caught. ESRCH means the process ended after the read, which is the state
                        // the kill wants; a process already signalled is signalled again but not counted again. The
                        // read and the kill are not atomic: a pid reused in between would need the kernel to cycle
                        // through every pid first.
                        if (kill(pid, SignalKill) == 0 && signalled.Add(pid)) found++;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    outcome.Error = "cannot list " + ProcRoot + ": " + ex.Message;
                }

                if (outcome.Error != null || found == 0) break;
                if (pass == MaxPasses - 1) outcome.Error = "new carriers still appeared after " + MaxPasses + " passes";
            }

            outcome.Killed = signalled.Count;
            outcome.Unreadable = unreadable.Count;
            return outcome;
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Whether the process's real user is the given user. A process that ended, or whose status cannot be read,
        /// is not owned for this purpose.
        /// </summary>
        private static bool OwnedBy(string processDirectory, uint uid)
        {
            try
            {
                foreach (string line in File.ReadLines(Path.Combine(processDirectory, "status")))
                {
                    if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
                    string[] fields = line.Substring(4).Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    return fields.Length > 0
                        && UInt32.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint real)
                        && real == uid;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // The process ended between the listing and the read.
            }

            return false;
        }

        /// <summary>
        /// Read the process's environment and look for the identifier among the values of the marker variable. A
        /// zombie reads as empty, so it is not a carrier; the kernel refuses the read for a process that is not
        /// dumpable, which is reported as unreadable.
        /// </summary>
        private static CarrierState ReadCarrierState(string processDirectory, byte[] id)
        {
            byte[] environment;
            try
            {
                using (FileStream stream = new FileStream(Path.Combine(processDirectory, "environ"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None))
                using (MemoryStream buffer = new MemoryStream())
                {
                    byte[] chunk = new byte[16 * 1024];
                    int read;
                    while (buffer.Length < EnvironmentReadLimitBytes && (read = stream.Read(chunk, 0, chunk.Length)) > 0)
                        buffer.Write(chunk, 0, read);
                    environment = buffer.ToArray();
                }
            }
            catch (UnauthorizedAccessException)
            {
                return CarrierState.Unreadable;
            }
            catch (IOException)
            {
                // The process ended between the listing and the read.
                return CarrierState.NotCarrier;
            }

            return Carries(environment, id) ? CarrierState.Carrier : CarrierState.NotCarrier;
        }

        private static bool Carries(ReadOnlySpan<byte> environment, ReadOnlySpan<byte> id)
        {
            while (!environment.IsEmpty)
            {
                int end = environment.IndexOf((byte)0);
                ReadOnlySpan<byte> entry = end < 0 ? environment : environment.Slice(0, end);
                environment = end < 0 ? ReadOnlySpan<byte>.Empty : environment.Slice(end + 1);
                if (!entry.StartsWith(_Prefix)) continue;

                ReadOnlySpan<byte> value = entry.Slice(_Prefix.Length);
                while (true)
                {
                    int colon = value.IndexOf((byte)':');
                    ReadOnlySpan<byte> token = colon < 0 ? value : value.Slice(0, colon);
                    if (token.SequenceEqual(id)) return true;
                    if (colon < 0) break;
                    value = value.Slice(colon + 1);
                }
            }

            return false;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);

        [DllImport("libc")]
        private static extern uint getuid();

        #endregion
    }
}
