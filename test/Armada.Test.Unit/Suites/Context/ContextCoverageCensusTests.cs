namespace Armada.Test.Unit.Suites.Context
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Context;
    using Armada.Core.Context.Census;
    using Armada.Test.Common;

    /// <summary>
    /// The context-retrieval coverage census, registered so its SAFETY-RECALL part is a gate.
    ///
    /// The first test is the invariant: over a representative set of requests (each vessel, each
    /// persona, an orchestrator, and a no-query request), the retrieval result must carry EVERY
    /// tier=core chunk and EVERY domain-matched must_retrieve chunk. It runs against a hermetic chunk
    /// set that always executes on the gate, and, when the live AI-Memory and docs roots resolve, also
    /// against the live index. It FAILS if any core or must_retrieve chunk is ever dropped, so a
    /// regression in the retrieval service that budget-limits or filters the core, or skips a
    /// must_retrieve leaf, breaks the build.
    ///
    /// The remaining tests are the report harness. read_when recall runs against the live index and is
    /// REPORTED, not gated on its 90% target, so a weak-metadata leaf never fails the build. The two
    /// database-sampling parts (byte reduction, failure replay) run only when
    /// <c>ARMADA_CENSUS_REPORT=1</c> and their sample files are provided; on the normal gate they log
    /// that they were skipped and pass, so they are never flaky. Run by hand to produce the committed
    /// report numbers:
    ///   ARMADA_CENSUS_REPORT=1 ARMADA_CENSUS_TASKS_FILE=... ARMADA_CENSUS_MAPPINGS_FILE=... \
    ///   ARMADA_CENSUS_UNMAPPED=N dotnet run --project test/Armada.Test.Unit -- --suite ContextCoverageCensus
    /// </summary>
    public sealed class ContextCoverageCensusTests : TestSuite
    {
        // A vessel named by no chunk, so a case always covers a vessel without leaves.
        private const string _UnscopedVessel = "UnscopedVessel";
        private static readonly string[] _Personas = new[] { "Architect", "Worker", "TestEngineer", "Judge" };

        // Generous read_when budget and a realistic per-task leaf budget. Reported with the numbers.
        private const int ReadWhenBudgetBytes = 32768;
        private const int RealisticLeafBudgetBytes = 16384;

        /// <summary>Suite name.</summary>
        public override string Name => "ContextCoverageCensus";

        /// <summary>Run the census.</summary>
        protected override async Task RunTestsAsync()
        {
            // ---- Part 1: the safety-recall INVARIANT (gate). Hermetic set always; live set when present. ----
            await RunTest("SafetyRecall_Invariant_Hermetic_EveryCoreAndMustRetrieveAlwaysReturned", () =>
            {
                List<ContextChunk> chunks = HermeticChunks();
                ContextCoverageCensus.SafetyRecallReport report =
                    ContextCoverageCensus.SafetyRecall(chunks, BuildCases(chunks));

                foreach (string f in report.Failures) Console.WriteLine("CENSUS: safety.hermetic.FAILURE " + f);
                AssertTrue(report.CoreCount > 0, "the hermetic set has core chunks");
                AssertTrue(report.Pass, "hermetic safety recall must be 100%: " + String.Join("; ", report.Failures));
                return Task.CompletedTask;
            });

            await RunTest("SafetyRecall_Invariant_Live_EveryCoreAndMustRetrieveAlwaysReturned", () =>
            {
                ContextIndex? live = TryBuildLiveIndex(out string note);
                if (live == null)
                {
                    Console.WriteLine("CENSUS: safety.live SKIPPED (" + note + ")");
                    return Task.CompletedTask;
                }

                ContextCoverageCensus.SafetyRecallReport report =
                    ContextCoverageCensus.SafetyRecall(live.Chunks, BuildCases(live.Chunks));

                Console.WriteLine("CENSUS: safety.live core_count=" + report.CoreCount
                    + " cases=" + report.CasesChecked + " pass=" + report.Pass);
                foreach (string f in report.Failures) Console.WriteLine("CENSUS: safety.live.FAILURE " + f);

                AssertTrue(report.CoreCount > 0, "the live index has core chunks");
                AssertTrue(report.Pass, "live safety recall must be 100%: " + String.Join("; ", report.Failures));
                return Task.CompletedTask;
            });

            // ---- Part 2: read_when recall (reported, not gated on the 90% target). ----
            // AI-Memory leaves carry read_when only through the docs/context-index sidecar, which is
            // operator-local and untracked. Without it there is nothing to measure, so the case is a
            // named skip, never a pass that measured nothing and never a false failure.
            if (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARMADA_CENSUS_DOCS_ROOT")) && FindDocsRoot() == null)
                SkipTest("ReadWhenRecall_Reported", "docs/context-index/chunk-metadata.json sidecar not present in this checkout");
            else
            await RunTest("ReadWhenRecall_Reported", () =>
            {
                ContextIndex? live = TryBuildLiveIndex(out string note);
                if (live == null)
                {
                    Console.WriteLine("CENSUS: readwhen SKIPPED (" + note + ")");
                    return Task.CompletedTask;
                }

                ContextCoverageCensus.ReadWhenRecallReport report =
                    ContextCoverageCensus.ReadWhenRecall(live.Chunks, ReadWhenBudgetBytes);

                Console.WriteLine("CENSUS: readwhen total=" + report.Total + " recalled=" + report.Recalled
                    + " percent=" + report.Percent + " budget_bytes=" + report.LeafBudgetBytes);
                foreach (ContextCoverageCensus.ReadWhenMiss m in report.Misses)
                    Console.WriteLine("CENSUS: readwhen.MISS topic=" + m.Topic + " scope=" + m.Scope + " read_when=" + m.ReadWhen);

                // Sanity only. The target is a report criterion, not a gate.
                AssertTrue(report.Total > 0, "the live index has leaves with a read_when trigger");
                AssertTrue(report.Percent >= 0.0 && report.Percent <= 100.0, "recall is a percentage");
                return Task.CompletedTask;
            });

            // ---- Part 3: byte reduction (by-hand report only). ----
            await RunTest("ByteReduction_Reported_WhenSamplesProvided", () =>
            {
                if (Environment.GetEnvironmentVariable("ARMADA_CENSUS_DUMP_TOPICS") == "1")
                {
                    ContextIndex? idx = TryBuildLiveIndex(out string _);
                    if (idx != null)
                        foreach (ContextChunk c in idx.Chunks.OrderBy(x => x.Topic, StringComparer.Ordinal))
                            Console.WriteLine("CENSUS: topic tier=" + c.Tier + " bytes=" + c.Bytes + " " + c.Topic);
                }
                if (!ReportMode())
                {
                    Console.WriteLine("CENSUS: bytereduction SKIPPED (report mode off)");
                    return Task.CompletedTask;
                }

                ContextIndex? live = TryBuildLiveIndex(out string note);
                List<string>? tasks = ReadLines(Environment.GetEnvironmentVariable("ARMADA_CENSUS_TASKS_FILE"));
                if (live == null || tasks == null || tasks.Count == 0)
                {
                    Console.WriteLine("CENSUS: bytereduction SKIPPED (no live index or no tasks file)");
                    return Task.CompletedTask;
                }

                long baseline = EagerBaselineBytes(live, out string baseNote);
                ContextCoverageCensus.ByteReductionReport report =
                    ContextCoverageCensus.ByteReduction(live, baseline, tasks, VesselsOf(live.Chunks), RealisticLeafBudgetBytes);

                Console.WriteLine("CENSUS: bytereduction baseline_bytes=" + report.EagerBaselineBytes
                    + " core_bytes=" + report.CoreBytes + " budget_bytes=" + report.LeafBudgetBytes
                    + " samples=" + report.Samples
                    + " min_pct=" + report.MinPct + " median_pct=" + report.MedianPct + " max_pct=" + report.MaxPct
                    + " median_leaf_count=" + report.MedianLeafCount);
                Console.WriteLine("CENSUS: bytereduction baseline_note=" + baseNote);
                foreach (ContextCoverageCensus.ByteReductionSample d in report.Details)
                    Console.WriteLine("CENSUS: bytereduction.SAMPLE slimmed=" + d.SlimmedBytes + " pct=" + d.ReductionPct + " leaves=" + d.LeafCount);
                return Task.CompletedTask;
            });

            // ---- Part 4: failure replay (by-hand report only). ----
            await RunTest("FailureReplay_Reported_WhenMappingsProvided", () =>
            {
                if (!ReportMode())
                {
                    Console.WriteLine("CENSUS: failurereplay SKIPPED (report mode off)");
                    return Task.CompletedTask;
                }

                ContextIndex? live = TryBuildLiveIndex(out string note);
                List<ContextCoverageCensus.FailureMapping>? mappings = ReadMappings(Environment.GetEnvironmentVariable("ARMADA_CENSUS_MAPPINGS_FILE"));
                if (live == null || mappings == null)
                {
                    Console.WriteLine("CENSUS: failurereplay SKIPPED (no live index or no mappings file)");
                    return Task.CompletedTask;
                }

                int unmapped = 0;
                int.TryParse(Environment.GetEnvironmentVariable("ARMADA_CENSUS_UNMAPPED"), out unmapped);
                // Guard: name any needed topic that is not in the index, so a mapping typo is
                // never silently counted as a regression.
                HashSet<string> known = new HashSet<string>(live.Chunks.Select(c => c.Topic), StringComparer.Ordinal);
                foreach (ContextCoverageCensus.FailureMapping m in mappings)
                    if (!known.Contains(m.NeededTopic))
                        Console.WriteLine("CENSUS: failurereplay.UNKNOWN_TOPIC " + m.Label + " -> " + m.NeededTopic);


                int replayBudget = RealisticLeafBudgetBytes;
                if (int.TryParse(Environment.GetEnvironmentVariable("ARMADA_CENSUS_LEAF_BUDGET"), out int b) && b > 0) replayBudget = b;
                ContextCoverageCensus.FailureReplayReport report =
                    ContextCoverageCensus.FailureReplay(live, mappings, unmapped, replayBudget);

                Console.WriteLine("CENSUS: failurereplay budget_bytes=" + replayBudget + " mapped=" + report.Mapped + " regressions=" + report.Regressions
                    + " unmapped=" + report.Unmapped);
                foreach (string r in report.RegressionDetails) Console.WriteLine("CENSUS: failurereplay.REGRESSION " + r);
                return Task.CompletedTask;
            });

            // ---- Part 5: safety-leaf integrity, checked by an INDEPENDENT oracle. ----
            // The safety-recall census derives its expectation from the same index under test, so a
            // dropped must_retrieve mapping (a renamed heading, or a file that fell under the sub-chunk
            // threshold) hides: the index loses the leaf, the oracle expects none, the gate stays green.
            // This test instead loads the sidecar on its own and asserts every must_retrieve key still
            // resolves to a generated chunk, and every managed vessel still yields a safety leaf.
            await RunTest("Every must_retrieve sidecar key resolves to a live chunk and every vessel keeps a safety leaf", () =>
            {
                ContextIndex? live = TryBuildLiveIndex(out string note);
                string? docsRoot = FindDocsRoot();
                if (live == null || docsRoot == null)
                {
                    Console.WriteLine("CENSUS: safetyleaf SKIPPED (" + note + ")");
                    return Task.CompletedTask;
                }

                ChunkMetadataSidecar sidecar = ChunkMetadataSidecar.Load(ChunkMetadataSidecar.ResolvePath(null, docsRoot), null);
                HashSet<string> chunkIds = new HashSet<string>(live.Chunks.Select(c => c.Id), StringComparer.Ordinal);

                List<string> orphanedSafety = new List<string>();
                foreach (string key in sidecar.Keys)
                    if (!chunkIds.Contains(key)
                        && sidecar.TryGet(key, out ChunkMetadataOverride ov)
                        && ov.MustRetrieve != null && ov.MustRetrieve.Count > 0)
                        orphanedSafety.Add(key);
                AssertEqual(0, orphanedSafety.Count,
                    "every must_retrieve sidecar key must resolve to a generated chunk; orphaned: " + String.Join(", ", orphanedSafety));

                foreach (string vessel in VesselsOf(live.Chunks).Where(v => !String.Equals(v, _UnscopedVessel, StringComparison.Ordinal)))
                {
                    bool hasLeaf = live.Chunks.Any(c => c.MustRetrieve != null
                        && c.MustRetrieve.Any(d => String.Equals(d, vessel, StringComparison.OrdinalIgnoreCase)));
                    AssertTrue(hasLeaf, "managed vessel '" + vessel + "' must keep at least one must_retrieve safety leaf");
                }
                return Task.CompletedTask;
            });
        }

        // ---- Representative cases over a chunk set: each vessel, each persona, orchestrator, no-query. ----
        private static List<ContextCoverageCensus.SafetyRecallCase> BuildCases(IReadOnlyList<ContextChunk> chunks)
        {
            List<ContextCoverageCensus.SafetyRecallCase> cases = new List<ContextCoverageCensus.SafetyRecallCase>();

            foreach (string vessel in VesselsOf(chunks))
            {
                cases.Add(new ContextCoverageCensus.SafetyRecallCase
                {
                    Label = "vessel:" + vessel,
                    Request = new ContextRetrievalRequest { Vessel = vessel, Query = "work on " + vessel, MaxLeafBytes = 0 },
                    ExpectedMustRetrieve = ExpectedMustRetrieve(chunks, vessel, null)
                });
            }

            foreach (string persona in _Personas)
            {
                cases.Add(new ContextCoverageCensus.SafetyRecallCase
                {
                    Label = "persona:" + persona,
                    Request = new ContextRetrievalRequest { RequestingPersona = persona, Query = persona + " task", MaxLeafBytes = 0 },
                    ExpectedMustRetrieve = ExpectedMustRetrieve(chunks, null, persona)
                });
            }

            cases.Add(new ContextCoverageCensus.SafetyRecallCase
            {
                Label = "orchestrator",
                Request = new ContextRetrievalRequest { Query = "dispatch and land work", MaxLeafBytes = 0 },
                ExpectedMustRetrieve = ExpectedMustRetrieve(chunks, null, null)
            });

            cases.Add(new ContextCoverageCensus.SafetyRecallCase
            {
                Label = "no-query",
                Request = new ContextRetrievalRequest { MaxLeafBytes = 0 },
                ExpectedMustRetrieve = ExpectedMustRetrieve(chunks, null, null)
            });

            return cases;
        }

        // The vessels a chunk set names through its vessel: scopes, plus one vessel no chunk names.
        // Read from the chunks rather than a committed list, so no real vessel name is hard-coded.
        private static List<string> VesselsOf(IReadOnlyList<ContextChunk> chunks)
        {
            SortedSet<string> vessels = new SortedSet<string>(StringComparer.Ordinal) { _UnscopedVessel };
            foreach (ContextChunk chunk in chunks)
            {
                if (chunk.AppliesTo == null) continue;
                foreach (string scope in chunk.AppliesTo)
                {
                    if (scope != null && scope.StartsWith("vessel:", StringComparison.OrdinalIgnoreCase) && scope.Length > "vessel:".Length)
                        vessels.Add(scope.Substring("vessel:".Length).Trim());
                }
            }
            return vessels.ToList();
        }

        // An independent oracle for the must_retrieve chunks a vessel/persona domain must receive:
        // every must_retrieve leaf whose bare domain token equals the vessel or persona. This mirrors
        // the service's vessel/persona domain-match branch without calling its private method, so the
        // two must agree or the invariant fails.
        private static List<string> ExpectedMustRetrieve(IReadOnlyList<ContextChunk> chunks, string? vessel, string? persona)
        {
            string? v = vessel?.Trim().ToLowerInvariant();
            string? p = persona?.Trim().ToLowerInvariant();
            List<string> expected = new List<string>();

            foreach (ContextChunk c in chunks)
            {
                if (c.Tier != ContextTierEnum.Leaf) continue;
                if (c.MustRetrieve == null || c.MustRetrieve.Count == 0) continue;

                foreach (string raw in c.MustRetrieve)
                {
                    if (String.IsNullOrWhiteSpace(raw)) continue;
                    string full = raw.Trim().ToLowerInvariant();
                    string bare = full;
                    int colon = full.IndexOf(':');
                    if (colon >= 0 && colon < full.Length - 1) bare = full.Substring(colon + 1).Trim();

                    bool match = (v != null && (bare == v || full == v)) || (p != null && (bare == p || full == p));
                    if (match) { expected.Add(c.Topic); break; }
                }
            }

            return expected;
        }

        // ---- Live index resolution. Read-only over the AI-Memory tree and the repo docs tree. ----
        private static ContextIndex? TryBuildLiveIndex(out string note)
        {
            string? memoryRoot = Environment.GetEnvironmentVariable("ARMADA_CENSUS_MEMORY_ROOT");
            if (String.IsNullOrWhiteSpace(memoryRoot)) memoryRoot = FindMemoryRoot();

            string? docsRoot = Environment.GetEnvironmentVariable("ARMADA_CENSUS_DOCS_ROOT");
            if (String.IsNullOrWhiteSpace(docsRoot)) docsRoot = FindDocsRoot();

            if (String.IsNullOrWhiteSpace(memoryRoot) || !Directory.Exists(memoryRoot))
            {
                note = "AI-Memory root not resolved";
                return null;
            }

            note = "memory=" + memoryRoot + (docsRoot != null ? "; docs=" + docsRoot : "; docs=<none>");
            return new ContextIndexGenerator().Build(memoryRoot, docsRoot);
        }

        // Walk up from the test binary to an ancestor that holds an AI-Memory tree, identified by
        // AI-Memory/shared/INDEX.md. Host-agnostic: no host path is hard-coded, so nothing about a
        // workstation or server layout is committed. An explicit ARMADA_CENSUS_MEMORY_ROOT overrides.
        private static string? FindMemoryRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "AI-Memory", "shared", "INDEX.md");
                if (File.Exists(candidate)) return Path.Combine(dir.FullName, "AI-Memory");
                dir = dir.Parent;
            }
            return null;
        }

        // Walk up from the test binary to a directory that holds docs/context-index/chunk-metadata.json.
        private static string? FindDocsRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "docs", "context-index", "chunk-metadata.json");
                if (File.Exists(candidate)) return Path.Combine(dir.FullName, "docs");
                dir = dir.Parent;
            }
            return null;
        }

        // The eager baseline: the always-on load an orchestrator reads today. Its AI-Memory imports
        // plus the curated docs it is told to read plus README.md and CLAUDE.md. Measured from the live
        // chunk bytes and the two root files, so the number is reproducible.
        private static long EagerBaselineBytes(ContextIndex live, out string note)
        {
            long aiMemory = live.Chunks.Where(c => c.Path.StartsWith("AI-Memory/", StringComparison.Ordinal)).Sum(c => (long)c.Bytes);

            string[] curatedDocs = new[]
            {
                "docs/armada-ops.md", "docs/MCP_API.md", "docs/MERGING.md",
                "docs/OPERATIONAL_ASSETS.md", "docs/DELIVERY_OPERATIONS.md"
            };
            long docs = live.Chunks.Where(c => curatedDocs.Any(d => c.Path.StartsWith(d, StringComparison.Ordinal))).Sum(c => (long)c.Bytes);

            long rootFiles = 0;
            string? docsRoot = FindDocsRoot();
            if (docsRoot != null)
            {
                DirectoryInfo? repo = new DirectoryInfo(docsRoot).Parent;
                if (repo != null)
                {
                    foreach (string name in new[] { "README.md", "CLAUDE.md" })
                    {
                        string p = Path.Combine(repo.FullName, name);
                        if (File.Exists(p)) rootFiles += new FileInfo(p).Length;
                    }
                }
            }

            note = "ai_memory=" + aiMemory + " curated_docs=" + docs + " root_files=" + rootFiles;
            return aiMemory + docs + rootFiles;
        }

        private static bool ReportMode() => Environment.GetEnvironmentVariable("ARMADA_CENSUS_REPORT") == "1";

        // Each non-blank line is one task description (newlines already collapsed to spaces at export).
        private static List<string>? ReadLines(string? path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            return File.ReadAllLines(path).Where(l => !String.IsNullOrWhiteSpace(l)).ToList();
        }

        // Each line: label<TAB>vessel<TAB>query<TAB>neededTopic. An empty vessel is an orchestrator domain.
        private static List<ContextCoverageCensus.FailureMapping>? ReadMappings(string? path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            List<ContextCoverageCensus.FailureMapping> list = new List<ContextCoverageCensus.FailureMapping>();
            foreach (string line in File.ReadAllLines(path))
            {
                if (String.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 4) continue;
                list.Add(new ContextCoverageCensus.FailureMapping
                {
                    Label = parts[0].Trim(),
                    Vessel = String.IsNullOrWhiteSpace(parts[1]) ? null : parts[1].Trim(),
                    Query = parts[2].Trim(),
                    NeededTopic = parts[3].Trim()
                });
            }
            return list;
        }

        // ---- A hermetic chunk set mirroring the real domain shape, so the gate always has data. ----
        private static List<ContextChunk> HermeticChunks()
        {
            return new List<ContextChunk>
            {
                Core("core.boundary", 1),
                Core("core.land-then-sync", 3),
                Core("core.proving-a-fix", 5),
                Leaf("memory.repos.examplevessel.readme", new List<string> { "vessel:ExampleVessel" }, new List<string> { "examplevessel" }),
                Leaf("memory.repos.exampleconsumer.readme", new List<string> { "vessel:ExampleConsumer" }, null),
                Leaf("memory.repos.armada.readme.dispatch", new List<string> { "orchestrator" }, null),
                Leaf("docs.judge.review", new List<string> { "persona:Judge" }, null),
                Leaf("docs.general", new List<string> { "all" }, null),
            };
        }

        private static ContextChunk Core(string topic, int order)
        {
            return new ContextChunk
            {
                Topic = topic,
                Path = "AI-Memory/shared/" + topic + ".md",
                Summary = topic,
                ReadWhen = "Always. Core rule; never retrieval-gated.",
                AppliesTo = new List<string> { "all" },
                Tier = ContextTierEnum.Core,
                Text = "Core rule body for " + topic + ".",
                CoreOrder = order
            };
        }

        private static ContextChunk Leaf(string topic, List<string> appliesTo, List<string>? mustRetrieve)
        {
            return new ContextChunk
            {
                Topic = topic,
                Path = "AI-Memory/repos/" + topic + ".md",
                Summary = topic,
                ReadWhen = "When the task needs " + topic + ".",
                AppliesTo = appliesTo,
                Tier = ContextTierEnum.Leaf,
                MustRetrieve = mustRetrieve ?? new List<string>(),
                Text = new String('x', 2000),
                CoreOrder = int.MaxValue
            };
        }
    }
}
