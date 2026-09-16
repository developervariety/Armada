namespace Armada.Test.Unit.Suites.Context
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Context;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the context retrieval service: the core is always returned first and never
    /// budget-limited; a must_retrieve leaf is included for a matching domain and excluded for a
    /// non-matching one; applies_to filters leaves by persona and vessel while <c>all</c> is always
    /// eligible; the leaf budget caps only the ranked leaves; the order is deterministic; and any
    /// error fails safe to every core chunk plus a conservative leaf superset without throwing. Every
    /// test builds its own small chunk set, so the suite never depends on the live AI-Memory content.
    /// </summary>
    public sealed class ContextRetrievalServiceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "ContextRetrievalService";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Core_AlwaysReturned_First_AndBudgetExempt", () =>
            {
                ContextRetrievalService service = new ContextRetrievalService(SampleChunks());

                // A zero leaf budget must still return every core chunk.
                ContextRetrievalResult result = service.Retrieve(new ContextRetrievalRequest
                {
                    Query = "nothing matches this at all zzz",
                    MaxLeafBytes = 0
                });

                List<string> coreTopics = result.Core.Select(c => c.Topic).ToList();
                AssertTrue(coreTopics.Contains("core.a"), "core.a is core");
                AssertTrue(coreTopics.Contains("core.b"), "core.b is core");
                AssertEqual(2, result.Core.Count, "exactly the two core chunks");
                AssertTrue(result.Core.All(c => c.Tier == ContextTierEnum.Core), "every Core entry is tier core");

                // Zero budget means zero ranked leaves, but the core is unaffected.
                AssertEqual(0, result.Leaves.Count, "no ranked leaves fit a zero budget");
                AssertFalse(result.Degraded, "a normal request is not degraded");
                return Task.CompletedTask;
            });

            await RunTest("MustRetrieve_IncludedForMatchingDomain_ExcludedForNonMatching_AndBudgetExempt", () =>
            {
                ContextRetrievalService service = new ContextRetrievalService(SampleChunks());

                // The vessel matches: the must_retrieve safety leaf is included even though its bytes
                // exceed the zero leaf budget (must_retrieve is budget-exempt).
                ContextRetrievalResult matched = service.Retrieve(new ContextRetrievalRequest
                {
                    Vessel = "ExampleVessel",
                    MaxLeafBytes = 0
                });
                AssertTrue(matched.MustRetrieve.Any(c => c.Topic == "leaf.safety.example"),
                    "the ExampleVessel safety leaf is force-included");
                AssertFalse(matched.Leaves.Any(c => c.Topic == "leaf.safety.example"),
                    "a force-included leaf is not double-counted in the ranked leaves");

                // A different vessel: the safety leaf's domain does not match, so it is not in the
                // must_retrieve set.
                ContextRetrievalResult other = service.Retrieve(new ContextRetrievalRequest
                {
                    Vessel = "OtherVessel",
                    MaxLeafBytes = 0
                });
                AssertFalse(other.MustRetrieve.Any(c => c.Topic == "leaf.safety.example"),
                    "the safety leaf is excluded for a non-matching vessel");
                return Task.CompletedTask;
            });

            await RunTest("AppliesTo_PersonaFiltering_ExcludesOtherPersona_IncludesAll", () =>
            {
                ContextRetrievalService service = new ContextRetrievalService(SampleChunks());

                // A Judge request: the Judge leaf is eligible; the Worker leaf is not; the all leaf is.
                ContextRetrievalResult judge = service.Retrieve(new ContextRetrievalRequest
                {
                    RequestingPersona = "Judge",
                    Query = "review proof judge worker general",
                    MaxLeafBytes = 100000
                });
                List<string> topics = judge.Leaves.Select(c => c.Topic).ToList();
                AssertTrue(topics.Contains("leaf.judge.review"), "Judge leaf eligible for a Judge request");
                AssertFalse(topics.Contains("leaf.worker.fidelity"), "Worker leaf excluded for a Judge request");
                AssertTrue(topics.Contains("leaf.general"), "an all leaf is always eligible");
                return Task.CompletedTask;
            });

            await RunTest("Budget_CapsRankedLeaves_Only", () =>
            {
                ContextRetrievalService service = new ContextRetrievalService(SampleChunks());

                // A generous budget returns both plain leaves for the query.
                ContextRetrievalResult wide = service.Retrieve(new ContextRetrievalRequest
                {
                    Query = "alpha beta general review",
                    RequestingPersona = "Judge",
                    MaxLeafBytes = 100000
                });
                int wideLeafCount = wide.Leaves.Count;
                AssertTrue(wideLeafCount >= 2, "a wide budget returns several leaves");

                // A tight budget that fits only the first ranked leaf.
                int firstLeafBytes = wide.Leaves.First().Bytes;
                ContextRetrievalResult tight = service.Retrieve(new ContextRetrievalRequest
                {
                    Query = "alpha beta general review",
                    RequestingPersona = "Judge",
                    MaxLeafBytes = firstLeafBytes
                });
                AssertEqual(1, tight.Leaves.Count, "only the top-ranked leaf fits a one-leaf budget");
                AssertTrue(tight.LeafBytes <= firstLeafBytes, "leaf bytes stay within the budget");
                // The core is unaffected by the leaf budget.
                AssertEqual(wide.Core.Count, tight.Core.Count, "the core is the same under any budget");
                return Task.CompletedTask;
            });

            await RunTest("Ordering_IsDeterministic_AcrossRuns", () =>
            {
                ContextRetrievalService a = new ContextRetrievalService(SampleChunks());
                ContextRetrievalService b = new ContextRetrievalService(SampleChunks());

                ContextRetrievalRequest request = new ContextRetrievalRequest
                {
                    Query = "review general alpha proof",
                    RequestingPersona = "Judge",
                    Vessel = "ExampleVessel",
                    MaxLeafBytes = 100000
                };

                string first = Signature(a.Retrieve(request));
                string second = Signature(b.Retrieve(request));
                string third = Signature(a.Retrieve(request));
                AssertEqual(first, second, "two services give the same order");
                AssertEqual(first, third, "two runs give the same order");
                return Task.CompletedTask;
            });

            await RunTest("FailSafe_OnRankerError_ReturnsCoreAndSuperset_NeverZeroCore_NeverThrows", () =>
            {
                // A ranker that throws forces the fail-safe path. The service must not throw, must
                // return every core chunk, must mark degraded, and must return a conservative leaf
                // superset (every eligible leaf, budget ignored).
                ContextRetrievalService service = new ContextRetrievalService(SampleChunks(), new ThrowingRanker());

                ContextRetrievalResult result = service.Retrieve(new ContextRetrievalRequest
                {
                    Query = "anything",
                    MaxLeafBytes = 0 // even a zero budget must not empty the fail-safe leaves
                });

                AssertTrue(result.Degraded, "a ranker error degrades the result");
                AssertNotNull(result.Note, "a degraded result names its reason");
                AssertEqual(2, result.Core.Count, "the fail-safe returns every core chunk");
                AssertTrue(result.Core.Count > 0, "never zero core");

                // Superset: the must_retrieve safety leaf plus every applies_to-eligible plain leaf.
                // An unscoped request excludes persona-restricted leaves, so the Judge and Worker
                // leaves are not eligible; the general leaf and the two alpha/beta leaves are.
                List<string> leafTopics = result.MustRetrieve.Select(c => c.Topic)
                    .Concat(result.Leaves.Select(c => c.Topic)).ToList();
                AssertTrue(leafTopics.Contains("leaf.safety.example"), "fail-safe includes every must_retrieve leaf");
                AssertTrue(leafTopics.Contains("leaf.general"), "fail-safe includes the general leaf despite zero budget");
                AssertTrue(result.Leaves.Count > 0, "fail-safe over-includes leaves regardless of budget");
                return Task.CompletedTask;
            });

            await RunTest("NullRequest_FailsSafe_NeverThrows", () =>
            {
                ContextRetrievalService service = new ContextRetrievalService(SampleChunks());
                ContextRetrievalResult result = service.Retrieve(null!);
                AssertTrue(result.Degraded, "a null request degrades");
                AssertEqual(2, result.Core.Count, "a null request still returns core");
                return Task.CompletedTask;
            });
        }

        // A deterministic signature of a result: the ordered topic ids of every returned chunk.
        private static string Signature(ContextRetrievalResult result)
        {
            IEnumerable<string> topics = result.Core.Select(c => "C:" + c.Topic)
                .Concat(result.MustRetrieve.Select(c => "M:" + c.Topic))
                .Concat(result.Leaves.Select(c => "L:" + c.Topic));
            return String.Join("|", topics);
        }

        private static List<ContextChunk> SampleChunks()
        {
            return new List<ContextChunk>
            {
                Core("core.b", 2, "Second core rule."),
                Core("core.a", 1, "First core rule."),
                Leaf("leaf.general", new List<string> { "all" }, null,
                    "General leaf about alpha and beta and review."),
                Leaf("leaf.judge.review", new List<string> { "persona:Judge" }, null,
                    "Judge review and proof leaf, general."),
                Leaf("leaf.worker.fidelity", new List<string> { "persona:Worker" }, null,
                    "Worker fidelity leaf."),
                Leaf("leaf.alpha", new List<string> { "all" }, null,
                    "Alpha leaf mentioning alpha and review."),
                Leaf("leaf.beta", new List<string> { "all" }, null,
                    "Beta leaf mentioning beta and general."),
                Leaf("leaf.safety.example", new List<string> { "all" }, new List<string> { "vessel:ExampleVessel" },
                    LongBody("Safety leaf for ExampleVessel. ")),
            };
        }

        private static ContextChunk Core(string topic, int order, string body)
        {
            return new ContextChunk
            {
                Topic = topic,
                Path = "AI-Memory/shared/" + topic + ".md",
                Summary = body,
                ReadWhen = "Always. Core rule; never retrieval-gated.",
                AppliesTo = new List<string> { "all" },
                Tier = ContextTierEnum.Core,
                MustRetrieve = new List<string>(),
                Text = body,
                CoreOrder = order
            };
        }

        private static ContextChunk Leaf(string topic, List<string> appliesTo, List<string>? mustRetrieve, string body)
        {
            return new ContextChunk
            {
                Topic = topic,
                Path = "AI-Memory/repos/" + topic + ".md",
                Summary = body,
                ReadWhen = "When the task needs " + topic + ".",
                AppliesTo = appliesTo,
                Tier = ContextTierEnum.Leaf,
                MustRetrieve = mustRetrieve ?? new List<string>(),
                Text = body,
                CoreOrder = int.MaxValue
            };
        }

        // A body large enough to exceed a zero leaf budget, proving must_retrieve is budget-exempt.
        private static string LongBody(string seed)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < 40; i++) sb.Append(seed);
            return sb.ToString();
        }

        // A ranker that always throws, to force the service's fail-safe path.
        private sealed class ThrowingRanker : IContextLeafRanker
        {
            public IReadOnlyList<ContextChunk> Rank(ContextRetrievalRequest request, IReadOnlyList<ContextChunk> eligibleLeaves)
            {
                throw new InvalidOperationException("forced ranker failure");
            }
        }
    }
}
