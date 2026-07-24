namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Regression coverage for the armada_dispatch indefinite-hang bug (2026-07-23).
    ///
    /// Root cause: the dispatch-time code-context build used the ENTIRE mission title+description as
    /// the lexical search Goal. A large structured brief (~3.6 KB) exploded SplitQueryTerms into
    /// hundreds of distinct terms and drove an O(records x terms x contentLength) scan -- plus a
    /// whole-brief content.Contains(query) needle per record -- so lexical scoring cost scaled with
    /// brief size. A tiny brief scored in microseconds; a large brief pegged CPU (and could starve the
    /// thread pool so the Task.WhenAny ceiling never fired), hanging dispatch with no voyage created.
    ///
    /// Fix: cap the distinct-term set (SplitQueryTerms) and gate the whole-query needle behind a length
    /// limit (ComputeLexicalScore) so lexical scan cost is independent of brief size. These tests prove
    /// the cost bound directly and deterministically -- no network, no sleep.
    /// </summary>
    public class DispatchCodeContextBoundedTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Dispatch Code Context Bounded";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("SplitQueryTerms_LargeBrief_CapsDistinctTerms_IndependentOfBriefSize", () =>
            {
                // A multi-KB structured brief would otherwise yield hundreds of distinct terms; the cap
                // makes the term set (and thus lexical scan cost) independent of brief size.
                string largeBrief = String.Join(" ", Enumerable.Range(0, 5000).Select(i => "termnumber" + i));
                string smallBrief = "fix the seed key unlock regression in the detroit adapter";

                string[] largeTerms = CodeIndexService.SplitQueryTerms(largeBrief);
                string[] smallTerms = CodeIndexService.SplitQueryTerms(smallBrief);

                AssertEqual(CodeIndexService._MaxQueryTerms, largeTerms.Length,
                    "a large brief must clamp to exactly the term cap");
                AssertTrue(largeTerms.Length <= CodeIndexService._MaxQueryTerms,
                    "term count must never exceed the cap");
                AssertTrue(smallTerms.Length <= CodeIndexService._MaxQueryTerms,
                    "a small brief stays under the cap");
                AssertTrue(smallTerms.Length < largeTerms.Length,
                    "the cap only truncates oversized briefs; a small brief is unaffected");
                return Task.CompletedTask;
            });

            await RunTest("ComputeLexicalScore_OversizedQuery_SkipsWholeQueryNeedle_StaysBounded", () =>
            {
                // content long enough to contain both needles as contiguous substrings.
                CodeIndexRecord record = new CodeIndexRecord { Path = "z.cs", Content = new string('a', 400) };

                // query <= _MaxQueryContainsLength: the whole-query content.Contains bonus (+8) applies.
                string shortQuery = new string('a', 200);
                double shortScore = CodeIndexService.ComputeLexicalScore(record, shortQuery, CodeIndexService.SplitQueryTerms(shortQuery));

                // query > _MaxQueryContainsLength: the O(contentLength x queryLength) needle is gated
                // off, so the +8 whole-query bonus is NOT applied -- scoring degrades to per-term counts.
                string longQuery = new string('a', 300);
                double longScore = CodeIndexService.ComputeLexicalScore(record, longQuery, CodeIndexService.SplitQueryTerms(longQuery));

                AssertTrue(shortScore >= 8.0,
                    "a short query that matches content must include the whole-query needle bonus, got " + shortScore);
                AssertTrue(longScore < 8.0,
                    "an oversized query must skip the whole-query needle, got " + longScore);

                // Scan-cost sanity: scoring a large brief across many records must stay bounded (no
                // pathological blow-up). This simulated scan is what stalled dispatch before the caps.
                string bigBrief = String.Join(" ", Enumerable.Range(0, 4000).Select(i => "keyword" + i));
                string[] cappedTerms = CodeIndexService.SplitQueryTerms(bigBrief);
                CodeIndexRecord bigRecord = new CodeIndexRecord { Path = "big.cs", Content = new string('x', 50_000) };
                Stopwatch watch = Stopwatch.StartNew();
                for (int i = 0; i < 2000; i++)
                    CodeIndexService.ComputeLexicalScore(bigRecord, bigBrief, cappedTerms);
                watch.Stop();

                AssertTrue(watch.Elapsed < TimeSpan.FromSeconds(5),
                    "capped lexical scan over 2000 records must stay bounded, took " + watch.Elapsed.TotalSeconds.ToString("F1") + "s");
                return Task.CompletedTask;
            });
        }
    }
}
