namespace Armada.Core.Harbor
{
    using System;

    /// <summary>A mission launch bound for a Harbor runner.</summary>
    public sealed class HarborProcessLaunch
    {
        #region Public-Members

        /// <summary>Runner that must run the job.</summary>
        public string RunnerId { get; set; } = String.Empty;

        /// <summary>Key of the work; one live job per key.</summary>
        public string LaunchKey { get; set; } = String.Empty;

        /// <summary>Mission the job runs.</summary>
        public string? MissionId { get; set; } = null;

        /// <summary>Captain the job runs for.</summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>Mission owner tenant. It must be the runner's enrolled tenant.</summary>
        public string OwnerTenantId { get; set; } = String.Empty;

        /// <summary>Mission owner user. It must be the runner's enrolled user.</summary>
        public string OwnerUserId { get; set; } = String.Empty;

        /// <summary>Working directory on the runner.</summary>
        public string WorkingDirectory { get; set; } = String.Empty;

        /// <summary>Launch plan the runtime built. Set by the runtime before the host is called.</summary>
        public HarborLaunchRequest? Request { get; set; } = null;

        #endregion
    }
}
