namespace Armada.Core.Settings
{
    /// <summary>
    /// Controls whether a dispatched voyage is armed with its own Build and UnitTest Checks.
    /// </summary>
    /// <remarks>
    /// A Judge PASS is rejected when the voyage carries no green independent Check, so a voyage
    /// dispatched without Checks is already condemned at the moment it starts - and nothing says
    /// so until the Judge stage, after the whole pipeline has run. Leaving the arming to the
    /// operator makes that outcome depend on remembering a second call, and cancelling a voyage
    /// discards its Checks, so a re-dispatch silently starts bare again.
    /// </remarks>
    public class VoyageCheckArmingSettings
    {
        #region Public-Members

        /// <summary>
        /// Whether dispatch arms Checks on the new voyage. Defaults to true.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Whether a Build check is armed when the vessel's profile defines a build command.
        /// Defaults to true.
        /// </summary>
        public bool ArmBuild { get; set; } = true;

        /// <summary>
        /// Whether a UnitTest check is armed when the vessel's profile defines a unit-test
        /// command. Defaults to true.
        /// </summary>
        public bool ArmUnitTest { get; set; } = true;

        #endregion
    }
}
