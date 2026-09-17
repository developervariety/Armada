namespace Armada.Test.Unit.Suites.Context
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Context;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the context-index generator: front-matter parsing, whole-file and section fallback,
    /// tier assignment from the owner allowlist, manifest shape, core-bundle derivation, determinism,
    /// and fail-open behavior. Every test builds a small hermetic memory tree in a temp directory, so
    /// the suite never depends on the live AI-Memory content.
    /// </summary>
    public sealed class ContextIndexGeneratorTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "ContextIndexGenerator";

        // A distinctive phrase from a whole-file-core source (the leak-prevention file).
        private const string CorePhrase = "NEVER-REACH-A-REPO-SENTINEL";
        // A distinctive phrase from a leaf-only section.
        private const string LeafPhrase = "LEAF-ONLY-SECTION-SENTINEL";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("ContextRetrievalSettings_CopyFrom_CopiesChunkMetadataPath", () =>
            {
                ContextRetrievalSettings source = new ContextRetrievalSettings { ChunkMetadataPath = "/srv/x/context-sidecar/chunk-metadata.json" };
                ContextRetrievalSettings target = new ContextRetrievalSettings();
                target.CopyFrom(source);
                AssertEqual("/srv/x/context-sidecar/chunk-metadata.json", target.ChunkMetadataPath!);
                return Task.CompletedTask;
            });

            await RunTest("FrontMatter_Parse_ReadsAllFields", () =>
            {
                string doc = "---\n" +
                    "topic: ops.example\n" +
                    "summary: An example chunk.\n" +
                    "read_when: When you need the example.\n" +
                    "applies_to: [orchestrator, persona:Judge]\n" +
                    "tier: leaf\n" +
                    "must_retrieve: [port:examplevessel]\n" +
                    "---\n" +
                    "Body line one.\n";
                ContextChunkFrontMatter fm = ContextChunkFrontMatter.Parse(doc);
                AssertTrue(fm.HasFrontMatter, "front-matter should be detected");
                AssertEqual("ops.example", fm.Topic);
                AssertEqual("An example chunk.", fm.Summary);
                AssertEqual("When you need the example.", fm.ReadWhen);
                AssertEqual(ContextTierEnum.Leaf, fm.Tier!.Value);
                AssertNotNull(fm.AppliesTo);
                AssertEqual(2, fm.AppliesTo!.Count);
                AssertEqual("orchestrator", fm.AppliesTo![0]);
                AssertEqual("port:examplevessel", fm.MustRetrieve![0]);
                AssertEqual("Body line one.\n", fm.Body);
                return Task.CompletedTask;
            });

            await RunTest("FrontMatter_Parse_NoFence_IsWholeBody", () =>
            {
                string doc = "# Title\n\nJust a body, no front-matter.\n";
                ContextChunkFrontMatter fm = ContextChunkFrontMatter.Parse(doc);
                AssertFalse(fm.HasFrontMatter, "no fence means no front-matter");
                AssertEqual(doc, fm.Body);
                AssertNull(fm.Topic);
                AssertNull(fm.Tier);
                return Task.CompletedTask;
            });

            await RunTest("FrontMatter_Parse_BlockListForm", () =>
            {
                string doc = "---\n" +
                    "topic: t\n" +
                    "applies_to:\n" +
                    "  - orchestrator\n" +
                    "  - vessel:ExampleVessel\n" +
                    "tier: core\n" +
                    "---\nbody\n";
                ContextChunkFrontMatter fm = ContextChunkFrontMatter.Parse(doc);
                AssertTrue(fm.HasFrontMatter, "front-matter present");
                AssertEqual(2, fm.AppliesTo!.Count);
                AssertEqual("vessel:ExampleVessel", fm.AppliesTo![1]);
                AssertEqual(ContextTierEnum.Core, fm.Tier!.Value);
                return Task.CompletedTask;
            });

            await RunTest("TierConfig_ApprovedCoreFiles_ResolveToCore", () =>
            {
                ContextTierConfig cfg = new ContextTierConfig();
                AssertTrue(cfg.IsWholeFileCore("shared/repository-boundary-and-leak-prevention.md", out _), "leak-prevention is whole-file core");
                AssertTrue(cfg.IsWholeFileCore("shared/land-then-sync.md", out _), "land-then-sync is whole-file core");
                AssertTrue(cfg.IsWholeFileCore("repos/armada/typed-decisions.md", out _), "typed-decisions is whole-file core");
                AssertTrue(cfg.IsSectionCore("shared/unified-project-memory.md", "Boundaries", out _), "Boundaries section is core");
                AssertTrue(cfg.IsSectionCore("shared/unified-project-memory.md", "Proving a fix", out _), "Proving a fix section is core");
                AssertTrue(cfg.IsSectionCore("shared/unified-project-memory.md", "Reporting style", out _), "Reporting style section is core");
                AssertTrue(cfg.IsSectionCore("shared/unified-project-memory.md", "Domain scope", out _), "Domain scope section is core");
                AssertTrue(cfg.IsSectionCore("shared/sole-memory-source.md", "How each runtime loads it", out _), "loaders section is core");
                AssertTrue(cfg.IsSectionCore("repos/armada/README.md", "Where Armada runs", out _), "Where Armada runs is core");
                return Task.CompletedTask;
            });

            await RunTest("TierConfig_NonAllowlisted_ResolveToLeaf", () =>
            {
                ContextTierConfig cfg = new ContextTierConfig();
                AssertFalse(cfg.IsWholeFileCore("machine-notes/macos.md", out _), "host notes are never whole-file core");
                AssertFalse(cfg.IsWholeFileCore("repos/examplevessel/README.md", out _), "examplevessel readme is a leaf");
                AssertFalse(cfg.IsSectionCore("shared/unified-project-memory.md", "What belongs here", out _), "a non-listed section is leaf");
                AssertFalse(cfg.IsSectionCore("repos/armada/README.md", "Deploying Armada on the server", out _), "deploy section is leaf");
                return Task.CompletedTask;
            });

            await RunTest("Slug_IsDeterministicAndAnchorShaped", () =>
            {
                AssertEqual("proving-a-fix", ContextTierConfig.Slug("Proving a fix"));
                AssertEqual("where-armada-runs", ContextTierConfig.Slug("Where Armada runs"));
                AssertEqual("how-each-runtime-loads-it", ContextTierConfig.Slug("How each runtime loads it"));
                return Task.CompletedTask;
            });

            await RunTest("Build_AssignsTiersFromConfig", () =>
            {
                string root = CreateFakeMemoryTree();
                try
                {
                    ContextIndexGenerator gen = new ContextIndexGenerator();
                    ContextIndex index = gen.Build(root, null);

                    ContextChunk boundary = FindByPath(index, "AI-Memory/shared/repository-boundary-and-leak-prevention.md");
                    AssertEqual(ContextTierEnum.Core, boundary.Tier);

                    ContextChunk boundariesSection = FindByPath(index, "AI-Memory/shared/unified-project-memory.md#boundaries");
                    AssertEqual(ContextTierEnum.Core, boundariesSection.Tier);

                    ContextChunk leafSection = FindByPath(index, "AI-Memory/shared/unified-project-memory.md#what-belongs-here");
                    AssertEqual(ContextTierEnum.Leaf, leafSection.Tier);

                    ContextChunk host = FindByPath(index, "AI-Memory/machine-notes/macos.md");
                    AssertEqual(ContextTierEnum.Leaf, host.Tier);
                    return Task.CompletedTask;
                }
                finally { SafeDelete(root); }
            });

            await RunTest("Build_ManifestShape_IsComplete", () =>
            {
                string root = CreateFakeMemoryTree();
                try
                {
                    ContextIndexGenerator gen = new ContextIndexGenerator();
                    ContextIndex index = gen.Build(root, null);
                    ContextIndexManifest m = index.Manifest;

                    AssertEqual(1, m.Version);
                    AssertEqual(index.Chunks.Count, m.ChunkCount);
                    AssertEqual(index.Chunks.Count(c => c.Tier == ContextTierEnum.Core), m.CoreCount);
                    AssertTrue(m.CoreCount >= 1, "at least the synthesized map is core");
                    AssertEqual(m.ChunkCount, m.Chunks.Count);

                    foreach (ContextManifestEntry e in m.Chunks)
                    {
                        AssertFalse(String.IsNullOrEmpty(e.Id), "every entry has an id");
                        AssertEqual(e.Id, e.Topic);
                        AssertFalse(String.IsNullOrEmpty(e.Path), "every entry has a path");
                        AssertNotNull(e.AppliesTo);
                        AssertNotNull(e.MustRetrieve);
                        AssertFalse(e.Path.StartsWith("/", StringComparison.Ordinal), "path is logical, never host-absolute");
                    }

                    // Manifest is valid JSON and serializes the tier as a lower-case string.
                    using JsonDocument parsed = JsonDocument.Parse(index.ManifestJson);
                    string firstTier = parsed.RootElement.GetProperty("chunks")[0].GetProperty("tier").GetString()!;
                    AssertTrue(firstTier == "core" || firstTier == "leaf", "tier serializes as core/leaf, got " + firstTier);
                    return Task.CompletedTask;
                }
                finally { SafeDelete(root); }
            });

            await RunTest("Build_CoreBundle_ContainsCore_ExcludesLeaf", () =>
            {
                string root = CreateFakeMemoryTree();
                try
                {
                    ContextIndexGenerator gen = new ContextIndexGenerator();
                    ContextIndex index = gen.Build(root, null);

                    AssertContains(CorePhrase, index.CoreBundle);
                    AssertFalse(index.CoreBundle.Contains(LeafPhrase, StringComparison.Ordinal),
                        "the core bundle must not carry a leaf-only section");
                    AssertContains("Always-On Core Bundle", index.CoreBundle);
                    return Task.CompletedTask;
                }
                finally { SafeDelete(root); }
            });

            await RunTest("Build_IsDeterministic_TwoRunsIdentical", () =>
            {
                string root = CreateFakeMemoryTree();
                try
                {
                    ContextIndexGenerator gen = new ContextIndexGenerator();
                    ContextIndex a = gen.Build(root, null);
                    ContextIndex b = gen.Build(root, null);
                    AssertEqual(a.ManifestJson, b.ManifestJson);
                    AssertEqual(a.CoreBundle, b.CoreBundle);
                    return Task.CompletedTask;
                }
                finally { SafeDelete(root); }
            });

            await RunTest("Build_FrontMatterFile_UsesItsMetadata", () =>
            {
                string root = CreateFakeMemoryTree();
                try
                {
                    // A leaf file that carries its own front-matter overrides the whole-file fallback.
                    string fmPath = Path.Combine(root, "shared", "with-frontmatter.md");
                    File.WriteAllText(fmPath,
                        "---\ntopic: shared.custom-topic\nsummary: Custom.\ntier: leaf\n---\nBody.\n");
                    ContextIndexGenerator gen = new ContextIndexGenerator();
                    ContextIndex index = gen.Build(root, null);
                    ContextChunk c = index.Chunks.Single(x => x.Topic == "shared.custom-topic");
                    AssertEqual(ContextTierEnum.Leaf, c.Tier);
                    AssertEqual("Custom.", c.Summary);
                    return Task.CompletedTask;
                }
                finally { SafeDelete(root); }
            });

            await RunTest("Build_DocsTemplate_IsNotIndexed_FilledCopyIs", () =>
            {
                string root = CreateFakeMemoryTree();
                string docs = NewTempDir("ctxdocs");
                try
                {
                    // A tracked template and the operator's filled copy carry the same front-matter shape;
                    // only the filled copy is guidance, so only it becomes a chunk.
                    Directory.CreateDirectory(Path.Combine(docs, "ops"));
                    File.WriteAllText(Path.Combine(docs, "armada-ops.md"), "# Operator Guide\n\nIndex.\n");
                    File.WriteAllText(Path.Combine(docs, "ops", "05-standard-workflow.md"),
                        "---\ntopic: ops.filled-chapter\nsummary: Filled.\ntier: leaf\n---\nFilled body.\n");
                    File.WriteAllText(Path.Combine(docs, "ops", "05-standard-workflow.example.md"),
                        "---\ntopic: ops.template-chapter\nsummary: Template.\ntier: leaf\n---\nTemplate body.\n");
                    ContextIndexGenerator gen = new ContextIndexGenerator();
                    ContextIndex index = gen.Build(root, docs);
                    AssertTrue(index.Chunks.Any(x => x.Topic == "ops.filled-chapter"), "the filled chapter should be indexed");
                    AssertFalse(index.Chunks.Any(x => x.Topic == "ops.template-chapter"), "a *.example.md template must not be indexed");
                    AssertFalse(index.Chunks.Any(x => x.Path.EndsWith(".example.md", StringComparison.Ordinal)), "no chunk may come from a template file");
                    return Task.CompletedTask;
                }
                finally { SafeDelete(root); SafeDelete(docs); }
            });

            await RunTest("Generate_WritesArtifacts", () =>
            {
                string root = CreateFakeMemoryTree();
                string outDir = NewTempDir("ctxout");
                try
                {
                    ContextIndexGenerator gen = new ContextIndexGenerator();
                    ContextIndexGenerationSummary s = gen.Generate(root, null, outDir);
                    AssertTrue(s.Written, "artifacts should be written");
                    AssertTrue(File.Exists(Path.Combine(outDir, "manifest.json")), "manifest written");
                    AssertTrue(File.Exists(Path.Combine(outDir, "context-core.md")), "core bundle written");
                    AssertTrue(s.CoreBundleBytes > 0, "core bundle has bytes");
                    return Task.CompletedTask;
                }
                finally { SafeDelete(root); SafeDelete(outDir); }
            });

            await RunTest("FailOpen_NullRoots_ReturnsMapCoreOnly_NoThrow", () =>
            {
                ContextIndexGenerator gen = new ContextIndexGenerator();
                ContextIndex index = gen.Build(null, null);
                // Only the synthesized map chunk exists, and it is core.
                AssertEqual(1, index.Chunks.Count);
                AssertEqual(ContextTierEnum.Core, index.Chunks[0].Tier);
                AssertEqual("core.context-index.map", index.Chunks[0].Topic);
                return Task.CompletedTask;
            });

            await RunTest("FailOpen_NonexistentRoot_IsSkippedNotThrown", () =>
            {
                string missing = Path.Combine(Path.GetTempPath(), "ctx_missing_" + Guid.NewGuid().ToString("N"));
                ContextIndexGenerator gen = new ContextIndexGenerator();
                ContextIndex index = gen.Build(missing, null);
                AssertEqual(1, index.Chunks.Count);
                return Task.CompletedTask;
            });

            await RunTest("Generate_MissingMemory_ReportsNote_NoThrow", () =>
            {
                string outDir = NewTempDir("ctxout2");
                try
                {
                    ContextIndexGenerator gen = new ContextIndexGenerator();
                    ContextIndexGenerationSummary s = gen.Generate(null, null, outDir);
                    AssertTrue(s.Written, "still writes the map-only artifacts");
                    AssertNotNull(s.Note);
                    AssertContains("AI-Memory root missing", s.Note!);
                    return Task.CompletedTask;
                }
                finally { SafeDelete(outDir); }
            });

            await RunTest("Sidecar_Merge_OverridesLeafMetadata", () =>
            {
                string root = CreateFakeMemoryTree();
                string sidecar = WriteSidecar(
                    "{\n" +
                    "  \"version\": 1,\n" +
                    "  \"chunks\": {\n" +
                    "    \"memory.repos.examplevessel.readme\": {\n" +
                    "      \"summary\": \"SIDE-SUMMARY.\",\n" +
                    "      \"read_when\": \"You are porting an ExampleVessel decoder.\",\n" +
                    "      \"applies_to\": [\"vessel:ExampleVessel\"],\n" +
                    "      \"must_retrieve\": [\"examplevessel\"]\n" +
                    "    }\n" +
                    "  }\n" +
                    "}\n");
                try
                {
                    ContextIndexGenerator gen = new ContextIndexGenerator();
                    ContextIndex index = gen.Build(root, null, sidecar);
                    ContextChunk c = index.Chunks.Single(x => x.Id == "memory.repos.examplevessel.readme");

                    // Sidecar wins on every field it names.
                    AssertEqual("SIDE-SUMMARY.", c.Summary);
                    AssertEqual("You are porting an ExampleVessel decoder.", c.ReadWhen);
                    AssertEqual(1, c.AppliesTo.Count);
                    AssertEqual("vessel:ExampleVessel", c.AppliesTo[0]);
                    AssertEqual(1, c.MustRetrieve.Count);
                    AssertEqual("examplevessel", c.MustRetrieve[0]);
                    // The chunk stays a leaf; the sidecar carries no tier.
                    AssertEqual(ContextTierEnum.Leaf, c.Tier);

                    // The override reaches the manifest entry too.
                    ContextManifestEntry e = index.Manifest.Chunks.Single(x => x.Id == "memory.repos.examplevessel.readme");
                    AssertEqual("SIDE-SUMMARY.", e.Summary);
                    AssertEqual("You are porting an ExampleVessel decoder.", e.ReadWhen);
                    AssertEqual(1, e.MustRetrieve.Count);
                    AssertEqual("examplevessel", e.MustRetrieve[0]);
                    return Task.CompletedTask;
                }
                finally { SafeDelete(root); SafeDeleteFile(sidecar); }
            });

            await RunTest("Sidecar_UnnamedChunk_KeepsAutoDerived", () =>
            {
                string root = CreateFakeMemoryTree();
                // The sidecar names only the examplevessel leaf; the macos leaf is untouched.
                string sidecar = WriteSidecar(
                    "{ \"chunks\": { \"memory.repos.examplevessel.readme\": { \"summary\": \"X.\" } } }");
                try
                {
                    ContextIndexGenerator gen = new ContextIndexGenerator();
                    ContextIndex index = gen.Build(root, null, sidecar);
                    ContextChunk host = index.Chunks.Single(x => x.Id == "memory.machine-notes.macos");
                    // Auto-derived: summary is the leading heading, read_when is empty, applies_to is "all".
                    AssertEqual("macOS Workstation Notes", host.Summary);
                    AssertEqual("", host.ReadWhen);
                    AssertEqual(1, host.AppliesTo.Count);
                    AssertEqual("all", host.AppliesTo[0]);
                    AssertEqual(0, host.MustRetrieve.Count);
                    return Task.CompletedTask;
                }
                finally { SafeDelete(root); SafeDeleteFile(sidecar); }
            });

            await RunTest("Sidecar_MustRetrieve_FlowsToRetrievalService", () =>
            {
                string root = CreateFakeMemoryTree();
                string sidecar = WriteSidecar(
                    "{ \"chunks\": { \"memory.repos.examplevessel.readme\": { \"must_retrieve\": [\"examplevessel\"] } } }");
                try
                {
                    ContextIndexGenerator gen = new ContextIndexGenerator();
                    ContextIndex index = gen.Build(root, null, sidecar);

                    ContextRetrievalService svc = new ContextRetrievalService(index.Chunks);
                    // A vessel-scoped request with NO query keyword still force-includes the safety leaf.
                    ContextRetrievalResult r = svc.Retrieve(new ContextRetrievalRequest
                    {
                        Vessel = "ExampleVessel",
                        MaxLeafBytes = 0
                    });
                    AssertTrue(r.MustRetrieve.Any(c => c.Id == "memory.repos.examplevessel.readme"),
                        "the examplevessel safety leaf must be force-included for an ExampleVessel request");
                    return Task.CompletedTask;
                }
                finally { SafeDelete(root); SafeDeleteFile(sidecar); }
            });

            await RunTest("Sidecar_DoesNotChangeTier_NorCoreBundle", () =>
            {
                string root = CreateFakeMemoryTree();
                // Name a leaf (all fields) AND a core chunk (must_retrieve only, which never prints in
                // the bundle) to prove the sidecar changes neither the tier set nor the core bundle.
                string sidecar = WriteSidecar(
                    "{ \"chunks\": {" +
                    "  \"memory.repos.examplevessel.readme\": { \"summary\": \"L.\", \"read_when\": \"when.\" }," +
                    "  \"memory.shared.repository-boundary-and-leak-prevention\": { \"must_retrieve\": [\"examplevessel\"] }" +
                    "} }");
                try
                {
                    ContextIndexGenerator gen = new ContextIndexGenerator();
                    ContextIndex withSidecar = gen.Build(root, null, sidecar);
                    ContextIndex noSidecar = gen.Build(root, null, "/does/not/exist.json");

                    AssertEqual(noSidecar.CoreBundle, withSidecar.CoreBundle);
                    AssertEqual(
                        noSidecar.Chunks.Count(c => c.Tier == ContextTierEnum.Core),
                        withSidecar.Chunks.Count(c => c.Tier == ContextTierEnum.Core));

                    // The named core chunk is still core; metadata never promotes or demotes.
                    ContextChunk boundary = withSidecar.Chunks.Single(x => x.Id == "memory.shared.repository-boundary-and-leak-prevention");
                    AssertEqual(ContextTierEnum.Core, boundary.Tier);
                    return Task.CompletedTask;
                }
                finally { SafeDelete(root); SafeDeleteFile(sidecar); }
            });

            await RunTest("Sidecar_MissingOrMalformed_IsIgnored_NoThrow", () =>
            {
                // Parse of junk yields an empty sidecar; Load of a missing path yields empty.
                AssertEqual(0, ChunkMetadataSidecar.Parse("{ not valid json").Count);
                AssertEqual(0, ChunkMetadataSidecar.Parse("").Count);
                AssertEqual(0, ChunkMetadataSidecar.Load(null).Count);
                AssertEqual(0, ChunkMetadataSidecar.Load("/no/such/sidecar.json").Count);

                // A build pointed at a malformed sidecar equals a build with none.
                string root = CreateFakeMemoryTree();
                string bad = WriteSidecar("{ this is : not json ]");
                try
                {
                    ContextIndexGenerator gen = new ContextIndexGenerator();
                    ContextIndex a = gen.Build(root, null, bad);
                    ContextIndex b = gen.Build(root, null, "/does/not/exist.json");
                    AssertEqual(b.ManifestJson, a.ManifestJson);
                    return Task.CompletedTask;
                }
                finally { SafeDelete(root); SafeDeleteFile(bad); }
            });

            await RunTest("Sidecar_Build_IsDeterministic", () =>
            {
                string root = CreateFakeMemoryTree();
                string sidecar = WriteSidecar(
                    "{ \"chunks\": { \"memory.repos.examplevessel.readme\": { \"summary\": \"D.\", \"read_when\": \"t.\", \"must_retrieve\": [\"examplevessel\"] } } }");
                try
                {
                    ContextIndexGenerator gen = new ContextIndexGenerator();
                    ContextIndex a = gen.Build(root, null, sidecar);
                    ContextIndex b = gen.Build(root, null, sidecar);
                    AssertEqual(a.ManifestJson, b.ManifestJson);
                    AssertEqual(a.CoreBundle, b.CoreBundle);
                    return Task.CompletedTask;
                }
                finally { SafeDelete(root); SafeDeleteFile(sidecar); }
            });

            // ---- Large-leaf sub-chunking: an oversized plain leaf becomes multiple section leaves;
            //      a small one stays single; the core count is unaffected. ----
            await RunTest("Build_LargeLeafFile_SubChunksAtSections_SmallStaysSingle_CoreCountUnchanged", () =>
            {
                string root = NewTempDir("ctxbig");
                Directory.CreateDirectory(Path.Combine(root, "shared"));
                Directory.CreateDirectory(Path.Combine(root, "repos", "bigleaf"));

                // One whole-file-core source, so the core count is a real, non-zero number to compare.
                File.WriteAllText(Path.Combine(root, "shared", "repository-boundary-and-leak-prevention.md"),
                    "# Repository Boundary and Leak Prevention\n\nCore body.\n");
                // A small leaf that must stay one chunk (well under the threshold).
                File.WriteAllText(Path.Combine(root, "repos", "bigleaf", "small.md"),
                    "# Small Leaf\n\n## Only Section\n\nA tiny leaf that stays whole.\n");

                try
                {
                    // Baseline core count WITHOUT the large leaf present.
                    ContextIndexGenerator gen = new ContextIndexGenerator();
                    int coreBefore = gen.Build(root, null).Chunks.Count(c => c.Tier == ContextTierEnum.Core);

                    // A large plain leaf: several ## sections whose bodies together exceed the 8 KB
                    // threshold, but each section is small.
                    string big = "# Big Leaf\n\nPreamble line.\n";
                    for (int i = 1; i <= 4; i++)
                        big += "\n## Section " + i + "\n\n" + new String('x', 3000) + "\n";
                    File.WriteAllText(Path.Combine(root, "repos", "bigleaf", "README.md"), big);
                    AssertTrue(System.Text.Encoding.UTF8.GetByteCount(big) > ContextIndexGenerator.LeafSubChunkThresholdBytes,
                        "the big leaf must exceed the sub-chunk threshold");

                    ContextIndex index = gen.Build(root, null);

                    // The large leaf became several section leaves, each its own chunk under the file path.
                    List<ContextChunk> bigChunks = index.Chunks
                        .Where(c => c.Path.StartsWith("AI-Memory/repos/bigleaf/README.md", StringComparison.Ordinal)).ToList();
                    AssertTrue(bigChunks.Count >= 4, "a large leaf sub-chunks into multiple section leaves (got " + bigChunks.Count + ")");
                    AssertTrue(bigChunks.All(c => c.Tier == ContextTierEnum.Leaf), "every sub-chunk of a leaf file is a leaf");
                    AssertTrue(bigChunks.All(c => System.Text.Encoding.UTF8.GetByteCount(c.Text) <= ContextIndexGenerator.LeafSubChunkThresholdBytes),
                        "each section leaf is at or under the threshold");
                    // The section anchors are distinct and locate a section via a '#' fragment.
                    AssertTrue(bigChunks.Count(c => c.Path.Contains('#')) >= 4, "section leaves carry a heading-anchor path fragment");

                    // The small leaf stayed a single chunk.
                    int smallChunks = index.Chunks.Count(c => c.Path.StartsWith("AI-Memory/repos/bigleaf/small.md", StringComparison.Ordinal));
                    AssertEqual(1, smallChunks);

                    // The core count is unaffected by leaf sub-chunking.
                    int coreAfter = index.Chunks.Count(c => c.Tier == ContextTierEnum.Core);
                    AssertEqual(coreBefore, coreAfter);
                    return Task.CompletedTask;
                }
                finally { SafeDelete(root); }
            });
        }

        // Write a sidecar JSON file to a temp path and return it.
        private static string WriteSidecar(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), "armada_ctxsidecar_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            return path;
        }

        private static void SafeDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best effort */ }
        }

        private static ContextChunk FindByPath(ContextIndex index, string path)
        {
            return index.Chunks.Single(c => String.Equals(c.Path, path, StringComparison.Ordinal));
        }

        // Build a hermetic AI-Memory tree with the exact core paths and headings the config names,
        // plus leaf content, so the generator's tier decisions are exercised end to end.
        private static string CreateFakeMemoryTree()
        {
            string root = NewTempDir("ctxmem");
            Directory.CreateDirectory(Path.Combine(root, "shared"));
            Directory.CreateDirectory(Path.Combine(root, "repos", "armada"));
            Directory.CreateDirectory(Path.Combine(root, "repos", "examplevessel"));
            Directory.CreateDirectory(Path.Combine(root, "machine-notes"));

            File.WriteAllText(Path.Combine(root, "shared", "repository-boundary-and-leak-prevention.md"),
                "# Repository Boundary and Leak Prevention\n\n" + CorePhrase + " content.\n");
            File.WriteAllText(Path.Combine(root, "shared", "land-then-sync.md"),
                "# Land Then Sync\n\nHard limits: never force-push.\n");
            File.WriteAllText(Path.Combine(root, "shared", "unified-project-memory.md"),
                "# Unified Project Memory\n\nIntro.\n\n" +
                "## What belongs here\n\n" + LeafPhrase + " prose.\n\n" +
                "## Reporting style\n\nASD-STE100.\n\n" +
                "## Boundaries\n\nNever write keys. Stop before outward actions.\n\n" +
                "## Proving a fix\n\nReproduce the symptom.\n\n" +
                "## Domain scope\n\nFirmware reflash is banned.\n");
            File.WriteAllText(Path.Combine(root, "shared", "sole-memory-source.md"),
                "# Sole Memory Source\n\nAI-Memory is the sole durable memory source.\n\n" +
                "## Scope decides the folder\n\nLeaf.\n\n" +
                "## How each runtime loads it\n\nUpdate all four loaders in the same commit.\n");
            File.WriteAllText(Path.Combine(root, "repos", "armada", "README.md"),
                "# Armada Memory\n\nIntro.\n\n" +
                "## Where Armada runs\n\nArmada's own repository is direct-edit only.\n\n" +
                "## Deploying Armada on the server\n\nLeaf deploy steps.\n");
            File.WriteAllText(Path.Combine(root, "repos", "armada", "typed-decisions.md"),
                "# Typed Decisions\n\n## Non-negotiables\n\nThe model never approves or lands.\n");
            File.WriteAllText(Path.Combine(root, "repos", "examplevessel", "README.md"),
                "# ExampleVessel Memory\n\nA leaf repo readme.\n");
            File.WriteAllText(Path.Combine(root, "machine-notes", "macos.md"),
                "# macOS Workstation Notes\n\nHost-only notes.\n");
            return root;
        }

        private static string NewTempDir(string prefix)
        {
            string dir = Path.Combine(Path.GetTempPath(), "armada_" + prefix + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void SafeDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { /* best effort */ }
        }
    }
}
