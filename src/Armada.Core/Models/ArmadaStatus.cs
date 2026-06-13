namespace Armada.Core.Models
{
    /// <summary>
    /// Aggregate status summary across all active work.
    /// </summary>
    public class ArmadaStatus
    {
        #region Public-Members

        /// <summary>
        /// Total number of registered captains.
        /// </summary>
        public int TotalCaptains { get; set; } = 0;

        /// <summary>
        /// Number of idle captains.
        /// </summary>
        public int IdleCaptains { get; set; } = 0;

        /// <summary>
        /// Number of working captains.
        /// </summary>
        public int WorkingCaptains { get; set; } = 0;

        /// <summary>
        /// Number of stalled captains.
        /// </summary>
        public int StalledCaptains { get; set; } = 0;

        /// <summary>
        /// Total number of active voyages.
        /// </summary>
        public int ActiveVoyages { get; set; } = 0;

        /// <summary>
        /// Missions grouped by status.
        /// </summary>
        public Dictionary<string, int> MissionsByStatus
        {
            get => _MissionsByStatus;
            set => _MissionsByStatus = value ?? new Dictionary<string, int>();
        }

        /// <summary>
        /// Active voyages with progress information.
        /// </summary>
        public List<VoyageProgress> Voyages
        {
            get => _Voyages;
            set => _Voyages = value ?? new List<VoyageProgress>();
        }

        /// <summary>
        /// Recent signals.
        /// </summary>
        public List<Signal> RecentSignals
        {
            get => _RecentSignals;
            set => _RecentSignals = value ?? new List<Signal>();
        }

        /// <summary>
        /// Timestamp of this status snapshot.
        /// </summary>
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Remote tunnel connectivity status.
        /// </summary>
        public RemoteTunnelStatus RemoteTunnel
        {
            get => _RemoteTunnel;
            set => _RemoteTunnel = value ?? new RemoteTunnelStatus();
        }

        /// <summary>
        /// Lightweight counts for structured delivery records.
        /// </summary>
        public StructuredDeliveryStatus StructuredDelivery
        {
            get => _StructuredDelivery;
            set => _StructuredDelivery = value ?? new StructuredDeliveryStatus();
        }

        /// <summary>
        /// Optional fleet-level aggregate of recent context-pack usage telemetry.
        /// </summary>
        public ContextPackUsageAggregate? ContextPackUsage { get; set; }

        #endregion

        #region Private-Members

        private Dictionary<string, int> _MissionsByStatus = new Dictionary<string, int>();
        private List<VoyageProgress> _Voyages = new List<VoyageProgress>();
        private List<Signal> _RecentSignals = new List<Signal>();
        private RemoteTunnelStatus _RemoteTunnel = new RemoteTunnelStatus();
        private StructuredDeliveryStatus _StructuredDelivery = new StructuredDeliveryStatus();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ArmadaStatus()
        {
        }

        #endregion
    }

    /// <summary>
    /// Aggregate counts for structured delivery records exposed on Armada status.
    /// </summary>
    public class StructuredDeliveryStatus
    {
        /// <summary>
        /// Objectives grouped by lifecycle status.
        /// </summary>
        public Dictionary<string, int> ObjectivesByStatus
        {
            get => _ObjectivesByStatus;
            set => _ObjectivesByStatus = value ?? new Dictionary<string, int>();
        }

        /// <summary>
        /// Backlog items grouped by backlog state.
        /// </summary>
        public Dictionary<string, int> BacklogByState
        {
            get => _BacklogByState;
            set => _BacklogByState = value ?? new Dictionary<string, int>();
        }

        /// <summary>
        /// Check runs grouped by status.
        /// </summary>
        public Dictionary<string, int> CheckRunsByStatus
        {
            get => _CheckRunsByStatus;
            set => _CheckRunsByStatus = value ?? new Dictionary<string, int>();
        }

        /// <summary>
        /// Releases grouped by status.
        /// </summary>
        public Dictionary<string, int> ReleasesByStatus
        {
            get => _ReleasesByStatus;
            set => _ReleasesByStatus = value ?? new Dictionary<string, int>();
        }

        /// <summary>
        /// Deployments grouped by status.
        /// </summary>
        public Dictionary<string, int> DeploymentsByStatus
        {
            get => _DeploymentsByStatus;
            set => _DeploymentsByStatus = value ?? new Dictionary<string, int>();
        }

        /// <summary>
        /// Incidents grouped by status.
        /// </summary>
        public Dictionary<string, int> IncidentsByStatus
        {
            get => _IncidentsByStatus;
            set => _IncidentsByStatus = value ?? new Dictionary<string, int>();
        }

        /// <summary>
        /// Open incidents grouped by severity.
        /// </summary>
        public Dictionary<string, int> OpenIncidentsBySeverity
        {
            get => _OpenIncidentsBySeverity;
            set => _OpenIncidentsBySeverity = value ?? new Dictionary<string, int>();
        }

        /// <summary>
        /// Pending required check count.
        /// </summary>
        public int PendingChecksRequired { get; set; } = 0;

        /// <summary>
        /// Pending optional check count.
        /// </summary>
        public int PendingChecksOptional { get; set; } = 0;

        /// <summary>
        /// Count of releases not yet shipped.
        /// </summary>
        public int InFlightReleasesCount { get; set; } = 0;

        /// <summary>
        /// Count of deployments that have not reached verified terminal state.
        /// </summary>
        public int UnverifiedDeploymentsCount { get; set; } = 0;

        /// <summary>
        /// Count of overdue runbook executions, when that surface can be computed cheaply.
        /// </summary>
        public int OverdueRunbookExecutionsCount { get; set; } = 0;

        private Dictionary<string, int> _ObjectivesByStatus = new Dictionary<string, int>();
        private Dictionary<string, int> _BacklogByState = new Dictionary<string, int>();
        private Dictionary<string, int> _CheckRunsByStatus = new Dictionary<string, int>();
        private Dictionary<string, int> _ReleasesByStatus = new Dictionary<string, int>();
        private Dictionary<string, int> _DeploymentsByStatus = new Dictionary<string, int>();
        private Dictionary<string, int> _IncidentsByStatus = new Dictionary<string, int>();
        private Dictionary<string, int> _OpenIncidentsBySeverity = new Dictionary<string, int>();
    }
}
