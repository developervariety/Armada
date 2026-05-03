namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Environment-specific commands within a workflow profile.
    /// </summary>
    public class WorkflowEnvironmentProfile
    {
        /// <summary>
        /// Environment name such as dev, staging, or prod.
        /// </summary>
        public string EnvironmentName
        {
            get => _EnvironmentName;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(EnvironmentName));
                _EnvironmentName = value.Trim();
            }
        }

        /// <summary>
        /// Deploy command for this environment.
        /// </summary>
        public string? DeployCommand { get; set; } = null;

        /// <summary>
        /// Rollback command for this environment.
        /// </summary>
        public string? RollbackCommand { get; set; } = null;

        /// <summary>
        /// Smoke-test command for this environment.
        /// </summary>
        public string? SmokeTestCommand { get; set; } = null;

        /// <summary>
        /// Health-check command for this environment.
        /// </summary>
        public string? HealthCheckCommand { get; set; } = null;

        private string _EnvironmentName = "dev";
    }
}
