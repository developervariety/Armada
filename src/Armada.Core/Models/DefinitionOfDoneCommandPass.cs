namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One command a passing definition-of-done gate ran in full.
    /// </summary>
    public sealed class DefinitionOfDoneCommandPass
    {
        #region Public-Members

        /// <summary>Mission whose gate ran the command.</summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>Vessel the command ran for.</summary>
        public string VesselId { get; set; } = String.Empty;

        /// <summary>Full HEAD commit of the worktree the command ran in.</summary>
        public string CommitHash { get; set; } = String.Empty;

        /// <summary>Gate label of the command, such as "build" or "unit-test".</summary>
        public string Label { get; set; } = String.Empty;

        /// <summary>The configured command, before the gate adds its restore step.</summary>
        public string Command { get; set; } = String.Empty;

        /// <summary>Stable form of the environment variables the command ran with; empty for none.</summary>
        public string EnvironmentFingerprint { get; set; } = String.Empty;

        /// <summary>When the command passed.</summary>
        public DateTime CompletedUtc { get; set; } = DateTime.UtcNow;

        #endregion
    }
}
