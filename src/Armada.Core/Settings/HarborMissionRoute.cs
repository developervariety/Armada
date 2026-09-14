namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// Opts one captain or one vessel into running its missions on one named Harbor runner. Nothing routes to a
    /// runner without a matching route, and a route has no effect while Harbor is disabled.
    /// </summary>
    public class HarborMissionRoute
    {
        #region Public-Members

        /// <summary>Runner that runs the matching missions. A route without a runner matches nothing.</summary>
        public string RunnerId { get; set; } = String.Empty;

        /// <summary>Captain whose missions run on the runner. A captain route takes precedence over a vessel route.</summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>Vessel whose missions run on the runner, for captains without a captain route.</summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Admiral-side directory under which dock worktrees live. When set with <see cref="RunnerWorkingDirectoryRoot"/>,
        /// a dock path under it is sent to the runner under the runner root; a dock outside it is refused.
        /// When both are empty the runner receives the Admiral's dock path unchanged, for a shared mount.
        /// </summary>
        public string? AdmiralWorkingDirectoryRoot { get; set; } = null;

        /// <summary>Runner-side directory that mirrors <see cref="AdmiralWorkingDirectoryRoot"/>.</summary>
        public string? RunnerWorkingDirectoryRoot { get; set; } = null;

        #endregion
    }
}
