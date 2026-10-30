namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The Check evidence a Judge brief carries: whether a passing UnitTest Check measured the exact commit
    /// the Judge reviews, and whether that Check executed its command or adopted a definition-of-done gate pass.
    /// </summary>
    /// <remarks>
    /// The Judge gate already requires green independent Checks for the reviewed commit. A passing
    /// UnitTest Check at that commit has run the vessel's full suite, so the Judge cites it and runs only
    /// focused tests for the changed code instead of a second full suite in its own dock. With no such
    /// Check the section says so, and the Judge runs the full suite. The section is built when the brief
    /// is written, so a Judge re-run after its Checks resolve reads the current state.
    /// </remarks>
    public static class JudgeCheckEvidence
    {
        #region Public-Members

        /// <summary>Prompt-budget module name of the section.</summary>
        public const string ModuleName = "mission.judge_check_evidence";

        /// <summary>Heading of the section in the Judge brief.</summary>
        public const string Heading = "## Check Evidence";

        #endregion

        #region Public-Methods

        /// <summary>
        /// The passing UnitTest Check that measured the reviewed commit, or null. Of the records for each
        /// Check only the latest decides (<see cref="CheckRunGateRules.SelectLatestPerCheck"/>). Every
        /// latest UnitTest record at the commit must have passed: a failed or still running one leaves no
        /// passing evidence. Records for another commit, intent markers and canceled records never count.
        /// </summary>
        /// <param name="checks">The Checks the Judge's gate reads. Null yields null.</param>
        /// <param name="reviewedCommit">The commit under review. Null or empty yields null.</param>
        /// <returns>The passing Check, or null when the commit has no passing UnitTest evidence.</returns>
        public static CheckRun? FindPassingUnitTest(IEnumerable<CheckRun>? checks, string? reviewedCommit)
        {
            if (checks == null || String.IsNullOrWhiteSpace(reviewedCommit)) return null;

            List<CheckRun> atCommit = CheckRunGateRules.SelectLatestPerCheck(checks)
                .Where(run => run.Type == CheckRunTypeEnum.UnitTest
                    && CheckRunGateRules.ParticipatesInRealSignalGate(run)
                    && CheckRunGateRules.SameCommit(run.CommitHash, reviewedCommit))
                .ToList();
            if (atCommit.Count == 0) return null;
            if (atCommit.Any(run => run.Status != CheckRunStatusEnum.Passed)) return null;

            return atCommit
                .OrderByDescending(run => run.CompletedUtc ?? run.CreatedUtc)
                .ThenBy(run => run.Id, StringComparer.Ordinal)
                .First();
        }

        /// <summary>
        /// The brief section for a Judge: the passing Check and the commit it measured, or a statement
        /// that no passing UnitTest Check exists for the commit under review. The lead says whether the Check
        /// executed its command or adopted a definition-of-done gate pass, and an adopted Check names the
        /// mission and commit of that pass and states that the Check ran no command.
        /// </summary>
        /// <param name="passing">The passing UnitTest Check, or null.</param>
        /// <param name="reviewedCommit">The commit under review, or null when none is recorded.</param>
        /// <param name="checks">Current lane Check records; omitted for a lookup that did not succeed.</param>
        /// <returns>The section text, ending with a newline.</returns>
        public static string BuildSection(CheckRun? passing, string? reviewedCommit, IEnumerable<CheckRun>? checks = null)
        {
            string? commit = String.IsNullOrWhiteSpace(reviewedCommit) ? null : reviewedCommit.Trim();
            if (passing != null && commit != null)
            {
                string label = String.IsNullOrWhiteSpace(passing.Label) ? passing.Type.ToString() : passing.Label!.Trim();
                return Heading + "\n" +
                    BuildPassingLead(passing, label, commit) +
                    "Cite this Check by id as your test evidence and run only focused tests for the code the diff changes, giving each " +
                    "command and its result. Run the full test suite instead when the diff changes test infrastructure or build configuration.\n"
                    + BuildSnapshot(checks, commit);
            }

            return Heading + "\n" +
                "No passing UnitTest Check is recorded for " +
                (commit == null ? "the commit under review" : "commit `" + commit + "`, the commit under review,") +
                " when this brief was written. Read current Check receipts before repeating work. Run the full test suite in the FOREGROUND as your test evidence if no current passing UnitTest receipt covers this commit.\n"
                + BuildSnapshot(checks, commit);
        }

        private static string BuildPassingLead(CheckRun passing, string label, string commit)
        {
            if (passing.SatisfiedBy == null)
                return "Executed: UnitTest Check `" + passing.Id + "` (" + label + ") ran its command and passed at commit `" + commit + "`, the commit under review. ";

            string sourceCommit = String.IsNullOrWhiteSpace(passing.SatisfiedBy.CommitHash) ? commit : passing.SatisfiedBy.CommitHash.Trim();
            return "Adopted from DoD: UnitTest Check `" + passing.Id + "` (" + label + ") passed at commit `" + commit + "`, the commit under review, "
                + "by adopting the definition-of-done gate pass of mission `" + passing.SatisfiedBy.MissionId + "` at commit `" + sourceCommit + "`; "
                + "this Check ran no command. ";
        }

        private static string BuildSnapshot(IEnumerable<CheckRun>? checks, string? commit)
        {
            StringBuilder section = new StringBuilder();
            section.Append("Snapshot only: Checks can complete after this brief was written. Before claiming required evidence is missing or failed, read current receipts with armada_enumerate entityType=checks and this voyageId (or missionId for a standalone mission), includeTestOutput=false. Read every result page and match the Check ID, reviewed commit, type, source and current status. Pending, failed or unavailable evidence remains unresolved.\n");
            section.Append("A separate local tool is not a substitute for a required native Check. It adds a requirement only when the actual workflow or acceptance criteria require it.\n");
            if (checks == null || commit == null) return section.ToString();
            foreach (CheckRun run in CheckRunGateRules.SelectLatestPerCheck(checks)
                .Where(run => CheckRunGateRules.ParticipatesInRealSignalGate(run) && CheckRunGateRules.SameCommit(run.CommitHash, commit))
                .OrderBy(run => run.Type).ThenBy(run => run.Id, StringComparer.Ordinal))
            {
                section.Append("- Check `").Append(run.Id).Append("`: ").Append(run.Type)
                    .Append(", source ").Append(run.Source).Append(", status ").Append(run.Status)
                    .Append(", commit `").Append(run.CommitHash).Append('`');
                if (run.Source == CheckRunSourceEnum.Armada && CheckRunGateRules.IsNativeSlopRun(run))
                    section.Append("; Armada native reviewed-diff classifier");
                if (run.SatisfiedBy != null)
                    section.Append("; adopted ").Append(run.SatisfiedBy.Kind).Append(" evidence from mission `")
                        .Append(run.SatisfiedBy.MissionId).Append("`, commit `").Append(run.SatisfiedBy.CommitHash)
                        .Append("`; this Check ran no command");
                section.Append(".\n");
            }
            return section.ToString();
        }

        #endregion
    }
}
