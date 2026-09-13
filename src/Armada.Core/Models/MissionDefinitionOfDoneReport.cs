namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Read-only definition-of-done report for one mission: current configuration and the latest recorded evaluation.
    /// Reading it runs no gate and does not change landing readiness.
    /// </summary>
    public class MissionDefinitionOfDoneReport
    {
        #region Public-Members

        /// <summary>
        /// Mission identifier.
        /// </summary>
        public string MissionId { get; set; } = "";

        /// <summary>
        /// Current configuration, or null when it cannot be described.
        /// </summary>
        public DefinitionOfDoneConfiguration? Configuration { get; set; } = null;

        /// <summary>
        /// Reason the configuration could not be described, or null.
        /// </summary>
        public string? ConfigurationUnavailableReason { get; set; } = null;

        /// <summary>
        /// Whether the latest evaluation can be reported.
        /// </summary>
        public RecordedHistoryStateEnum HistoryState { get; set; } = RecordedHistoryStateEnum.NotRecorded;

        /// <summary>
        /// Reason the latest evaluation is unavailable, or null.
        /// </summary>
        public string? HistoryUnavailableReason { get; set; } = null;

        /// <summary>
        /// Latest recorded evaluation when <see cref="HistoryState"/> is Recorded; otherwise null.
        /// </summary>
        public DefinitionOfDoneEvaluationRecord? LatestEvaluation { get; set; } = null;

        #endregion
    }
}
