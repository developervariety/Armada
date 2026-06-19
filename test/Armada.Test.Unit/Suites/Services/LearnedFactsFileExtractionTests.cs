namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Memory;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for extracting learned-fact proposal blocks from captain output.
    /// </summary>
    public class LearnedFactsFileExtractionTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Learned Facts File Extraction";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("ExtractProposals_InlineMarker_ReturnsProposal", () =>
            {
                List<string> proposals = LearnedFactsFile.ExtractProposals(
                    "done\n[LEARNED-FACT-PROPOSAL] [high] Use the custom TestSuite harness, not xUnit.");

                AssertEqual(1, proposals.Count, "Inline proposal count");
                AssertEqual("[high] Use the custom TestSuite harness, not xUnit.", proposals[0], "Inline proposal body");
                return Task.CompletedTask;
            });

            await RunTest("ExtractProposals_BlockMarker_ReturnsContiguousBlock", () =>
            {
                List<string> proposals = LearnedFactsFile.ExtractProposals(
                    "notes\n[LEARNED-FACT-PROPOSAL]\n[medium] Build with dotnet build src/Armada.sln.\nKeep tests in the custom harness.\n\nnot part of proposal");

                AssertEqual(1, proposals.Count, "Block proposal count");
                AssertContains("[medium] Build with dotnet build src/Armada.sln.", proposals[0], "Block should include first line");
                AssertContains("Keep tests in the custom harness.", proposals[0], "Block should include second line");
                AssertFalse(proposals[0].Contains("not part of proposal"), "Blank line should terminate block proposal");
                return Task.CompletedTask;
            });

            await RunTest("ExtractProposals_WhitespaceOnlyMarker_OmitsProposal", () =>
            {
                List<string> proposals = LearnedFactsFile.ExtractProposals(
                    "[LEARNED-FACT-PROPOSAL]   \n\nordinary final answer");

                AssertEqual(0, proposals.Count, "Whitespace-only proposal should be ignored");
                return Task.CompletedTask;
            });
        }
    }
}
