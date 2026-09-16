namespace Armada.Test.Unit.Suites.Context
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Context;
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
            await RunTest("FrontMatter_Parse_ReadsAllFields", () =>
            {
                string doc = "---\n" +
                    "topic: ops.example\n" +
                    "summary: An example chunk.\n" +
                    "read_when: When you need the example.\n" +
                    "applies_to: [orchestrator, persona:Judge]\n" +
                    "tier: leaf\n" +
                    "must_retrieve: [port:eculink]\n" +
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
                AssertEqual("port:eculink", fm.MustRetrieve![0]);
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
                AssertFalse(cfg.IsWholeFileCore("repos/eculink/README.md", out _), "eculink readme is a leaf");
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
            Directory.CreateDirectory(Path.Combine(root, "repos", "eculink"));
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
                "## Domain scope\n\nUDS 0x34 reflash is banned.\n");
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
            File.WriteAllText(Path.Combine(root, "repos", "eculink", "README.md"),
                "# EcuLink Memory\n\nA leaf repo readme.\n");
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
