namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// One stage attempt: a captain's run on a mission from launch to exit, with the configuration it ran with (provenance), what was observed, what Armada did next, and the suspected cause when it fell short. The basis of the per-model scorecard (docs/MODEL_HETEROGENEITY.md).
    /// </summary>
    public class StageOutcome
    {
        #region Public-Members

        /// <summary>
        /// Unique identifier (sto_ prefix).
        /// </summary>
        public string Id
        {
            get => _Id;
            set => _Id = String.IsNullOrEmpty(value) ? throw new ArgumentNullException(nameof(Id)) : value;
        }

        /// <summary>
        /// Tenant of the mission.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Owner of the mission.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Mission (msn_ prefix).
        /// </summary>
        public string MissionId { get; set; } = "";

        /// <summary>
        /// Voyage, or null.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Vessel, or null.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Captain that ran the attempt.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Captain name at the time of the attempt.
        /// </summary>
        public string? CaptainName { get; set; } = null;

        /// <summary>
        /// Persona the mission routed as (Worker when the mission had none).
        /// </summary>
        public string? Persona { get; set; } = null;

        /// <summary>
        /// Attempt number on this mission, starting at 1.
        /// </summary>
        public int Attempt { get; set; } = 1;

        /// <summary>
        /// The mission's rework round.
        /// </summary>
        public int ReworkRound { get; set; } = 0;

        /// <summary>
        /// Runtime of the captain.
        /// </summary>
        public AgentRuntimeEnum? Runtime { get; set; } = null;

        /// <summary>
        /// Model the captain ran (the endpoint's model for an API-endpoint captain).
        /// </summary>
        public string? Model { get; set; } = null;

        /// <summary>
        /// Provider of the model endpoint (OpenAI, Ollama, ...), or null for a vendor CLI.
        /// </summary>
        public string? ModelProvider { get; set; } = null;

        /// <summary>
        /// Model endpoint, or null.
        /// </summary>
        public string? ModelEndpointId { get; set; } = null;

        /// <summary>
        /// Harbor the dock ran on, or null for the Admiral host.
        /// </summary>
        public string? HarborId { get; set; } = null;

        /// <summary>
        /// Configured context length, or null when not configured.
        /// </summary>
        public int? ContextTokens { get; set; } = null;

        /// <summary>
        /// Configured sampling temperature, or null.
        /// </summary>
        public double? Temperature { get; set; } = null;

        /// <summary>
        /// Configured top-p, or null.
        /// </summary>
        public double? TopP { get; set; } = null;

        /// <summary>
        /// Whether tools were enabled (API-endpoint captains), or null when not applicable.
        /// </summary>
        public bool? ToolsEnabled { get; set; } = null;

        /// <summary>
        /// Whether Armada MCP tools were enabled (API-endpoint captains), or null when not applicable.
        /// </summary>
        public bool? McpToolsEnabled { get; set; } = null;

        /// <summary>
        /// Configured reasoning effort, or null.
        /// </summary>
        public string? ReasoningEffort { get; set; } = null;

        /// <summary>
        /// Runtime (CLI or engine) version, or null when not reported.
        /// </summary>
        public string? RuntimeVersion { get; set; } = null;

        /// <summary>
        /// Model digest, or null when not reported.
        /// </summary>
        public string? ModelDigest { get; set; } = null;

        /// <summary>
        /// Version of the prompt templates the attempt ran with: a hash over the template set.
        /// </summary>
        public string? PromptVersion { get; set; } = null;

        /// <summary>
        /// Armada server version.
        /// </summary>
        public string? ArmadaVersion { get; set; } = null;

        /// <summary>
        /// Agent exit code, or null when unknown.
        /// </summary>
        public int? ExitCode { get; set; } = null;

        /// <summary>
        /// How the runtime classified the exit, or null.
        /// </summary>
        public RuntimeFailureKindEnum? RuntimeFailureKind { get; set; } = null;

        /// <summary>
        /// Stage check status, or null when no check ran (non-zero exit).
        /// </summary>
        public StageCheckStatusEnum? StageCheckStatus { get; set; } = null;

        /// <summary>
        /// Stage check reason, or null.
        /// </summary>
        public StageCheckReasonEnum? StageCheckReason { get; set; } = null;

        /// <summary>
        /// What Armada did next.
        /// </summary>
        public StageDispositionEnum Disposition { get; set; } = StageDispositionEnum.Pending;

        /// <summary>
        /// Observed failure: the mission's failure kind, or null.
        /// </summary>
        public MissionFailureKindEnum? FailureKind { get; set; } = null;

        /// <summary>
        /// Observed failure text (truncated), or null.
        /// </summary>
        public string? FailureReason { get; set; } = null;

        /// <summary>
        /// For a Judge attempt: the parsed verdict (Pass, Fail, NeedsRevision, SpecConflict, None), or null for other personas.
        /// </summary>
        public string? JudgeVerdict { get; set; } = null;

        /// <summary>
        /// For a review attempt: the mission whose work it reviewed, or null.
        /// </summary>
        public string? ReviewedMissionId { get; set; } = null;

        /// <summary>
        /// When escalated: the captain the work went to.
        /// </summary>
        public string? NextCaptainId { get; set; } = null;

        /// <summary>
        /// The commit the attempt left on the mission branch, or null when unknown. Recorded for an escalated attempt
        /// (its work so far) and a completed one (the produced commit).
        /// </summary>
        public string? CommitHash { get; set; } = null;

        /// <summary>
        /// When escalated: the later attempt (sto_ prefix) that completed the mission, or null while it has not.
        /// </summary>
        public string? FinishedByOutcomeId { get; set; } = null;

        /// <summary>
        /// When escalated: the captain whose attempt completed the mission, or null while it has not.
        /// </summary>
        public string? FinishedByCaptainId { get; set; } = null;

        /// <summary>
        /// When escalated and finished: true when the finishing captain kept this attempt's work (its commit is an
        /// ancestor of the finished commit), false when it replaced it, null when unknown (no commit, or the repository
        /// could not be read).
        /// </summary>
        public bool? PriorWorkKept { get; set; } = null;

        /// <summary>
        /// The deterministic contract repair Armada applied to this attempt (also recorded as a
        /// <c>mission.contract_repaired</c> event), or null when its report needed no repair.
        /// When one attempt needs more than one repair, the last one recorded is kept here; every repair has its own event.
        /// </summary>
        public ContractRepairKindEnum? ContractRepairKind { get; set; } = null;

        /// <summary>
        /// Suspected cause.
        /// </summary>
        public FailureCauseEnum SuspectedCause { get; set; } = FailureCauseEnum.None;

        /// <summary>
        /// What assigned the suspected cause.
        /// </summary>
        public FailureCauseSourceEnum CauseSource { get; set; } = FailureCauseSourceEnum.None;

        /// <summary>
        /// Rule that assigned the cause (fcr_ prefix), or null.
        /// </summary>
        public string? CauseRuleId { get; set; } = null;

        /// <summary>
        /// Evidence or a person's note on the cause.
        /// </summary>
        public string? CauseNote { get; set; } = null;

        /// <summary>
        /// User who classified the attempt by hand, or null.
        /// </summary>
        public string? ClassifiedByUserId { get; set; } = null;

        /// <summary>
        /// When the cause was last assigned (UTC).
        /// </summary>
        public DateTime? ClassifiedUtc { get; set; } = null;

        /// <summary>
        /// Whether the attempt was an evaluation or battery run (excluded from production scorecards).
        /// </summary>
        public bool IsEvaluation { get; set; } = false;

        /// <summary>
        /// When the attempt started (UTC), or null.
        /// </summary>
        public DateTime? StartedUtc { get; set; } = null;

        /// <summary>
        /// When the agent exited (UTC).
        /// </summary>
        public DateTime EndedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Attempt duration in milliseconds, or null.
        /// </summary>
        public long? DurationMs { get; set; } = null;

        /// <summary>
        /// UTC creation time.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC last update time.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.StageOutcomeIdPrefix, 24);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public StageOutcome()
        {
        }

        #endregion
    }
}
