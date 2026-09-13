namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Services;

    /// <summary>
    /// Versioned payload of the event recorded for one definition-of-done gate evaluation.
    /// </summary>
    public class DefinitionOfDoneEvaluationRecord
    {
        #region Public-Members

        /// <summary>
        /// Event type under which evaluation records are stored.
        /// </summary>
        public const string EventType = "mission.definition_of_done_evaluated";

        /// <summary>
        /// Payload version this code writes and reads.
        /// </summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>
        /// Maximum stored length of the redacted output tail.
        /// </summary>
        public const int MaxOutputTailLength = 4000;

        /// <summary>
        /// Payload version.
        /// </summary>
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>
        /// Evaluation outcome.
        /// </summary>
        public DefinitionOfDoneEvaluationOutcomeEnum Outcome { get; set; } = DefinitionOfDoneEvaluationOutcomeEnum.EvaluationError;

        /// <summary>
        /// Reason the gate was skipped or not verifiable, or null.
        /// </summary>
        public string? SkippedReason { get; set; } = null;

        /// <summary>
        /// Label of the failing command, or null.
        /// </summary>
        public string? CommandLabel { get; set; } = null;

        /// <summary>
        /// Exit code of the failing command, or null when no command failed.
        /// </summary>
        public int? ExitCode { get; set; } = null;

        /// <summary>
        /// Failure classification, or null.
        /// </summary>
        public DefinitionOfDoneFailureClassEnum? FailureClass { get; set; } = null;

        /// <summary>
        /// Redacted and bounded tail of the failing command output, or null.
        /// </summary>
        public string? OutputTail { get; set; } = null;

        /// <summary>
        /// Captain that produced the evaluated work.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Dock whose worktree was evaluated.
        /// </summary>
        public string? DockId { get; set; } = null;

        /// <summary>
        /// Mission branch at evaluation time.
        /// </summary>
        public string? BranchName { get; set; } = null;

        /// <summary>
        /// Mission commit hash recorded at evaluation time, when known. Null does not mean no commit exists.
        /// </summary>
        public string? CommitHash { get; set; } = null;

        /// <summary>
        /// Mission recovery attempt count at evaluation time.
        /// </summary>
        public int RecoveryAttempts { get; set; } = 0;

        /// <summary>
        /// UTC time the evaluation started.
        /// </summary>
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC time the evaluation outcome was known.
        /// </summary>
        public DateTime CompletedUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public DefinitionOfDoneEvaluationRecord()
        {
        }

        /// <summary>
        /// Build a record from a gate result. A result with a skip reason is Skipped even though it carries Passed=true.
        /// </summary>
        /// <param name="result">Gate result.</param>
        /// <param name="startedUtc">Evaluation start time.</param>
        /// <returns>Evaluation record without mission identity.</returns>
        public static DefinitionOfDoneEvaluationRecord FromResult(DefinitionOfDoneResult result, DateTime startedUtc)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            DefinitionOfDoneEvaluationRecord record = new DefinitionOfDoneEvaluationRecord
            {
                StartedUtc = startedUtc,
                CompletedUtc = DateTime.UtcNow
            };

            if (!String.IsNullOrEmpty(result.SkippedReason))
            {
                record.Outcome = DefinitionOfDoneEvaluationOutcomeEnum.Skipped;
                record.SkippedReason = result.SkippedReason;
            }
            else if (!result.Passed)
            {
                record.Outcome = DefinitionOfDoneEvaluationOutcomeEnum.Failed;
                record.CommandLabel = result.CommandLabel;
                record.ExitCode = result.ExitCode;
                record.FailureClass = result.FailureClass;
                record.OutputTail = BoundOutput(result.OutputTail);
            }
            else
            {
                record.Outcome = DefinitionOfDoneEvaluationOutcomeEnum.Passed;
            }

            return record;
        }

        /// <summary>
        /// Build a record for a gate that was not run because the mission changed nothing.
        /// </summary>
        /// <param name="reason">Reason text.</param>
        /// <returns>Evaluation record without mission identity.</returns>
        public static DefinitionOfDoneEvaluationRecord NotVerifiable(string reason)
        {
            DateTime now = DateTime.UtcNow;
            return new DefinitionOfDoneEvaluationRecord
            {
                Outcome = DefinitionOfDoneEvaluationOutcomeEnum.NotVerifiable,
                SkippedReason = reason,
                StartedUtc = now,
                CompletedUtc = now
            };
        }

        /// <summary>
        /// Build a record for an evaluation that threw. The exception message is not stored.
        /// </summary>
        /// <param name="startedUtc">Evaluation start time.</param>
        /// <param name="exceptionType">Exception type name.</param>
        /// <returns>Evaluation record without mission identity.</returns>
        public static DefinitionOfDoneEvaluationRecord EvaluationError(DateTime startedUtc, string exceptionType)
        {
            return new DefinitionOfDoneEvaluationRecord
            {
                Outcome = DefinitionOfDoneEvaluationOutcomeEnum.EvaluationError,
                CommandLabel = "gate-evaluation",
                ExitCode = -1,
                FailureClass = DefinitionOfDoneFailureClassEnum.Infra,
                OutputTail = BoundOutput("Gate evaluation could not be completed (" + exceptionType + ")."),
                StartedUtc = startedUtc,
                CompletedUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Redact secret-shaped values and keep the last characters within <see cref="MaxOutputTailLength"/>,
        /// including the truncation marker. Applying it again to its own result returns the same text.
        /// </summary>
        /// <param name="output">Raw output.</param>
        /// <returns>Safe bounded output, or null.</returns>
        public static string? BoundOutput(string? output)
        {
            if (output == null) return null;
            string redacted = SecretRedactor.Redact(output);
            if (redacted.Length <= MaxOutputTailLength) return redacted;
            int keep = MaxOutputTailLength - _TruncationMarker.Length;
            return _TruncationMarker + redacted.Substring(redacted.Length - keep);
        }

        private const string _TruncationMarker = "...(earlier output truncated)\n";

        #endregion
    }
}
