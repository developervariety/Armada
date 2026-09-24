namespace Armada.Core.Services
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>
    /// One short-lived child process for <see cref="BoundedProcessRunner"/>: what to start, how long it may run,
    /// and how much of its output to keep.
    /// </summary>
    public sealed class BoundedProcessRequest
    {
        #region Public-Members

        /// <summary>Default per-stream output budget: 4 MiB.</summary>
        public const int DefaultOutputLimitBytes = 4 * 1024 * 1024;

        /// <summary>Default wait for the output pipes to close after the process exits.</summary>
        public static readonly TimeSpan DefaultOutputDrainTimeout = TimeSpan.FromSeconds(5);

        /// <summary>Default wait for the output pipes, and for the process, after a kill.</summary>
        public static readonly TimeSpan DefaultKillDrainTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// What to start. The runner sets the redirect, shell-execute and window flags itself; the caller sets the
        /// file name, arguments, working directory, environment and encodings.
        /// </summary>
        public ProcessStartInfo StartInfo
        {
            get { return _StartInfo; }
            set { _StartInfo = value ?? throw new ArgumentNullException(nameof(StartInfo)); }
        }

        /// <summary>
        /// Longest the process may run. When it passes, the process and everything it started are killed and the
        /// result reports <see cref="BoundedProcessResult.TimedOut"/>.
        /// </summary>
        public TimeSpan Timeout
        {
            get { return _Timeout; }
            set
            {
                if (value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(Timeout), "Must be positive.");
                _Timeout = value;
            }
        }

        /// <summary>
        /// Most UTF-8 bytes kept from each output stream. Output past it is still read, so the child never blocks
        /// on a full pipe, and is counted in the result's omitted bytes.
        /// </summary>
        public int OutputLimitBytes
        {
            get { return _OutputLimitBytes; }
            set { _OutputLimitBytes = Math.Max(1024, value); }
        }

        /// <summary>Which part of an over-budget stream is kept.</summary>
        public BoundedOutputShapeEnum OutputShape { get; set; } = BoundedOutputShapeEnum.HeadAndTail;

        /// <summary>
        /// Text placed where output was dropped. Null uses the default marker, which names the byte count.
        /// </summary>
        public string? TruncationMarker { get; set; } = null;

        /// <summary>
        /// Collect standard output and standard error into one line-ordered stream, returned as
        /// <see cref="BoundedProcessResult.StandardOutput"/>. Lines are normalized to "\n" and a single line longer
        /// than the budget is cut while it is read. Off by default: each stream is kept separately and exactly.
        /// </summary>
        public bool CombineOutput { get; set; } = false;

        /// <summary>Text written to standard input before it is closed. Standard input is always closed.</summary>
        public string? StandardInput { get; set; } = null;

        /// <summary>
        /// Writes standard input from a caller-owned source, for input too large for <see cref="StandardInput"/>.
        /// Standard input is closed when it returns.
        /// </summary>
        public Func<Stream, CancellationToken, Task>? StandardInputWriter { get; set; } = null;

        /// <summary>
        /// Start the process as the leader of its own session and process group, so a timeout or cancellation also
        /// kills a background child it left behind. Applies on Unix when setsid or perl exists and the executable
        /// resolves, in the order Process.Start uses, to a file this process may execute. An executable that does not
        /// resolve starts without the group, so a missing or non-executable file is the same start failure it is
        /// without this option. Requires <see cref="ProcessStartInfo.ArgumentList"/> rather than an argument string.
        /// On Linux the run also sets <see cref="ContainmentMarker.VariableName"/> in the child's environment, and a
        /// timeout or cancellation kills every process of the same user that still carries it, so a descendant that
        /// started its own session and left the tree dies too.
        /// </summary>
        public bool OwnProcessGroup { get; set; } = false;

        /// <summary>
        /// Count the output pipes closing as part of the process: after the process exits, keep reading within the
        /// remaining <see cref="Timeout"/>, and time out if a child still holds a pipe. Off by default: after exit,
        /// the pipes get <see cref="OutputDrainTimeout"/> and are then abandoned.
        /// </summary>
        public bool WaitForOutputClose { get; set; } = false;

        /// <summary>
        /// Wait for the output pipes to close after the process exits. A background child holding a pipe past it
        /// is left running, the readers stop, and <see cref="BoundedProcessResult.OutputDrainTimedOut"/> is set.
        /// </summary>
        public TimeSpan OutputDrainTimeout
        {
            get { return _OutputDrainTimeout; }
            set
            {
                if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(OutputDrainTimeout), "Must be positive.");
                _OutputDrainTimeout = value;
            }
        }

        /// <summary>Wait for the process to exit and its pipes to close after a kill.</summary>
        public TimeSpan KillDrainTimeout
        {
            get { return _KillDrainTimeout; }
            set
            {
                if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(KillDrainTimeout), "Must be positive.");
                _KillDrainTimeout = value;
            }
        }

        #endregion

        #region Private-Members

        private ProcessStartInfo _StartInfo;
        private TimeSpan _Timeout;
        private int _OutputLimitBytes = DefaultOutputLimitBytes;
        private TimeSpan _OutputDrainTimeout = DefaultOutputDrainTimeout;
        private TimeSpan _KillDrainTimeout = DefaultKillDrainTimeout;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a request.
        /// </summary>
        /// <param name="startInfo">What to start.</param>
        /// <param name="timeout">Longest the process may run.</param>
        public BoundedProcessRequest(ProcessStartInfo startInfo, TimeSpan timeout)
        {
            _StartInfo = startInfo ?? throw new ArgumentNullException(nameof(startInfo));
            Timeout = timeout;
        }

        #endregion
    }
}
