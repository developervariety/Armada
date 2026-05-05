namespace Armada.Core.Models
{
    /// <summary>
    /// Summary of deployment-oriented workflow coverage for a vessel.
    /// </summary>
    public class VesselDeploymentMetadata
    {
        /// <summary>
        /// Number of named deployment environments.
        /// </summary>
        public int EnvironmentCount { get; set; } = 0;

        /// <summary>
        /// Whether any deploy command is configured.
        /// </summary>
        public bool HasDeployCommand { get; set; } = false;

        /// <summary>
        /// Whether any rollback command is configured.
        /// </summary>
        public bool HasRollbackCommand { get; set; } = false;

        /// <summary>
        /// Whether any smoke-test command is configured.
        /// </summary>
        public bool HasSmokeTestCommand { get; set; } = false;

        /// <summary>
        /// Whether any health-check command is configured.
        /// </summary>
        public bool HasHealthCheckCommand { get; set; } = false;

        /// <summary>
        /// Whether any deployment-verification command is configured.
        /// </summary>
        public bool HasDeploymentVerificationCommand { get; set; } = false;

        /// <summary>
        /// Whether any rollback-verification command is configured.
        /// </summary>
        public bool HasRollbackVerificationCommand { get; set; } = false;
    }
}
