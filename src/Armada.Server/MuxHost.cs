namespace Armada.Server
{
    using Armada.Core.Services;

    /// <summary>
    /// Where a mux command runs: on a linked Harbor (the mux installed on the developer's machine) or, in standalone
    /// mode, on the Admiral host. Produced by <see cref="MuxHostResolver"/>.
    /// </summary>
    public class MuxHost
    {
        #region Public-Members

        /// <summary>
        /// Executor that runs the command, or null when no host is available (see <see cref="Error"/>).
        /// </summary>
        public IHostCommandExecutor? Executor { get; set; } = null;

        /// <summary>
        /// Working directory on the chosen host, or null for the host's default.
        /// </summary>
        public string? WorkingDirectory { get; set; } = null;

        /// <summary>
        /// Harbor that runs the command, or null for the Admiral host.
        /// </summary>
        public string? HarborId { get; set; } = null;

        /// <summary>
        /// True when the command runs on a Harbor.
        /// </summary>
        public bool IsRemote
        {
            get { return HarborId != null; }
        }

        /// <summary>
        /// Why no host is available, or null when <see cref="Executor"/> is set.
        /// </summary>
        public string? Error { get; set; } = null;

        #endregion
    }
}
