namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// A bounded projection of a <see cref="CheckRun"/> that carries the verdict without the
    /// whole command log.
    /// </summary>
    /// <remarks>
    /// A check run's <see cref="CheckRun.Output"/> is the complete build or test log, routinely
    /// one to several megabytes. Returning it from a tool call exceeds the transport's output
    /// limit, so the caller receives a truncation error instead of a result and has to go and
    /// parse the record out of band - every time, including the common case where the only
    /// question was whether the check passed.
    /// <para>
    /// This view answers that question directly: status, exit code, and parsed test totals, plus
    /// the tail of the log, which is where a failure's cause almost always is. The full log stays
    /// on the record and is fetched deliberately, by a caller that has read the summary and
    /// decided it needs more.
    /// </para>
    /// </remarks>
    public class CheckRunSummaryView
    {
        #region Public-Members

        /// <summary>Check run identifier.</summary>
        public string Id { get; set; } = String.Empty;

        /// <summary>Tenant identifier.</summary>
        public string? TenantId { get; set; } = null;

        /// <summary>Vessel the check ran against.</summary>
        public string? VesselId { get; set; } = null;

        /// <summary>Linked mission, when the check is attached to one.</summary>
        public string? MissionId { get; set; } = null;

        /// <summary>Linked voyage, when the check is attached to one.</summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>Workflow profile that supplied the command.</summary>
        public string? WorkflowProfileId { get; set; } = null;

        /// <summary>Display label.</summary>
        public string? Label { get; set; } = null;

        /// <summary>Check type.</summary>
        public CheckRunTypeEnum Type { get; set; } = CheckRunTypeEnum.Build;

        /// <summary>Where the check came from.</summary>
        public CheckRunSourceEnum Source { get; set; } = CheckRunSourceEnum.Armada;

        /// <summary>Terminal or in-flight status. This is the answer most callers want.</summary>
        public CheckRunStatusEnum Status { get; set; } = CheckRunStatusEnum.Pending;

        /// <summary>Process exit code, when the command ran.</summary>
        public int? ExitCode { get; set; } = null;

        /// <summary>Branch the check ran against.</summary>
        public string? BranchName { get; set; } = null;

        /// <summary>Commit the check ran against.</summary>
        public string? CommitHash { get; set; } = null;

        /// <summary>Environment name for deployment-shaped checks.</summary>
        public string? EnvironmentName { get; set; } = null;

        /// <summary>Human-readable one-line summary built when the check completed.</summary>
        public string? Summary { get; set; } = null;

        /// <summary>Parsed pass/fail/skip totals, when the output could be parsed.</summary>
        public CheckRunTestSummary? TestSummary { get; set; } = null;

        /// <summary>Parsed coverage totals, when available.</summary>
        public CheckRunCoverageSummary? CoverageSummary { get; set; } = null;

        /// <summary>Collected artifacts, including any written test-results files.</summary>
        public List<CheckRunArtifact> Artifacts { get; set; } = new List<CheckRunArtifact>();

        /// <summary>Wall-clock duration of the command.</summary>
        public long? DurationMs { get; set; } = null;

        /// <summary>When the command started.</summary>
        public DateTime? StartedUtc { get; set; } = null;

        /// <summary>When the command finished.</summary>
        public DateTime? CompletedUtc { get; set; } = null;

        /// <summary>When the record was created.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>When the record last changed.</summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Size of the complete log in characters, so a caller can tell what it is not being sent.
        /// </summary>
        public int OutputLength { get; set; } = 0;

        /// <summary>
        /// Whether <see cref="OutputTail"/> is shorter than the complete log.
        /// </summary>
        public bool OutputTruncated { get; set; } = false;

        /// <summary>
        /// The last lines of the command output. A failure's cause is almost always here, and a
        /// bounded tail is what makes the common case answerable without a second call.
        /// </summary>
        public string? OutputTail { get; set; } = null;

        /// <summary>
        /// How to obtain the complete log when the tail is not enough.
        /// </summary>
        public string? OutputRetrieval { get; set; } = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Project a check run into its bounded view.
        /// </summary>
        /// <param name="run">The check run to project.</param>
        /// <param name="tailLines">
        /// How many trailing output lines to keep. Clamped to at least one line, so a caller
        /// cannot accidentally ask for a view that says nothing about why a check failed.
        /// </param>
        /// <returns>The bounded view.</returns>
        public static CheckRunSummaryView From(CheckRun run, int tailLines = 40)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (tailLines < 1) tailLines = 1;

            string output = run.Output ?? String.Empty;
            string tail = TakeTailLines(output, tailLines);

            return new CheckRunSummaryView
            {
                Id = run.Id,
                TenantId = run.TenantId,
                VesselId = run.VesselId,
                MissionId = run.MissionId,
                VoyageId = run.VoyageId,
                WorkflowProfileId = run.WorkflowProfileId,
                Label = run.Label,
                Type = run.Type,
                Source = run.Source,
                Status = run.Status,
                ExitCode = run.ExitCode,
                BranchName = run.BranchName,
                CommitHash = run.CommitHash,
                EnvironmentName = run.EnvironmentName,
                Summary = run.Summary,
                TestSummary = run.TestSummary,
                CoverageSummary = run.CoverageSummary,
                Artifacts = run.Artifacts ?? new List<CheckRunArtifact>(),
                DurationMs = run.DurationMs,
                StartedUtc = run.StartedUtc,
                CompletedUtc = run.CompletedUtc,
                CreatedUtc = run.CreatedUtc,
                LastUpdateUtc = run.LastUpdateUtc,
                OutputLength = output.Length,
                OutputTruncated = tail.Length < output.Length,
                OutputTail = tail.Length == 0 ? null : tail,
                OutputRetrieval = tail.Length < output.Length
                    ? "Full log omitted (" + output.Length + " chars). Call get_check_run with includeOutput=true for the complete output."
                    : null
            };
        }

        #endregion

        #region Private-Methods

        private static string TakeTailLines(string text, int lines)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;

            // A trailing newline terminates the last line rather than starting another one.
            // Counting it would return one fewer line than the caller asked for.
            int end = text.Length - 1;
            if (text[end] == '\n') end--;
            if (end < 0) return text;

            // Walk backwards to the newline that begins the Nth line from the end, so the tail is
            // whole lines rather than a cut through the middle of one.
            int seen = 0;
            for (int i = end; i >= 0; i--)
            {
                if (text[i] != '\n') continue;
                seen++;
                if (seen == lines) return text.Substring(i + 1);
            }

            return text;
        }

        #endregion
    }
}
