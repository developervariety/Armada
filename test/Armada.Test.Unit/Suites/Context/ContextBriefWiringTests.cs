namespace Armada.Test.Unit.Suites.Context
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Context;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for wiring context retrieval into captain-brief generation behind the
    /// <c>contextRetrieval.briefSlimmingEnabled</c> flag, through the
    /// <see cref="MissionService.BuildMemorySection"/> seam.
    ///
    /// The contract proved here: with the flag OFF the section is byte-for-byte the full
    /// read-every-file memory section (non-breaking); with the flag ON it carries the always-on core
    /// plus the mission's must-retrieve safety leaves and a fetch-context pointer, and drops the
    /// "read every file under shared/" instruction; and with the flag ON but retrieval unavailable or
    /// degraded, it falls back to the full section (never empty, never fewer rules) and records the
    /// fallback on its telemetry.
    /// </summary>
    public sealed class ContextBriefWiringTests : TestSuite
    {
        private const string ReadEveryFilePhrase = "Read every file under";

        /// <summary>Suite name.</summary>
        public override string Name => "ContextBriefWiring";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("FlagOff_MemorySection_IsByteForByteTheFullSection", () =>
            {
                ContextRetrievalService service = new ContextRetrievalService(SampleChunks());

                // With a resolved repo folder.
                string off = MissionService.BuildMemorySection(
                    slimmingEnabled: false, retrieval: service,
                    memoryRoot: "/memory-root", repoFolder: "examplevessel", unresolvedReason: null,
                    persona: "Worker", vesselName: "ExampleVessel", query: "some task",
                    leafBudgetBytes: 24000, fetchToolEnabled: true, logging: null,
                    telemetry: out ContextBriefTelemetry? t1);
                string full = MissionService.BuildAiMemorySection("/memory-root", "examplevessel", null);
                AssertEqual(full, off, "flag off returns exactly the full section (repo folder)");
                AssertNull(t1, "flag off produces no slimming telemetry");

                // With no repo folder and an unresolved reason.
                string off2 = MissionService.BuildMemorySection(
                    slimmingEnabled: false, retrieval: service,
                    memoryRoot: "/memory-root", repoFolder: null, unresolvedReason: "the root cannot be probed",
                    persona: null, vesselName: null, query: "",
                    leafBudgetBytes: 0, fetchToolEnabled: false, logging: null,
                    telemetry: out ContextBriefTelemetry? t2);
                string full2 = MissionService.BuildAiMemorySection("/memory-root", null, "the root cannot be probed");
                AssertEqual(full2, off2, "flag off returns exactly the full section (no folder)");
                AssertNull(t2, "flag off produces no slimming telemetry (no folder)");
                return Task.CompletedTask;
            });

            await RunTest("FlagOn_SlimmedSection_CarriesCorePlusSafetyLeaf_AndFetchPointer_AndDropsReadEveryFile", () =>
            {
                ContextRetrievalService service = new ContextRetrievalService(SampleChunks());

                string on = MissionService.BuildMemorySection(
                    slimmingEnabled: true, retrieval: service,
                    memoryRoot: "/memory-root", repoFolder: "examplevessel", unresolvedReason: null,
                    persona: "Worker", vesselName: "ExampleVessel", query: "alpha review task",
                    leafBudgetBytes: 24000, fetchToolEnabled: true, logging: null,
                    telemetry: out ContextBriefTelemetry? t);

                AssertNotNull(t, "flag on produces slimming telemetry");
                AssertTrue(t!.Enabled, "telemetry marks the flag enabled");
                AssertFalse(t.FellBack, "a healthy retrieval does not fall back");

                // Core rules are present (both core chunks).
                AssertContains("core.a", on, "core rule a is inline");
                AssertContains("First core rule.", on, "core rule a body is inline");
                AssertContains("core.b", on, "core rule b is inline");
                AssertEqual(2, t.CoreCount, "two core chunks accounted");

                // The vessel's must-retrieve safety leaf ships and is labelled.
                AssertContains("leaf.safety.example", on, "the vessel safety leaf is inline");
                AssertContains("safety: always included", on, "the safety leaf is labelled");
                AssertTrue(t.MustRetrieveCount >= 1, "at least one must-retrieve safety leaf accounted");

                // The on-demand fetch pointer is present.
                AssertContains("armada_fetch_context", on, "the fetch-context pointer is present");

                // The whole point: it does NOT tell the captain to read every file under shared/.
                AssertFalse(on.Contains(ReadEveryFilePhrase), "slimmed section drops the read-every-file instruction");

                // The authority rule survives (reference material, not authority).
                AssertContains("win on conflict", on, "the authority rule survives in the slimmed section");
                return Task.CompletedTask;
            });

            await RunTest("FlagOn_NoBuiltIndex_FallsBackToFullSection", () =>
            {
                string on = MissionService.BuildMemorySection(
                    slimmingEnabled: true, retrieval: null,
                    memoryRoot: "/memory-root", repoFolder: "examplevessel", unresolvedReason: null,
                    persona: "Worker", vesselName: "ExampleVessel", query: "task",
                    leafBudgetBytes: 24000, fetchToolEnabled: true, logging: null,
                    telemetry: out ContextBriefTelemetry? t);

                string full = MissionService.BuildAiMemorySection("/memory-root", "examplevessel", null);
                AssertEqual(full, on, "no built index falls back to exactly the full section");
                AssertNotNull(t, "fallback still produces telemetry");
                AssertTrue(t!.FellBack, "telemetry marks the fallback");
                AssertNotNull(t.FallbackReason, "the fallback names a reason");
                AssertTrue(on.Contains(ReadEveryFilePhrase), "the fallback section is the full read-every-file section");
                AssertTrue(t.SectionBytes > 0, "the fallback section is not empty");
                return Task.CompletedTask;
            });

            await RunTest("FlagOn_ForcedRetrievalFailure_DegradesToFullSection_NeverEmpty", () =>
            {
                // A ranker that always throws forces the retrieval service down its degraded fail-safe
                // path. The slimming seam must then fall back to the full section, never ship fewer
                // rules, and record the degradation.
                ContextRetrievalService service = new ContextRetrievalService(SampleChunks(), new ThrowingRanker());

                string on = MissionService.BuildMemorySection(
                    slimmingEnabled: true, retrieval: service,
                    memoryRoot: "/memory-root", repoFolder: "examplevessel", unresolvedReason: null,
                    persona: "Worker", vesselName: "ExampleVessel", query: "task",
                    leafBudgetBytes: 24000, fetchToolEnabled: true, logging: null,
                    telemetry: out ContextBriefTelemetry? t);

                string full = MissionService.BuildAiMemorySection("/memory-root", "examplevessel", null);
                AssertEqual(full, on, "a degraded retrieval falls back to exactly the full section");
                AssertNotNull(t, "degraded fallback still produces telemetry");
                AssertTrue(t!.FellBack, "telemetry marks the degraded fallback");
                AssertTrue(t.Degraded, "telemetry marks the result degraded");
                AssertTrue(on.Contains(ReadEveryFilePhrase), "the degraded fallback is the full section");
                AssertTrue(on.Length > 0, "the section is never empty");
                return Task.CompletedTask;
            });

            await RunTest("BuildMemoryRetrievalQuery_JoinsTitleAndDescription_Bounded", () =>
            {
                Mission mission = new Mission { Title = "Port PGN64965", Description = "Reproduce the byte truncation defect." };
                string q = MissionService.BuildMemoryRetrievalQuery(mission);
                AssertContains("Port PGN64965", q, "query carries the title");
                AssertContains("byte truncation", q, "query carries the description");

                Mission longMission = new Mission { Title = "T", Description = new string('x', 5000) };
                string ql = MissionService.BuildMemoryRetrievalQuery(longMission);
                AssertTrue(ql.Length <= 2000, "query is bounded to 2000 chars");
                return Task.CompletedTask;
            });
        }

        private static List<ContextChunk> SampleChunks()
        {
            return new List<ContextChunk>
            {
                Core("core.b", 2, "Second core rule."),
                Core("core.a", 1, "First core rule."),
                Leaf("leaf.general", new List<string> { "all" }, null,
                    "General leaf about alpha and beta and review."),
                Leaf("leaf.alpha", new List<string> { "all" }, null,
                    "Alpha leaf mentioning alpha and review."),
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

        private static string LongBody(string seed)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < 40; i++) sb.Append(seed);
            return sb.ToString();
        }

        private sealed class ThrowingRanker : IContextLeafRanker
        {
            public IReadOnlyList<ContextChunk> Rank(ContextRetrievalRequest request, IReadOnlyList<ContextChunk> eligibleLeaves)
            {
                throw new InvalidOperationException("forced ranker failure");
            }
        }
    }
}
