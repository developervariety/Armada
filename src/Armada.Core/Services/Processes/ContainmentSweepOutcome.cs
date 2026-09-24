namespace Armada.Core.Services
{
    /// <summary>
    /// What one <see cref="ContainmentMarker.Sweep"/> did.
    /// </summary>
    public sealed class ContainmentSweepOutcome
    {
        #region Public-Members

        /// <summary>Processes that carried the run identifier and were sent SIGKILL.</summary>
        public int Killed { get; set; } = 0;

        /// <summary>Processes of the same user whose environment the kernel refused to show, so they were not checked.</summary>
        public int Unreadable { get; set; } = 0;

        /// <summary>Why the sweep stopped before it was sure it had found every carrier; null when it finished.</summary>
        public string? Error { get; set; } = null;

        #endregion
    }
}
