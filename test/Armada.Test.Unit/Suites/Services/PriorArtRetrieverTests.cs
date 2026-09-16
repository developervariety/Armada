namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for the deterministic D26 prior-art retriever and its identifier extractor. The retriever
    /// is fed a fixture source, so these assert a property of the input, not of a live tree: what terms
    /// are mined, that each surface becomes a candidate with the right <c>where</c>, that hits on one
    /// location de-duplicate into one candidate carrying both terms, that a dead surface is skipped, and
    /// that the twelve-candidate and token caps hold and flag truncation.
    /// </summary>
    public class PriorArtRetrieverTests : TestSuite
    {
        public override string Name => "Prior Art Retriever (D26)";

        private static PriorArtQuery Query(string title, string description, params string[] criteria)
        {
            return new PriorArtQuery
            {
                Context = new PriorArtSearchContext { VesselId = "vsl_x", RepoPath = "/repo", TargetRef = "main", DefaultBranch = "main" },
                Title = title,
                Description = description,
                AcceptanceCriteria = new List<string>(criteria)
            };
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Extract_MinesTypeMethodAndFileNames_DropsStopwords", () =>
            {
                IReadOnlyList<string> terms = PriorArtIdentifierExtractor.Extract(
                    "Implement PriorArtRetriever in PriorArtRetriever.cs",
                    "The should always compute VolvoMackKeySelector from the tables.");

                AssertTrue(terms.Contains("PriorArtRetriever"), "a Pascal-case type name is a term");
                AssertTrue(terms.Contains("PriorArtRetriever.cs"), "a file path is a term");
                AssertTrue(terms.Contains("VolvoMackKeySelector"), "a cased identifier is a term");
                AssertTrue(!terms.Contains("should"), "a common stopword is dropped");
                AssertTrue(!terms.Contains("always"), "a common stopword is dropped");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Extract_StrongSignalsOrderedFirst", () =>
            {
                IReadOnlyList<string> terms = PriorArtIdentifierExtractor.Extract("handle DecodeScaled inside Program.cs quickly");
                int fileIndex = terms.ToList().IndexOf("Program.cs");
                int wordIndex = terms.ToList().IndexOf("quickly");
                AssertTrue(fileIndex >= 0, "the file path is mined");
                AssertTrue(wordIndex < 0 || fileIndex < wordIndex, "strong signals precede plain long words");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("NoTerms_ReturnsEmpty_NoSourceCall", async () =>
            {
                FakePriorArtSource source = new FakePriorArtSource().With(PriorArtWhereEnum.Landed,
                    FakePriorArtSource.Hit(PriorArtWhereEnum.Landed, "a.cs:1", "x"));
                PriorArtRetriever retriever = new PriorArtRetriever(source);

                // Only stopwords and short words: nothing to grep, so no candidate and no surface call.
                PriorArtRetrieval retrieval = await retriever.RetrieveAsync(Query("do it", "the work is done"), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, retrieval.Candidates.Count);
                AssertTrue(!retrieval.HasCandidates, "no candidates found");
                AssertEqual(0, source.LastTerms.Count);
            }).ConfigureAwait(false);

            await RunTest("EachSurface_BecomesCandidate_WithItsWhere", async () =>
            {
                FakePriorArtSource source = new FakePriorArtSource()
                    .With(PriorArtWhereEnum.Landed, FakePriorArtSource.Hit(PriorArtWhereEnum.Landed, "src/Landed.cs:10", "PriorArtRetriever"))
                    .With(PriorArtWhereEnum.UnlandedBranch, FakePriorArtSource.Hit(PriorArtWhereEnum.UnlandedBranch, "src/Branch.cs:5", "PriorArtRetriever", refName: "msn_1"))
                    .With(PriorArtWhereEnum.RecoverRef, FakePriorArtSource.Hit(PriorArtWhereEnum.RecoverRef, "src/Rec.cs:3", "PriorArtRetriever", refName: "recover/x"))
                    .With(PriorArtWhereEnum.OpenObjective, FakePriorArtSource.Hit(PriorArtWhereEnum.OpenObjective, "obj_other", "PriorArtRetriever"));
                PriorArtRetriever retriever = new PriorArtRetriever(source);

                PriorArtRetrieval retrieval = await retriever.RetrieveAsync(Query("Add PriorArtRetriever", "Add PriorArtRetriever"), CancellationToken.None).ConfigureAwait(false);

                List<string> labels = retrieval.Candidates.Select(c => c.WhereLabel).ToList();
                AssertTrue(labels.Contains("landed"), "landed candidate present");
                AssertTrue(labels.Contains("unlanded_branch"), "unlanded branch candidate present");
                AssertTrue(labels.Contains("recover_ref"), "recover ref candidate present");
                AssertTrue(labels.Contains("open_objective"), "open objective candidate present");
            }).ConfigureAwait(false);

            await RunTest("DuplicateLocation_CollapsesToOneCandidate_WithBothTerms", async () =>
            {
                FakePriorArtSource source = new FakePriorArtSource().With(PriorArtWhereEnum.Landed,
                    FakePriorArtSource.Hit(PriorArtWhereEnum.Landed, "src/Same.cs:10", "PriorArtRetriever"),
                    FakePriorArtSource.Hit(PriorArtWhereEnum.Landed, "src/Same.cs:10", "VolvoMackKeySelector"));
                PriorArtRetriever retriever = new PriorArtRetriever(source);

                PriorArtRetrieval retrieval = await retriever.RetrieveAsync(
                    Query("Add PriorArtRetriever and VolvoMackKeySelector", "x"), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, retrieval.Candidates.Count);
                AssertEqual(2, retrieval.Candidates[0].Terms.Count);
            }).ConfigureAwait(false);

            await RunTest("DeadSurface_IsSkipped_OthersStillReturn", async () =>
            {
                FakePriorArtSource source = new FakePriorArtSource()
                    .Throwing(PriorArtWhereEnum.Landed)
                    .With(PriorArtWhereEnum.UnlandedBranch, FakePriorArtSource.Hit(PriorArtWhereEnum.UnlandedBranch, "src/B.cs:1", "PriorArtRetriever", refName: "msn_1"));
                PriorArtRetriever retriever = new PriorArtRetriever(source);

                PriorArtRetrieval retrieval = await retriever.RetrieveAsync(Query("Add PriorArtRetriever", "x"), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, retrieval.Candidates.Count);
                AssertEqual("unlanded_branch", retrieval.Candidates[0].WhereLabel);
            }).ConfigureAwait(false);

            await RunTest("TwelveCandidateCap_Truncates", async () =>
            {
                List<PriorArtHit> many = new List<PriorArtHit>();
                for (int i = 0; i < 20; i++)
                    many.Add(FakePriorArtSource.Hit(PriorArtWhereEnum.Landed, "src/File.cs:" + i, "PriorArtRetriever"));
                FakePriorArtSource source = new FakePriorArtSource().With(PriorArtWhereEnum.Landed, many.ToArray());
                PriorArtRetriever retriever = new PriorArtRetriever(source);

                PriorArtRetrieval retrieval = await retriever.RetrieveAsync(Query("Add PriorArtRetriever", "x"), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(12, retrieval.Candidates.Count);
                AssertTrue(retrieval.Truncated, "the candidate set is flagged truncated");
            }).ConfigureAwait(false);

            await RunTest("TokenCap_Truncates_BeforeTwelve", async () =>
            {
                // Two candidates whose excerpts each cost more than half the token budget: the first is
                // admitted (a lone candidate is always kept), and the second pushes the running total past
                // the budget and truncates the set. 60000 chars is ~15000 tokens, so two exceed 24000.
                StringBuilder big = new StringBuilder();
                for (int i = 0; i < 60000; i++) big.Append('x');
                FakePriorArtSource source = new FakePriorArtSource().With(PriorArtWhereEnum.Landed,
                    FakePriorArtSource.Hit(PriorArtWhereEnum.Landed, "src/One.cs:1", "PriorArtRetriever", excerpt: big.ToString()),
                    FakePriorArtSource.Hit(PriorArtWhereEnum.Landed, "src/Two.cs:1", "PriorArtRetriever", excerpt: big.ToString()));
                PriorArtRetriever retriever = new PriorArtRetriever(source);

                PriorArtRetrieval retrieval = await retriever.RetrieveAsync(Query("Add PriorArtRetriever", "x"), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, retrieval.Candidates.Count);
                AssertTrue(retrieval.Truncated, "the token budget truncates the set");
            }).ConfigureAwait(false);

            await RunTest("Excerpt_TrimmedToFortyLines", async () =>
            {
                StringBuilder fifty = new StringBuilder();
                for (int i = 0; i < 50; i++) fifty.Append("line").Append(i).Append('\n');
                FakePriorArtSource source = new FakePriorArtSource().With(PriorArtWhereEnum.Landed,
                    FakePriorArtSource.Hit(PriorArtWhereEnum.Landed, "src/Long.cs:1", "PriorArtRetriever", excerpt: fifty.ToString()));
                PriorArtRetriever retriever = new PriorArtRetriever(source);

                PriorArtRetrieval retrieval = await retriever.RetrieveAsync(Query("Add PriorArtRetriever", "x"), CancellationToken.None).ConfigureAwait(false);

                int lines = retrieval.Candidates[0].Excerpt.Split('\n').Length;
                AssertTrue(lines <= 40, "the excerpt is trimmed to at most forty lines");
            }).ConfigureAwait(false);
        }
    }
}
