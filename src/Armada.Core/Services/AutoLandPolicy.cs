namespace Armada.Core.Services
{
    using System.Collections.Generic;

    /// <summary>
    /// Per-vessel rules that decide, from the shape of a passing mission's change, whether it may land
    /// unattended or must hold for human review. A zero threshold means "no limit" for that dimension.
    /// </summary>
    public sealed class AutoLandPolicy
    {
        #region Public-Members

        /// <summary>
        /// Whether auto-land evaluation is active for the vessel. When false the predicate imposes no hold.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Maximum number of changed files that may auto-land; 0 means unlimited.
        /// </summary>
        public int MaxFiles { get; set; } = 0;

        /// <summary>
        /// Maximum number of changed lines (added + removed) that may auto-land; 0 means unlimited.
        /// </summary>
        public int MaxLines { get; set; } = 0;

        /// <summary>
        /// Glob patterns a changed path must match to be auto-landable. When non-empty, a change that
        /// touches any path outside the allow-list holds for review.
        /// </summary>
        public List<string> PathAllowGlobs { get; set; } = new List<string>();

        /// <summary>
        /// Glob patterns that force a hold: a change touching any matching path never auto-lands.
        /// </summary>
        public List<string> PathDenyGlobs { get; set; } = new List<string>();

        #endregion
    }
}
