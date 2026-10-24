namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

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

        /// <summary>Dock that owns the mission attempt.</summary>
        public string? DockId { get; set; } = null;

        /// <summary>Mission owner tenant. It must be the runner's enrolled tenant.</summary>
        public string OwnerTenantId { get; set; } = String.Empty;

        /// <summary>Mission owner user. It must be the runner's enrolled user.</summary>
        public string OwnerUserId { get; set; } = String.Empty;

        /// <summary>Working directory on the runner.</summary>
        public string WorkingDirectory { get; set; } = String.Empty;

        /// <summary>
        /// Arguments appended after the runtime's own, in the place a local launch appends its isolation arguments.
        /// The Admiral sets only workspace-root arguments, with paths as the runner resolves them; nothing that
        /// carries a credential travels here.
        /// </summary>
        public List<string> ExtraArguments
        {
            get => _ExtraArguments;
            set => _ExtraArguments = value ?? new List<string>();
        }

        /// <summary>Launch plan the runtime built. Set by the runtime before the host is called.</summary>
        public HarborLaunchRequest? Request { get; set; } = null;

        /// <summary>Durably bind the created Harbor job to the exact mission attempt before the runner receives it.</summary>
        public Func<HarborJobSnapshot, CancellationToken, Task<bool>>? OnRecorded { get; set; } = null;

        #endregion

        #region Private-Members

        private List<string> _ExtraArguments = new List<string>();

        #endregion
    }
}
