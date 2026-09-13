namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>Current resolved configuration, not a recorded landing result.</summary>
    public sealed class LandingConfiguration
    {
        #region Public-Members

        /// <summary>Explicit mode, or null when legacy flags apply.</summary>
        public LandingModeEnum? LandingMode { get; set; }

        /// <summary>Source selected by the handler's precedence rules.</summary>
        public LandingConfigurationSourceEnum Source { get; set; } = LandingConfigurationSourceEnum.Legacy;

        /// <summary>Effective push flag.</summary>
        public bool AutoPush { get; set; }

        /// <summary>Effective pull-request flag; this path takes precedence over AutoPush.</summary>
        public bool AutoCreatePullRequests { get; set; }

        /// <summary>Effective pull-request merge flag; execution also requires pull-request creation.</summary>
        public bool AutoMergePullRequests { get; set; }

        /// <summary>Effective vessel or global branch cleanup policy.</summary>
        public BranchCleanupPolicyEnum BranchCleanupPolicy { get; set; }

        #endregion
    }
}
