namespace Armada.Test.Unit.Suites.Services
{
    using System.Text;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>Tests for the numbered rework section a captain receives when its work is sent back.</summary>
    public sealed class ReworkPromptBuilderTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Rework Prompt Builder";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Build numbers every Judge finding and gives each one demand, with the output contract last", () =>
            {
                string review = String.Join("\n", new[]
                {
                    "## Correctness",
                    "- **Blocking.** The retry loop never stops after a cancelled token.",
                    "- **Blocking.** The parser accepts a negative length.",
                    "## Verdict",
                    "[ARMADA:VERDICT] NEEDS_REVISION"
                });

                string section = ReworkPromptBuilder.Build(review);

                AssertContains("Findings:\n1. **Blocking.** The retry loop never stops after a cancelled token.\n2. **Blocking.** The parser accepts a negative length.\n", section, "Findings are numbered in review order.");
                AssertContains("1. Finding 1: ", section, "Demand 1 answers finding 1.");
                AssertContains("2. Finding 2: ", section, "Demand 2 answers finding 2.");
                AssertFalse(section.Contains("Finding 3:", StringComparison.Ordinal), "No demand exists without a finding.");
                AssertTrue(section.EndsWith(ReworkPromptBuilder.OutputContract, StringComparison.Ordinal), "The output contract closes the section.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Build carries a free-form reviewer comment whole as finding 1", () =>
            {
                string section = ReworkPromptBuilder.Build("Add tests before continuing.\nCover the timeout path too.");

                AssertContains("1. Add tests before continuing.\n   Cover the timeout path too.\n", section, "The comment is finding 1 with its second line kept.");
                AssertContains("1. Finding 1: ", section, "The one finding has one demand.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Build drops whole findings over the bound and states how many", () =>
            {
                StringBuilder review = new StringBuilder("## Correctness\n");
                for (int i = 0; i < 10; i++)
                    review.Append("- **Blocking.** Finding ").Append(i).Append(" is here. ").Append(new string('y', 500)).Append(" Finding ").Append(i).Append(" ends.\n");

                string section = ReworkPromptBuilder.Build(review.ToString(), 2000);

                int kept = 0;
                while (kept < 10 && section.Contains("Finding " + kept + " ends.", StringComparison.Ordinal)) kept++;
                AssertTrue(kept > 0 && kept < 10, "Some findings fit. Kept: " + kept);
                for (int i = kept; i < 10; i++)
                    AssertFalse(section.Contains("Finding " + i + " is here.", StringComparison.Ordinal), "A finding that does not fit is dropped whole.");
                AssertContains("(" + (10 - kept) + " lower-priority findings were left out whole", section, "The section states how many findings it left out.");
                string demands = section.Substring(section.IndexOf("Demands:", StringComparison.Ordinal));
                AssertContains(kept + ". Finding " + kept + ": ", demands, "Demands cover the last kept finding.");
                AssertFalse(demands.Contains("Finding " + (kept + 1) + ": ", StringComparison.Ordinal), "No demand exists for a dropped finding.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
