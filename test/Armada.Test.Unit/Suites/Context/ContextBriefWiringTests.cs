namespace Armada.Test.Unit.Suites.Context
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Context;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for wiring context retrieval into captain-brief generation behind the
    /// <c>contextRetrieval.briefSlimmingEnabled</c> flag, through the
    /// <see cref="MissionService.BuildMemoryDeliveryAsync"/> seam.
    ///
    /// The contract proved here: with the flag OFF the section is byte-for-byte the full
    /// read-every-file memory section and no files are delivered; with the flag ON the always-on core
    /// and the mission's must-retrieve safety leaves are delivered as bounded files the section lists,
    /// with a fetch-context pointer and without the "read every file under shared/" instruction; a leaf
    /// sort moves leaves to reference files without dropping any; and with the flag ON but retrieval
    /// unavailable or degraded, it falls back to the full section (never empty, never fewer rules) and
    /// records the fallback on its telemetry.
    /// </summary>
    public sealed class ContextBriefWiringTests : TestSuite
    {
        private const string ReadEveryFilePhrase = "Read every file under";

        /// <summary>Suite name.</summary>
        public override string Name => "ContextBriefWiring";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("FlagOff_MemorySection_IsByteForByteTheFullSection", async () =>
            {
                ContextRetrievalService service = new ContextRetrievalService(SampleChunks());

                // With a resolved repo folder.
                ContextBriefDelivery off = await MissionService.BuildMemoryDeliveryAsync(
                    slimmingEnabled: false, retrieval: service,
                    memoryRoot: "/memory-root", repoFolder: "examplevessel", unresolvedReason: null,
                    persona: "Worker", vesselName: "ExampleVessel", query: "some task",
                    leafBudgetBytes: 24000, fetchToolEnabled: true, sortLeaves: null, logging: null);
                string full = MissionService.BuildAiMemorySection("/memory-root", "examplevessel", null);
                AssertEqual(full, off.Section, "flag off returns exactly the full section (repo folder)");
                AssertNull(off.Telemetry, "flag off produces no slimming telemetry");
                AssertEqual(0, off.Files.Count, "flag off delivers no memory files");

                // With no repo folder and an unresolved reason.
                ContextBriefDelivery off2 = await MissionService.BuildMemoryDeliveryAsync(
                    slimmingEnabled: false, retrieval: service,
                    memoryRoot: "/memory-root", repoFolder: null, unresolvedReason: "the root cannot be probed",
                    persona: null, vesselName: null, query: "",
                    leafBudgetBytes: 0, fetchToolEnabled: false, sortLeaves: null, logging: null);
                string full2 = MissionService.BuildAiMemorySection("/memory-root", null, "the root cannot be probed");
                AssertEqual(full2, off2.Section, "flag off returns exactly the full section (no folder)");
                AssertNull(off2.Telemetry, "flag off produces no slimming telemetry (no folder)");
            });

            await RunTest("FlagOn_DeliversCoreAndSafetyLeafAsListedFiles_AndFetchPointer_AndDropsReadEveryFile", async () =>
            {
                ContextRetrievalService service = new ContextRetrievalService(SampleChunks());

                ContextBriefDelivery on = await MissionService.BuildMemoryDeliveryAsync(
                    slimmingEnabled: true, retrieval: service,
                    memoryRoot: "/memory-root", repoFolder: "examplevessel", unresolvedReason: null,
                    persona: "Worker", vesselName: "ExampleVessel", query: "alpha review task",
                    leafBudgetBytes: 24000, fetchToolEnabled: true, sortLeaves: null, logging: null);
                ContextBriefTelemetry? t = on.Telemetry;
                string files = AllFileText(on);

                AssertNotNull(t, "flag on produces slimming telemetry");
                AssertTrue(t!.Enabled, "telemetry marks the flag enabled");
                AssertFalse(t.FellBack, "a healthy retrieval does not fall back");

                // Core rules are delivered in files, and the section does not carry their bodies.
                AssertContains("First core rule.", files, "core rule a body is delivered");
                AssertContains("Second core rule.", files, "core rule b body is delivered");
                AssertFalse(on.Section.Contains("First core rule."), "the section lists files, it does not inline rule bodies");
                AssertEqual(2, t.CoreCount, "two core chunks accounted");

                // Core rules come first, in core order.
                AssertTrue(on.Files[0].RelativePath.EndsWith("-core.md"), "the first file holds the core rules");
                AssertTrue(files.IndexOf("First core rule.") < files.IndexOf("Second core rule."), "core rules keep their order");

                // The vessel's must-retrieve safety leaf ships, labelled and read first.
                AssertContains("leaf.safety.example", files, "the vessel safety leaf is delivered");
                AssertContains("safety: always included", files, "the safety leaf is labelled");
                AssertTrue(t.MustRetrieveCount >= 1, "at least one must-retrieve safety leaf accounted");

                // Every delivered file is listed in the section, all as read-first when no sort ran.
                foreach (ContextBriefFile f in on.Files)
                {
                    AssertContains("`" + f.RelativePath + "`", on.Section, "the section lists " + f.RelativePath);
                    AssertTrue(f.ReadFirst, f.RelativePath + " is read-first without a sort");
                    AssertTrue(f.RelativePath.StartsWith(ContextBriefRenderer.MemoryFolder + "/"), "files live under the memory folder");
                }
                AssertEqual(on.Files.Count, t.FileCount, "telemetry counts the delivered files");
                AssertTrue(t.FileBytes > 0, "telemetry counts the delivered bytes");

                // The on-demand fetch pointer is present.
                AssertContains("armada_fetch_context", on.Section, "the fetch-context pointer is present");

                // It does NOT tell the captain to read every file under shared/.
                AssertFalse(on.Section.Contains(ReadEveryFilePhrase), "slimmed section drops the read-every-file instruction");

                // The authority rule survives (reference material, not authority).
                AssertContains("win on conflict", on.Section, "the authority rule survives in the slimmed section");
            });

            await RunTest("FlagOn_LargeMemory_SplitsIntoFilesThatEachFitOneRead_AndLosesNothing", async () =>
            {
                List<ContextChunk> chunks = new List<ContextChunk>();
                for (int i = 0; i < 12; i++) chunks.Add(Core("core.big" + i.ToString("00"), i, LongBody("Core rule text " + i + " line.\n")));
                chunks.Add(Core("core.huge", 99, HugeBody()));
                ContextRetrievalService service = new ContextRetrievalService(chunks);

                ContextBriefDelivery on = await MissionService.BuildMemoryDeliveryAsync(
                    slimmingEnabled: true, retrieval: service,
                    memoryRoot: "/memory-root", repoFolder: "examplevessel", unresolvedReason: null,
                    persona: "Worker", vesselName: "ExampleVessel", query: "task",
                    leafBudgetBytes: 24000, fetchToolEnabled: false, sortLeaves: null, logging: null);

                AssertTrue(on.Files.Count > 1, "memory larger than one file is split across files");
                foreach (ContextBriefFile f in on.Files)
                {
                    AssertTrue(f.Bytes <= ContextBriefRenderer.MaxFileBytes, f.RelativePath + " fits the byte bound (" + f.Bytes + ")");
                    AssertTrue(f.Content.Split('\n').Length - 1 <= ContextBriefRenderer.MaxFileLines, f.RelativePath + " fits the line bound");
                }

                string files = AllFileText(on);
                foreach (ContextChunk c in chunks)
                {
                    foreach (string line in c.Text.Split('\n'))
                    {
                        if (line.Length == 0) continue;
                        AssertContains(line.Length > 60 ? line.Substring(0, 60) : line, files, "no line of " + c.Topic + " is lost");
                    }
                }
                AssertEqual(chunks.Count, on.Telemetry!.CoreCount, "every core chunk is accounted");
                AssertEqual(chunks.Sum(c => c.Bytes), on.Telemetry.CoreBytes, "every core byte is accounted");
            });

            await RunTest("FlagOn_LeafSort_MovesLeavesToReferenceFiles_WithoutDroppingAny", async () =>
            {
                ContextRetrievalService service = new ContextRetrievalService(SampleChunks());

                ContextBriefDelivery on = await MissionService.BuildMemoryDeliveryAsync(
                    slimmingEnabled: true, retrieval: service,
                    memoryRoot: "/memory-root", repoFolder: "examplevessel", unresolvedReason: null,
                    persona: "Worker", vesselName: "ExampleVessel", query: "alpha review task",
                    leafBudgetBytes: 24000, fetchToolEnabled: true,
                    sortLeaves: (leaves, ct) =>
                    {
                        ContextLeafSort sort = new ContextLeafSort { Outcome = "test" };
                        foreach (ContextChunk leaf in leaves) if (leaf.Topic == "leaf.general") sort.ReferenceTopics.Add(leaf.Topic);
                        return Task.FromResult(sort);
                    },
                    logging: null);

                List<ContextBriefFile> reference = on.Files.Where(f => !f.ReadFirst).ToList();
                AssertEqual(1, reference.Count, "the sorted leaf is delivered in one reference file");
                AssertTrue(reference[0].Topics.Contains("leaf.general"), "the reference file carries the sorted leaf");
                AssertContains("General leaf about alpha", reference[0].Content, "the reference leaf is delivered in full");
                AssertTrue(on.Files.Where(f => f.ReadFirst).Any(f => f.Topics.Contains("leaf.alpha")), "an unsorted leaf stays read-first");
                AssertTrue(on.Files.Where(f => f.ReadFirst).Any(f => f.Topics.Contains("leaf.safety.example")), "a safety leaf is never reference");
                AssertTrue(on.Section.IndexOf("### Read first") < on.Section.IndexOf("### Reference"), "reference files are listed after the read-first files");
                AssertEqual(1, on.Telemetry!.ReferenceLeafCount, "telemetry counts the reference leaf");
                AssertEqual("test", on.Telemetry.LeafSortOutcome, "telemetry records the sort outcome");
            });

            await RunTest("FlagOn_LeafSortThrows_EveryLeafStaysReadFirst_AndTheFailureIsRecorded", async () =>
            {
                ContextRetrievalService service = new ContextRetrievalService(SampleChunks());

                ContextBriefDelivery on = await MissionService.BuildMemoryDeliveryAsync(
                    slimmingEnabled: true, retrieval: service,
                    memoryRoot: "/memory-root", repoFolder: "examplevessel", unresolvedReason: null,
                    persona: "Worker", vesselName: "ExampleVessel", query: "alpha review task",
                    leafBudgetBytes: 24000, fetchToolEnabled: true,
                    sortLeaves: (leaves, ct) => throw new InvalidOperationException("forced sort failure"),
                    logging: null);

                AssertTrue(on.Files.All(f => f.ReadFirst), "a failed sort keeps every file read-first");
                AssertContains("General leaf about alpha", AllFileText(on), "a failed sort drops no leaf");
                AssertEqual("error", on.Telemetry!.LeafSortOutcome, "the failed sort is recorded");
                AssertFalse(on.Telemetry.FellBack, "a failed sort is not a retrieval fallback");
            });

            await RunTest("WriteBriefFiles_ReplacesAnEarlierMissionsFiles", async () =>
            {
                string dock = Path.Combine(Path.GetTempPath(), "armada-memfiles-" + Guid.NewGuid().ToString("N"));
                try
                {
                    ContextBriefFile stale = new ContextBriefFile { RelativePath = ContextBriefRenderer.MemoryFolder + "/09-reference.md", Content = "stale" };
                    await MissionService.WriteBriefFilesAsync(dock, ContextBriefRenderer.MemoryFolder, new List<ContextBriefFile> { stale });
                    ContextBriefFile fresh = new ContextBriefFile { RelativePath = ContextBriefRenderer.MemoryFolder + "/01-core.md", Content = "fresh" };
                    await MissionService.WriteBriefFilesAsync(dock, ContextBriefRenderer.MemoryFolder, new List<ContextBriefFile> { fresh });

                    string folder = Path.Combine(dock, "_briefing", "memory");
                    AssertFalse(File.Exists(Path.Combine(folder, "09-reference.md")), "an earlier mission's memory file is removed");
                    AssertEqual("fresh", File.ReadAllText(Path.Combine(folder, "01-core.md")), "the new file is written");

                    await MissionService.WriteBriefFilesAsync(dock, ContextBriefRenderer.MemoryFolder, new List<ContextBriefFile>());
                    AssertFalse(Directory.Exists(folder), "a brief with no memory files leaves no memory folder");
                }
                finally
                {
                    if (Directory.Exists(dock)) Directory.Delete(dock, true);
                }
            });

            await RunTest("FlagOn_NoBuiltIndex_FallsBackToFullSection", async () =>
            {
                ContextBriefDelivery on = await MissionService.BuildMemoryDeliveryAsync(
                    slimmingEnabled: true, retrieval: null,
                    memoryRoot: "/memory-root", repoFolder: "examplevessel", unresolvedReason: null,
                    persona: "Worker", vesselName: "ExampleVessel", query: "task",
                    leafBudgetBytes: 24000, fetchToolEnabled: true, sortLeaves: null, logging: null);
                ContextBriefTelemetry? t = on.Telemetry;

                string full = MissionService.BuildAiMemorySection("/memory-root", "examplevessel", null);
                AssertEqual(full, on.Section, "no built index falls back to exactly the full section");
                AssertEqual(0, on.Files.Count, "the fallback delivers no files");
                AssertNotNull(t, "fallback still produces telemetry");
                AssertTrue(t!.FellBack, "telemetry marks the fallback");
                AssertNotNull(t.FallbackReason, "the fallback names a reason");
                AssertTrue(on.Section.Contains(ReadEveryFilePhrase), "the fallback section is the full read-every-file section");
                AssertTrue(t.SectionBytes > 0, "the fallback section is not empty");
            });

            await RunTest("FlagOn_ForcedRetrievalFailure_DegradesToFullSection_NeverEmpty", async () =>
            {
                // A ranker that always throws forces the retrieval service down its degraded fail-safe
                // path. The slimming seam must then fall back to the full section, never ship fewer
                // rules, and record the degradation.
                ContextRetrievalService service = new ContextRetrievalService(SampleChunks(), new ThrowingRanker());

                ContextBriefDelivery on = await MissionService.BuildMemoryDeliveryAsync(
                    slimmingEnabled: true, retrieval: service,
                    memoryRoot: "/memory-root", repoFolder: "examplevessel", unresolvedReason: null,
                    persona: "Worker", vesselName: "ExampleVessel", query: "task",
                    leafBudgetBytes: 24000, fetchToolEnabled: true, sortLeaves: null, logging: null);
                ContextBriefTelemetry? t = on.Telemetry;

                string full = MissionService.BuildAiMemorySection("/memory-root", "examplevessel", null);
                AssertEqual(full, on.Section, "a degraded retrieval falls back to exactly the full section");
                AssertNotNull(t, "degraded fallback still produces telemetry");
                AssertTrue(t!.FellBack, "telemetry marks the degraded fallback");
                AssertTrue(t.Degraded, "telemetry marks the result degraded");
                AssertTrue(on.Section.Contains(ReadEveryFilePhrase), "the degraded fallback is the full section");
                AssertTrue(on.Section.Length > 0, "the section is never empty");
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

        private static string AllFileText(ContextBriefDelivery delivery)
        {
            return String.Join("\n", delivery.Files.Select(f => f.Content));
        }

        private static string HugeBody()
        {
            // One chunk larger than a whole file, with one line longer than a file, so the split path runs.
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < 400; i++) sb.Append("Huge rule line " + i + " of a single chunk.\n");
            sb.Append(new string('y', 20000)).Append('\n');
            return sb.ToString();
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
