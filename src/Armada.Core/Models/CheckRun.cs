namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;

    /// <summary>
    /// Structured record of a build, test, deploy, or related check execution.
    /// </summary>
    public class CheckRun
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
        /// Workflow profile used for this run.
        /// </summary>
        public string? WorkflowProfileId { get; set; } = null;

        /// <summary>
        /// Vessel linked to this run.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Mission linked to this run.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Voyage linked to this run.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Deployment linked to this run.
        /// </summary>
        public string? DeploymentId { get; set; } = null;

        /// <summary>
        /// Optional display label.
        /// </summary>
        public string? Label { get; set; } = null;

        /// <summary>
        /// Type of check being executed.
        /// </summary>
        public CheckRunTypeEnum Type { get; set; } = CheckRunTypeEnum.Build;

        /// <summary>
        /// Whether this run was executed by Armada or imported from an external system.
        /// </summary>
        public CheckRunSourceEnum Source { get; set; } = CheckRunSourceEnum.Armada;

        /// <summary>
        /// Current status.
        /// </summary>
        public CheckRunStatusEnum Status { get; set; } = CheckRunStatusEnum.Pending;

        /// <summary>
        /// Optional external provider name when the run was imported.
        /// </summary>
        public string? ProviderName { get; set; } = null;

        /// <summary>
        /// Optional provider-specific run identifier.
        /// </summary>
        public string? ExternalId { get; set; } = null;

        /// <summary>
        /// Optional deep link to the imported run.
        /// </summary>
        public string? ExternalUrl { get; set; } = null;

        /// <summary>
        /// Environment name when applicable.
        /// </summary>
        public string? EnvironmentName { get; set; } = null;

        /// <summary>
        /// Command that was executed.
        /// </summary>
        public string Command
        {
            get => _Command;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Command));
                _Command = value;
            }
        }

        /// <summary>
        /// Working directory used for execution.
        /// </summary>
        public string? WorkingDirectory { get; set; } = null;

        /// <summary>
        /// Branch name associated with the run.
        /// </summary>
        public string? BranchName { get; set; } = null;

        /// <summary>
        /// Commit hash associated with the run.
        /// </summary>
        public string? CommitHash { get; set; } = null;

        /// <summary>
        /// Post-land regression class this Check guards, or None.
        /// </summary>
        public RegressionPurposeEnum RegressionPurpose { get; set; } = RegressionPurposeEnum.None;

        /// <summary>
        /// Objective whose landed change this Check guards.
        /// </summary>
        public string? RegressionObjectiveId { get; set; } = null;

        /// <summary>
        /// Landed commit this Check guards.
        /// </summary>
        public string? RegressionLandedCommit { get; set; } = null;

        /// <summary>
        /// Exit code when available.
        /// </summary>
        public int? ExitCode { get; set; } = null;

        /// <summary>
        /// Combined command output.
        /// </summary>
        public string? Output { get; set; } = null;

        /// <summary>
        /// Human-readable summary.
        /// </summary>
        public string? Summary { get; set; } = null;

        /// <summary>
        /// True when this Check was read without its Output, so its null Output does not mean the run printed
        /// nothing. An update of such a Check is refused, because it would overwrite the stored log with null.
        /// </summary>
        internal bool OutputOmitted { get; set; } = false;

        /// <summary>
        /// Length in characters of the Output a listing withheld, computed by the database; null when the Output
        /// was read or the run printed nothing. Never stored.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? OutputLength { get; internal set; } = null;

        /// <summary>
        /// Parsed test summary when the run output contained recognizable test-result totals.
        /// </summary>
        public CheckRunTestSummary? TestSummary { get; set; } = null;

        /// <summary>
        /// Parsed coverage summary when the run artifacts or output contained recognizable coverage data.
        /// </summary>
        public CheckRunCoverageSummary? CoverageSummary { get; set; } = null;

        /// <summary>
        /// Discovered artifacts.
        /// </summary>
        public List<CheckRunArtifact> Artifacts { get; set; } = new List<CheckRunArtifact>();

        /// <summary>
        /// Run duration in milliseconds when known. A finished run that started carries the time from
        /// <see cref="StartedUtc"/> to <see cref="CompletedUtc"/>; see <see cref="SettleRunDuration"/>.
        /// </summary>
        public long? DurationMs { get; set; } = null;

        /// <summary>
        /// UTC time the run asked for the host-wide command slot, after preparation and before the
        /// wait. Distinct from creation (armed) time and from <see cref="StartedUtc"/>.
        /// </summary>
        public DateTime? SlotRequestedUtc { get; set; } = null;

        /// <summary>
        /// Time from durable record creation until the command acquired the shared host slot.
        /// </summary>
        public long? QueueDurationMs => StartedUtc.HasValue
            ? Math.Max(0L, Convert.ToInt64(Math.Round((StartedUtc.Value - CreatedUtc).TotalMilliseconds)))
            : null;

        /// <summary>
        /// Run start time in UTC.
        /// </summary>
        public DateTime? StartedUtc { get; set; } = null;

        /// <summary>
        /// Run completion time in UTC.
        /// </summary>
        public DateTime? CompletedUtc { get; set; } = null;

        /// <summary>
        /// Creation timestamp.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last update timestamp.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.CheckRunIdPrefix, 24);
        private string _Command = "echo";

        /// <summary>
        /// Set <see cref="DurationMs"/> to the time from <see cref="StartedUtc"/> to <see cref="CompletedUtc"/>.
        /// Every path that ends a started run calls this, so a timeout, an error or a restart records how long
        /// the command ran, the same as a command that exited. A run without both times keeps its duration.
        /// </summary>
        public void SettleRunDuration()
        {
            if (!StartedUtc.HasValue || !CompletedUtc.HasValue) return;
            DurationMs = Math.Max(0L, Convert.ToInt64(Math.Round((CompletedUtc.Value - StartedUtc.Value).TotalMilliseconds)));
        }

        /// <summary>
        /// A copy of this run without its <see cref="Output"/>, carrying <see cref="OutputLength"/> instead. A
        /// change notification sends this copy: the output can run to megabytes, and a reader that needs it
        /// re-reads the run by id. The copy shares this run's collections and cannot be stored.
        /// </summary>
        /// <returns>The copy.</returns>
        public CheckRun WithoutOutput()
        {
            CheckRun copy = (CheckRun)MemberwiseClone();
            copy.OutputLength = OutputOmitted ? OutputLength : (String.IsNullOrEmpty(Output) ? null : Output.Length);
            copy.Output = null;
            copy.OutputOmitted = true;
            return copy;
        }
    }
}
