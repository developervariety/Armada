namespace Armada.Core.Models
{
    /// <summary>
    /// Read-only preview of the effective configuration for one objective dispatch.
    /// </summary>
    public class ObjectiveDispatchPreview
    {
        /// <summary>
        /// Objective identifier.
        /// </summary>
        public string ObjectiveId { get; set; } = String.Empty;

        /// <summary>
        /// True when the preview found no blocking issue.
        /// </summary>
        public bool IsReady { get; set; }

        /// <summary>
        /// Number of blocking findings.
        /// </summary>
        public int ErrorCount { get; set; }

        /// <summary>
        /// Number of non-blocking warning findings.
        /// </summary>
        public int WarningCount { get; set; }

        /// <summary>
        /// Resolved target vessel identifier.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Resolved pipeline identifier. Null means the Worker-only path.
        /// </summary>
        public string? PipelineId { get; set; } = null;

        /// <summary>
        /// Resolved pipeline name. WorkerOnly identifies the path with no pipeline row.
        /// </summary>
        public string? PipelineName { get; set; } = null;

        /// <summary>
        /// Effective first-stage start ref.
        /// </summary>
        public string? StartFromRef { get; set; } = null;

        /// <summary>
        /// Commit resolved from the effective start ref.
        /// </summary>
        public string? ResolvedStartCommit { get; set; } = null;

        /// <summary>
        /// Effective mission deliverable mode.
        /// </summary>
        public string DeliverableMode { get; set; } = String.Empty;

        /// <summary>
        /// Effective mission modes after objective defaults are applied.
        /// </summary>
        public List<string> EffectiveMissionModes { get; set; } = new List<string>();

        /// <summary>
        /// Effective first-stage refs and their resolved commits for operator missions.
        /// </summary>
        public List<ObjectiveDispatchMissionRef> MissionStartRefs { get; set; } = new List<ObjectiveDispatchMissionRef>();

        /// <summary>
        /// Server-rendered objective brief that execution receives.
        /// </summary>
        public string RenderedBrief { get; set; } = String.Empty;

        /// <summary>
        /// Captain coverage for required pipeline roles.
        /// </summary>
        public List<ObjectiveDispatchRole> RequiredRoles { get; set; } = new List<ObjectiveDispatchRole>();

        /// <summary>
        /// Checks required by dispatch arming configuration.
        /// </summary>
        public List<ObjectiveDispatchCheck> RequiredChecks { get; set; } = new List<ObjectiveDispatchCheck>();

        /// <summary>
        /// Incomplete or cyclic objective dependency chains. A shared dependency analyzer can populate this list.
        /// </summary>
        public List<List<string>> BlockingChains { get; set; } = new List<List<string>>();

        /// <summary>
        /// Complete typed dependency graph and bounded diagnostic paths.
        /// </summary>
        public ObjectiveDependencyAnalysis DependencyAnalysis { get; set; } = new ObjectiveDependencyAnalysis();

        /// <summary>
        /// Persona names of the pipeline stages that would be materialised, after a stored confirmed
        /// stage skip. WorkerOnly is reported as a single Worker stage.
        /// </summary>
        public List<string> EffectivePipelineStages { get; set; } = new List<string>();

        /// <summary>
        /// Persona names dropped by a stored confirmed stage skip. Empty when no skip is applied.
        /// </summary>
        public List<string> SkippedPipelineStages { get; set; } = new List<string>();

        /// <summary>
        /// True when the stored skip names a confirmer. An unconfirmed skip is not applied.
        /// </summary>
        public bool StageSkipConfirmed { get; set; }

        /// <summary>
        /// The named refusal a stored confirmed skip would hit, or null when the skip applies or none is stored.
        /// </summary>
        public string? StageSkipRefusalCode { get; set; } = null;

        /// <summary>
        /// Dispatch-preflight readiness: whether the recorded answers admit dispatch, which questions
        /// still block it, and the facts the code determined for the deterministic questions.
        /// </summary>
        public ObjectiveDispatchPreflight Preflight { get; set; } = new ObjectiveDispatchPreflight();

        /// <summary>
        /// All preview findings in deterministic evaluation order.
        /// </summary>
        public List<ObjectiveDispatchPreviewIssue> Issues { get; set; } = new List<ObjectiveDispatchPreviewIssue>();
    }
}
