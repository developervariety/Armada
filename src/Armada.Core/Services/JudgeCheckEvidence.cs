namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The independent Check evidence a Judge brief carries: whether a passing UnitTest Check measured the
    /// exact commit the Judge reviews.
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
        public const string Heading = "## Independent Check Evidence";

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
        /// that no passing UnitTest Check exists for the commit under review.
        /// </summary>
        /// <param name="passing">The passing UnitTest Check, or null.</param>
        /// <param name="reviewedCommit">The commit under review, or null when none is recorded.</param>
        /// <returns>The section text, ending with a newline.</returns>
        public static string BuildSection(CheckRun? passing, string? reviewedCommit)
        {
            string? commit = String.IsNullOrWhiteSpace(reviewedCommit) ? null : reviewedCommit.Trim();
            if (passing != null && commit != null)
            {
                string label = String.IsNullOrWhiteSpace(passing.Label) ? passing.Type.ToString() : passing.Label!.Trim();
                return Heading + "\n" +
                    "Independent UnitTest Check `" + passing.Id + "` (" + label + ") passed at commit `" + commit + "`, the commit under review. " +
                    "Cite this Check by id as your test evidence and run only focused tests for the code the diff changes, giving each " +
                    "command and its result. Run the full test suite instead when the diff changes test infrastructure or build configuration.\n";
            }

            return Heading + "\n" +
                "No passing independent UnitTest Check is recorded for " +
                (commit == null ? "the commit under review" : "commit `" + commit + "`, the commit under review,") +
                " when this brief was written. Run the full test suite in the FOREGROUND as your test evidence.\n";
        }

        #endregion
    }
}
