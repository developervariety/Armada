namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using Armada.Core.Models;

    /// <summary>
    /// The prompt section a captain receives when its work is sent back: the reviewer's blocking findings as a
    /// numbered list with one demand per finding, then the fixed rework demands, then the output contract
    /// restated last. Findings reach the captain whole. A bound that cannot hold every finding drops whole
    /// lower-priority findings and the section states how many were dropped.
    /// </summary>
    public static class ReworkPromptBuilder
    {
        #region Public-Members

        /// <summary>Default character bound for the numbered findings.</summary>
        public const int DefaultFindingsBudgetChars = 4000;

        /// <summary>The output contract a reworking captain ends its report with.</summary>
        public const string OutputContract =
            "End with a standalone line `[ARMADA:RESULT] COMPLETE` followed by a short summary that says, for each numbered finding, "
            + "whether you fixed it or rejected it with evidence.";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the rework section for a review.
        /// </summary>
        /// <param name="review">The reviewer's written output, a Judge review or a reviewer comment.</param>
        /// <param name="findingsBudgetChars">Character bound for the numbered findings.</param>
        /// <param name="completeReviewPointer">Optional sentence naming where the complete review can be read.</param>
        /// <returns>The section, without a trailing newline.</returns>
        public static string Build(string? review, int findingsBudgetChars = DefaultFindingsBudgetChars, string? completeReviewPointer = null)
        {
            string text = (review ?? String.Empty).Trim();
            BlockingFindingSelection selection = StageReportEssentials.SelectBlockingFindings(text, findingsBudgetChars);
            List<string> findings = selection.Items;
            bool freeform = findings.Count == 0;

            StringBuilder section = new StringBuilder();
            section.Append("## Review Feedback\n");
            section.Append("The reviewer sent this work back. The findings below are numbered; each has one demand.\n\n");

            if (freeform)
            {
                // No finding could be extracted: the reviewer's words are the one finding, carried whole.
                section.Append("Findings:\n");
                section.Append("1. ").Append(text.Length == 0 ? "The reviewer recorded no written finding. Re-check the whole mission scope." : IndentContinuation(text)).Append('\n');
                findings = new List<string> { text };
            }
            else
            {
                section.Append("Findings:\n");
                for (int i = 0; i < findings.Count; i++)
                    section.Append(i + 1).Append(". ").Append(IndentContinuation(findings[i])).Append('\n');
                if (selection.Omitted > 0)
                {
                    section.Append("(").Append(selection.Omitted)
                        .Append(selection.Omitted == 1 ? " lower-priority finding was" : " lower-priority findings were")
                        .Append(" left out whole to fit the bound; it was not cut. ")
                        .Append(String.IsNullOrWhiteSpace(completeReviewPointer) ? "Read the complete review before you finish." : completeReviewPointer!.Trim())
                        .Append(")\n");
                }
            }

            section.Append('\n').Append(BuildDemands(findings.Count)).Append('\n');
            return section.ToString().TrimEnd();
        }

        /// <summary>
        /// Build the numbered demands: one per finding, then the fixed rework demands, then the output contract
        /// last. With no numbered finding the first demand points at the reviewer feedback as a whole.
        /// </summary>
        /// <param name="findingCount">Number of numbered findings the captain was given.</param>
        /// <returns>The demands block, one demand per line, without a trailing newline.</returns>
        public static string BuildDemands(int findingCount)
        {
            StringBuilder demands = new StringBuilder("Demands:\n");
            int number = 1;
            if (findingCount <= 0)
                demands.Append(number++).Append(". Address the reviewer feedback above, or reject it with evidence from the code.\n");
            for (int i = 0; i < findingCount; i++)
                demands.Append(number++).Append(". Finding ").Append(i + 1)
                    .Append(": change the code so it no longer applies, or reject it with evidence from the code.\n");
            demands.Append(number++).Append(". Keep the work already on the branch. Do not start over.\n");
            demands.Append(number++).Append(". Change only what the findings require.\n");
            demands.Append(number++).Append(". Run the vessel's checks again after the fixes.\n");
            demands.Append(number++).Append(". Commit the fixes on the existing branch.\n");
            demands.Append(number).Append(". ").Append(OutputContract);
            return demands.ToString();
        }

        #endregion

        #region Private-Methods

        // A finding that spans lines keeps its later lines under its number.
        private static string IndentContinuation(string finding)
        {
            return finding.Trim().Replace("\r\n", "\n").Replace("\n", "\n   ");
        }

        #endregion
    }
}
