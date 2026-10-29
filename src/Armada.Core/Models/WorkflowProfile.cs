namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;

    /// <summary>
    /// Defines how a project builds, tests, releases, and deploys.
    /// </summary>
    public class WorkflowProfile
    {
        /// <summary>
        /// Unique identifier.
        /// </summary>
        public string Id
        {
            get => _Id;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Id));
                _Id = value;
            }
        }

        /// <summary>
        /// Tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Owning user identifier.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Human-readable profile name.
        /// </summary>
        public string Name
        {
            get => _Name;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Name));
                _Name = value.Trim();
            }
        }

        /// <summary>
        /// Optional description.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// Scope of the profile.
        /// </summary>
        public WorkflowProfileScopeEnum Scope { get; set; } = WorkflowProfileScopeEnum.Global;

        /// <summary>
        /// Fleet identifier when scope is Fleet.
        /// </summary>
        public string? FleetId { get; set; } = null;

        /// <summary>
        /// Vessel identifier when scope is Vessel.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Whether this is the default profile for its scope.
        /// </summary>
        public bool IsDefault { get; set; } = false;

        /// <summary>
        /// Whether the profile is active.
        /// </summary>
        public bool Active { get; set; } = true;

        /// <summary>
        /// Optional language/runtime hints.
        /// </summary>
        public List<string> LanguageHints { get; set; } = new List<string>();

        /// <summary>
        /// Lint command.
        /// </summary>
        /// <summary>
        /// Environment variables applied to every command this profile runs.
        /// </summary>
        /// <remarks>
        /// A command can always export a variable inline, but that puts the value in every command
        /// string that needs it and leaves a check unrunnable in a dock whose profile forgot one.
        /// Declaring them once on the profile is what makes a guard that depends on a variable
        /// runnable in EVERY dock the profile resolves for.
        /// </remarks>
        public Dictionary<string, string> EnvironmentVariables { get; set; } = new Dictionary<string, string>();

        public string? LintCommand { get; set; } = null;

        /// <summary>
        /// Build command.
        /// </summary>
        public string? BuildCommand { get; set; } = null;

        /// <summary>
        /// Unit-test command.
        /// </summary>
        public string? UnitTestCommand { get; set; } = null;

        /// <summary>
        /// Optional fast subset of <see cref="UnitTestCommand"/>. Stored and returned with the profile.
        /// Checks and the definition-of-done gate keep running <see cref="UnitTestCommand"/>.
        /// An empty value means the profile has no abbreviated command.
        /// </summary>
        public string? AbbreviatedUnitTestCommand { get; set; } = null;

        /// <summary>
        /// Unit-test command used when no container runtime is available in the dock. Lets a vessel
        /// scope container-dependent test assemblies out (for example a category filter or an
        /// explicit project list) so the gate still verifies everything that does not need
        /// containers, instead of running fixtures that are guaranteed to fail. Null means no
        /// containerless variant is configured.
        /// </summary>
        public string? ContainerlessUnitTestCommand { get; set; } = null;

        /// <summary>
        /// A .NET regular expression with a named group <c>name</c> that reads one failing test name from
        /// one line of the unit-test output. The definition-of-done gate applies it in addition to its
        /// built-in matchers, so a runner whose failure lines no built-in matcher reads still gets its
        /// failing tests re-run alone. Validated on save. Null means the built-in matchers only.
        /// </summary>
        public string? FailedTestLinePattern { get; set; } = null;

        /// <summary>
        /// A complete command that re-runs only the named failing tests, plus every other step the
        /// unit-test command runs. The definition-of-done gate runs it instead of narrowing a
        /// <c>dotnet test</c> command, and passes the failing names in the <c>ARMADA_RERUN_TESTS</c>
        /// environment variable, one per line; a name never appears in the command text. Null means the
        /// gate narrows a <c>dotnet test</c> command itself.
        /// </summary>
        public string? IsolatedTestRerunCommand { get; set; } = null;

        /// <summary>
        /// Seconds each definition-of-done gate command run with this profile may take before it is stopped as a
        /// time limit: the vessel's build, unit-test and post-test-step retry, and a consumer's build and tests when
        /// this is the consumer's profile. An explicit value is shared by a logical DoD build or test family
        /// across post-test, isolated, flake and host-load retries in one evaluation. Build, test and consumer
        /// subjects have separate budgets. It also bounds a Build or UnitTest Check's full command plus its
        /// isolated retry together, excluding host-slot waits. Null retains the global per-command DoD policy
        /// and uses the global
        /// <c>DefinitionOfDone.CommandTimeoutSeconds</c> for DoD and the existing Check default for Checks. A save
        /// refuses a value outside [<see cref="MinimumGateCommandTimeoutSeconds"/>,
        /// <see cref="MaximumGateCommandTimeoutSeconds"/>].
        /// </summary>
        public int? GateCommandTimeoutSeconds { get; set; } = null;

        /// <summary>Fewest seconds <see cref="GateCommandTimeoutSeconds"/> may hold.</summary>
        public const int MinimumGateCommandTimeoutSeconds = 30;

        /// <summary>Most seconds <see cref="GateCommandTimeoutSeconds"/> may hold.</summary>
        public const int MaximumGateCommandTimeoutSeconds = 3600;

        /// <summary>
        /// Integration-test command.
        /// </summary>
        public string? IntegrationTestCommand { get; set; } = null;

        /// <summary>
        /// End-to-end test command.
        /// </summary>
        public string? E2ETestCommand { get; set; } = null;

        /// <summary>
        /// Migration command.
        /// </summary>
        public string? MigrationCommand { get; set; } = null;

        /// <summary>
        /// Security-scan command.
        /// </summary>
        public string? SecurityScanCommand { get; set; } = null;

        /// <summary>
        /// Performance-check command.
        /// </summary>
        public string? PerformanceCommand { get; set; } = null;

        /// <summary>
        /// Package command.
        /// </summary>
        public string? PackageCommand { get; set; } = null;

        /// <summary>
        /// Deployment-verification command.
        /// </summary>
        public string? DeploymentVerificationCommand { get; set; } = null;

        /// <summary>
        /// Rollback-verification command.
        /// </summary>
        public string? RollbackVerificationCommand { get; set; } = null;

        /// <summary>
        /// Publish-artifact command.
        /// </summary>
        public string? PublishArtifactCommand { get; set; } = null;

        /// <summary>
        /// Release-versioning command.
        /// </summary>
        public string? ReleaseVersioningCommand { get; set; } = null;

        /// <summary>
        /// Changelog-generation command.
        /// </summary>
        public string? ChangelogGenerationCommand { get; set; } = null;

        /// <summary>
        /// Required secret or configuration references.
        /// </summary>
        public List<string> RequiredSecrets { get; set; } = new List<string>();

        /// <summary>
        /// Structured workflow input references exposed over the API and dashboard.
        /// These are persisted through the legacy RequiredSecrets backing list to avoid
        /// a dedicated schema migration for readiness/preflight support.
        /// </summary>
        public List<WorkflowInputReference> RequiredInputs
        {
            get => WorkflowInputReference.ParseMany(RequiredSecrets);
            set => RequiredSecrets = WorkflowInputReference.SerializeMany(value?.Where(item => item != null) ?? Enumerable.Empty<WorkflowInputReference>());
        }

        /// <summary>
        /// Expected build or release artifacts.
        /// </summary>
        public List<string> ExpectedArtifacts { get; set; } = new List<string>();

        /// <summary>
        /// Repository-relative paths or glob patterns that this profile's required checks read, for example a data
        /// dictionary a docs check compares against the schema. Commands are shell strings, so Armada cannot derive
        /// these; the operator declares them. An Architect brief names them as files a plan must assign when the
        /// change touches what they describe, and a plan that assigns none of them is flagged. Empty means none.
        /// </summary>
        public List<string> GateInputPaths { get; set; } = new List<string>();

        /// <summary>
        /// Environment-specific delivery commands.
        /// </summary>
        public List<WorkflowEnvironmentProfile> Environments { get; set; } = new List<WorkflowEnvironmentProfile>();

        /// <summary>
        /// Creation timestamp.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last update timestamp.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.WorkflowProfileIdPrefix, 24);
        private string _Name = "Default Workflow";
    }
}
