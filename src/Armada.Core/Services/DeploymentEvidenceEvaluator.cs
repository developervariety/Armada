namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Decides whether an environment's required Check types passed at one deployment candidate commit.
    /// </summary>
    /// <remarks>
    /// Each required type is decided on its own: a passing Build never stands in for a UnitTest. A record counts for
    /// a type only when it measured this vessel at exactly the candidate's full commit. A record that Armada ran must
    /// have run the workflow profile's own command for that type, so a run narrowed by a filter or a command override
    /// is not proof of the whole Check. A record imported from an external system counts as that system's verdict at
    /// the commit. Of the records that count, the newest Passed or Failed one decides; a Canceled, Pending or Running
    /// record neither proves nor disproves the type. A failure newer than a pass therefore withdraws the proof.
    /// </remarks>
    public static class DeploymentEvidenceEvaluator
    {
        #region Public-Members

        /// <summary>
        /// Prefix of the refusal when a required type has no deciding Passed record at the candidate commit.
        /// </summary>
        public const string MissingReason = "deployment_validation_missing";

        /// <summary>
        /// Prefix of the refusal when a required type's deciding record at the candidate commit failed.
        /// </summary>
        public const string FailedReason = "deployment_validation_failed";

        /// <summary>
        /// Prefix of the refusal when the deployment has no pinned commit to measure.
        /// </summary>
        public const string UnpinnedReason = "deployment_source_unpinned";

        #endregion

        #region Public-Methods

        /// <summary>
        /// True when a Check type measures source and can therefore be required at a candidate commit. Deploy,
        /// rollback, smoke, health and the verification types measure a running target instead.
        /// </summary>
        /// <param name="type">Check type.</param>
        /// <returns>True when the type can be required.</returns>
        public static bool IsRequirableType(CheckRunTypeEnum type)
        {
            return type != CheckRunTypeEnum.Deploy
                && type != CheckRunTypeEnum.Rollback
                && type != CheckRunTypeEnum.SmokeTest
                && type != CheckRunTypeEnum.HealthCheck
                && type != CheckRunTypeEnum.DeploymentVerification
                && type != CheckRunTypeEnum.RollbackVerification;
        }

        /// <summary>
        /// Evaluate the required evidence for one candidate.
        /// </summary>
        /// <param name="requiredTypes">Check types the environment requires.</param>
        /// <param name="vesselId">Vessel the candidate belongs to.</param>
        /// <param name="sourceCommit">Full candidate commit; null when the deployment is not pinned.</param>
        /// <param name="records">Check records of the vessel to judge, in any order.</param>
        /// <param name="expectedCommands">The workflow profile's command per required type; a missing entry means
        /// the profile defines none, so no record Armada ran can prove that type.</param>
        /// <returns>The verdict; never null.</returns>
        public static DeploymentEvidenceResult Evaluate(
            IReadOnlyList<CheckRunTypeEnum> requiredTypes,
            string vesselId,
            string? sourceCommit,
            IReadOnlyList<CheckRun> records,
            IReadOnlyDictionary<CheckRunTypeEnum, string> expectedCommands)
        {
            if (requiredTypes == null) throw new ArgumentNullException(nameof(requiredTypes));
            if (records == null) throw new ArgumentNullException(nameof(records));
            if (expectedCommands == null) throw new ArgumentNullException(nameof(expectedCommands));

            DeploymentEvidenceResult result = new DeploymentEvidenceResult();
            List<CheckRunTypeEnum> required = requiredTypes.Distinct().ToList();
            if (required.Count == 0)
            {
                result.Satisfied = true;
                result.Summary = "the environment requires no Check evidence";
                return result;
            }

            string commit = (sourceCommit ?? String.Empty).Trim();
            if (commit.Length != 40)
            {
                result.Satisfied = false;
                result.Summary = UnpinnedReason + ": the environment requires " + String.Join(", ", required)
                    + " at the deployment's source commit, but the deployment is not pinned to a full commit.";
                return result;
            }

            List<string> lines = new List<string>();
            bool anyFailed = false;
            bool allPassed = true;
            foreach (CheckRunTypeEnum type in required)
            {
                string? expected = expectedCommands.TryGetValue(type, out string? command) ? command : null;
                TypeVerdict verdict = DecideType(type, vesselId, commit, records, expected);
                lines.Add(verdict.Line);
                if (verdict.PassedRunId != null)
                {
                    result.CheckRunIds.Add(verdict.PassedRunId);
                }
                else
                {
                    allPassed = false;
                    if (verdict.Failed) anyFailed = true;
                }
            }

            result.Satisfied = allPassed;
            string prefix = allPassed ? "required Checks passed at " + Short(commit) : (anyFailed ? FailedReason : MissingReason) + ": ";
            result.Summary = allPassed
                ? prefix + ": " + String.Join("; ", lines)
                : prefix + String.Join("; ", lines);
            return result;
        }

        #endregion

        #region Private-Methods

        private static TypeVerdict DecideType(
            CheckRunTypeEnum type,
            string vesselId,
            string commit,
            IReadOnlyList<CheckRun> records,
            string? expectedCommand)
        {
            List<CheckRun> typed = records
                .Where(run => run != null
                    && run.Type == type
                    && String.Equals(run.VesselId, vesselId, StringComparison.Ordinal))
                .OrderByDescending(run => run.CreatedUtc)
                .ThenByDescending(run => run.Id, StringComparer.Ordinal)
                .ToList();

            List<CheckRun> atCommit = typed
                .Where(run => !String.IsNullOrWhiteSpace(run.CommitHash)
                    && String.Equals(run.CommitHash!.Trim(), commit, StringComparison.OrdinalIgnoreCase))
                .ToList();

            string expected = NormalizeCommand(expectedCommand);
            List<CheckRun> counted = atCommit
                .Where(run => run.Source != CheckRunSourceEnum.Armada
                    || (expected.Length > 0 && String.Equals(NormalizeCommand(run.Command), expected, StringComparison.Ordinal)))
                .ToList();

            CheckRun? deciding = counted.FirstOrDefault(run => run.Status == CheckRunStatusEnum.Passed || run.Status == CheckRunStatusEnum.Failed);
            if (deciding != null && deciding.Status == CheckRunStatusEnum.Passed)
                return new TypeVerdict { PassedRunId = deciding.Id, Line = type + " passed in " + deciding.Id };

            if (deciding != null)
            {
                return new TypeVerdict
                {
                    Failed = true,
                    Line = type + " failed in " + deciding.Id + " at " + Short(commit)
                        + (deciding.ExitCode.HasValue ? " (exit " + deciding.ExitCode.Value + ")" : String.Empty)
                };
            }

            if (atCommit.Count > counted.Count)
            {
                string reason = expected.Length == 0
                    ? "the workflow profile defines no " + type + " command, so no record Armada ran can prove it"
                    : "the " + type + " records at " + Short(commit) + " ran a different command than the workflow profile's, such as a filtered or overridden run";
                return new TypeVerdict { Line = type + " not proven: " + reason };
            }

            if (counted.Count > 0)
                return new TypeVerdict { Line = type + " not proven: its records at " + Short(commit) + " did not finish with a verdict" };

            CheckRun? newest = typed.FirstOrDefault();
            if (newest != null)
            {
                return new TypeVerdict
                {
                    Line = type + " not proven: no record measured " + Short(commit) + "; the newest " + type + " record " + newest.Id
                        + " measured " + (String.IsNullOrWhiteSpace(newest.CommitHash) ? "no recorded commit" : Short(newest.CommitHash!.Trim()))
                };
            }

            return new TypeVerdict { Line = type + " not proven: the vessel has no " + type + " record" };
        }

        private static string NormalizeCommand(string? command)
        {
            if (String.IsNullOrWhiteSpace(command)) return String.Empty;
            return String.Join(" ", command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string Short(string commit)
        {
            return commit.Length <= 12 ? commit : commit.Substring(0, 12);
        }

        #endregion

        #region Private-Types

        private sealed class TypeVerdict
        {
            public string? PassedRunId { get; set; } = null;

            public bool Failed { get; set; } = false;

            public string Line { get; set; } = String.Empty;
        }

        #endregion
    }
}
