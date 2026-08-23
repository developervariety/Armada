namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;

    /// <summary>
    /// Abstract database driver providing access to all entity methods.
    /// </summary>
    public abstract class DatabaseDriver : IDisposable
    {
        #region Public-Members

        /// <summary>
        /// Fleet operations.
        /// </summary>
        public IFleetMethods Fleets { get; protected set; } = null!;

        /// <summary>
        /// Vessel operations.
        /// </summary>
        public IVesselMethods Vessels { get; protected set; } = null!;

        /// <summary>
        /// Captain operations.
        /// </summary>
        public ICaptainMethods Captains { get; protected set; } = null!;

        /// <summary>
        /// Mission operations.
        /// </summary>
        public IMissionMethods Missions { get; protected set; } = null!;

        /// <summary>
        /// Voyage operations.
        /// </summary>
        public IVoyageMethods Voyages { get; protected set; } = null!;

        /// <summary>
        /// Planning session operations.
        /// </summary>
        public IPlanningSessionMethods PlanningSessions { get; protected set; } = null!;

        /// <summary>
        /// Planning session message operations.
        /// </summary>
        public IPlanningSessionMessageMethods PlanningSessionMessages { get; protected set; } = null!;

        /// <summary>
        /// Coordination room operations.
        /// </summary>
        public ICoordinationRoomMethods CoordinationRooms { get; protected set; } = null!;

        /// <summary>
        /// Coordination message operations.
        /// </summary>
        public ICoordinationMessageMethods CoordinationMessages { get; protected set; } = null!;

        /// <summary>
        /// Coordination participant (presence) operations.
        /// </summary>
        public ICoordinationParticipantMethods CoordinationParticipants { get; protected set; } = null!;

        /// <summary>
        /// Coordination claim (reservation) operations.
        /// </summary>
        public ICoordinationClaimMethods CoordinationClaims { get; protected set; } = null!;

        /// <summary>
        /// Objective/backlog operations.
        /// </summary>
        public IObjectiveMethods Objectives { get; protected set; } = null!;

        /// <summary>
        /// Objective refinement session operations.
        /// </summary>
        public IObjectiveRefinementSessionMethods ObjectiveRefinementSessions { get; protected set; } = null!;

        /// <summary>
        /// Objective refinement transcript message operations.
        /// </summary>
        public IObjectiveRefinementMessageMethods ObjectiveRefinementMessages { get; protected set; } = null!;

        /// <summary>
        /// Dock operations.
        /// </summary>
        public IDockMethods Docks { get; protected set; } = null!;

        /// <summary>
        /// Signal operations.
        /// </summary>
        public ISignalMethods Signals { get; protected set; } = null!;

        /// <summary>
        /// Event operations.
        /// </summary>
        public IEventMethods Events { get; protected set; } = null!;

        /// <summary>
        /// Request-history operations.
        /// </summary>
        public IRequestHistoryMethods RequestHistory { get; protected set; } = null!;

        /// <summary>
        /// Merge entry operations.
        /// </summary>
        public IMergeEntryMethods MergeEntries { get; protected set; } = null!;

        /// <summary>
        /// Durable landing job operations.
        /// </summary>
        public ILandingJobMethods LandingJobs { get; protected set; } = null!;

        /// <summary>
        /// Tenant operations.
        /// </summary>
        public ITenantMethods Tenants { get; protected set; } = null!;

        /// <summary>
        /// User operations.
        /// </summary>
        public IUserMethods Users { get; protected set; } = null!;

        /// <summary>
        /// Credential operations.
        /// </summary>
        public ICredentialMethods Credentials { get; protected set; } = null!;

        /// <summary>
        /// Prompt template operations.
        /// </summary>
        public IPromptTemplateMethods PromptTemplates { get; protected set; } = null!;

        /// <summary>
        /// Persona operations.
        /// </summary>
        public IPersonaMethods Personas { get; protected set; } = null!;

        /// <summary>
        /// Pipeline operations.
        /// </summary>
        public IPipelineMethods Pipelines { get; protected set; } = null!;

        /// <summary>
        /// Playbook operations.
        /// </summary>
        public IPlaybookMethods Playbooks { get; protected set; } = null!;

        /// <summary>
        /// Workflow-profile operations.
        /// </summary>
        public IWorkflowProfileMethods WorkflowProfiles { get; protected set; } = null!;

        /// <summary>
        /// Deployment environment operations.
        /// </summary>
        public IDeploymentEnvironmentMethods Environments { get; protected set; } = null!;

        /// <summary>
        /// Structured check-run operations.
        /// </summary>
        public ICheckRunMethods CheckRuns { get; protected set; } = null!;

        /// <summary>
        /// Release operations.
        /// </summary>
        public IReleaseMethods Releases { get; protected set; } = null!;

        /// <summary>
        /// Deployment operations.
        /// </summary>
        public IDeploymentMethods Deployments { get; protected set; } = null!;

        /// <summary>
        /// Vessel pack-curate hint operations (v2-F1).
        /// </summary>
        public IVesselPackHintMethods VesselPackHints { get; protected set; } = null!;

        /// <summary>
        /// Project profile operations. A provider that does not assign this leaves it null, and the
        /// first caller then fails with a NullReferenceException naming nothing useful; see
        /// <see cref="FindUnwiredMethodSets"/> for the check that reports the gap instead.
        /// </summary>
        public IProjectProfileMethods ProjectProfiles { get; protected set; } = null!;

        /// <summary>
        /// Skill operations.
        /// </summary>
        public ISkillMethods Skills { get; protected set; } = null!;

        /// <summary>
        /// Coordination lease operations.
        /// </summary>
        public ICoordinationLeaseMethods CoordinationLeases { get; protected set; } = null!;

        /// <summary>
        /// Background job operations.
        /// </summary>
        public IJobMethods Jobs { get; protected set; } = null!;

        /// <summary>
        /// Per-model token accounting operations.
        /// </summary>
        public ITokenUsageMethods TokenUsage { get; protected set; } = null!;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public DatabaseDriver()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Report which entity method sets this driver left unassigned. Every method set is declared
        /// <c>null!</c> and assigned by the concrete provider's constructor, so a provider that gains a
        /// new entity but never assigns it compiles cleanly and then throws a NullReferenceException at
        /// the first call, from a stack that names neither the provider nor the missing entity. This
        /// turns that into a list a caller can act on.
        /// </summary>
        /// <returns>Names of the unassigned method sets, empty when the driver is fully wired.</returns>
        public List<string> FindUnwiredMethodSets()
        {
            List<string> missing = new List<string>();

            if (ProjectProfiles == null) missing.Add(nameof(ProjectProfiles));
            if (Skills == null) missing.Add(nameof(Skills));
            if (CoordinationLeases == null) missing.Add(nameof(CoordinationLeases));

            return missing;
        }

        /// <summary>
        /// Initialize the database schema and seed data.
        /// </summary>
        public abstract Task InitializeAsync(CancellationToken token = default);

        /// <summary>
        /// Execute an action inside a database transaction.
        /// </summary>
        /// <typeparam name="T">Return type.</typeparam>
        /// <param name="action">Action to execute.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Result from the action.</returns>
        public abstract Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action, CancellationToken token = default);

        /// <summary>
        /// Execute an action inside a database transaction.
        /// </summary>
        /// <param name="action">Action to execute.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task ExecuteInTransactionAsync(Func<Task> action, CancellationToken token = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            await ExecuteInTransactionAsync(async () =>
            {
                await action().ConfigureAwait(false);
                return true;
            }, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Dispose.
        /// </summary>
        /// <summary>
        /// Get the highest applied schema-migration version, or 0 when the migrations table does not
        /// exist yet. Every provider already implemented this; declaring it here is what lets a caller
        /// holding a DatabaseDriver ask, rather than needing the concrete provider type.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Applied schema version, or 0 when no migrations have run.</returns>
        public abstract Task<int> GetSchemaVersionAsync(CancellationToken token = default);

        public abstract void Dispose();

        #endregion
    }
}
