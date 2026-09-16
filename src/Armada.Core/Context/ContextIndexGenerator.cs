namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using SyslogLogging;

    /// <summary>
    /// Generates the context index: a machine-readable manifest over every AI-Memory and Armada docs
    /// chunk, plus a derived core bundle (the concatenation of every <c>tier: core</c> chunk). It only
    /// READS the sources; it writes only its two artifacts, into the admiral data directory. AI-Memory
    /// is authoritative and untouched; the manifest is a presentation and retrieval layer, never a
    /// second copy of a rule.
    ///
    /// Output is deterministic: text is normalized to LF, chunks are stably ordered, and neither file
    /// carries a timestamp or a host-absolute path, so two runs over the same input are byte-identical.
    /// Generation is fail-open: a single unreadable file is skipped, and the startup caller wraps the
    /// whole run so a failure logs a warning and never breaks startup.
    /// </summary>
    public sealed class ContextIndexGenerator
    {
        #region Public-Members

        /// <summary>The subdirectories of the AI-Memory root that this generator indexes.</summary>
        public static readonly string[] MemorySubdirectories = new[] { "shared", "repos", "machine-notes" };

        /// <summary>The core bundle file name written beside the manifest.</summary>
        public const string CoreBundleFileName = "context-core.md";

        /// <summary>The manifest file name.</summary>
        public const string ManifestFileName = "manifest.json";

        #endregion

        #region Private-Members

        private const string _Header = "[ContextIndexGenerator] ";
        private readonly LoggingModule? _Logging;
        private readonly ContextTierConfig _Tier = new ContextTierConfig();

        private static readonly JsonSerializerOptions _JsonOptions = BuildJsonOptions();

        private static JsonSerializerOptions BuildJsonOptions()
        {
            JsonSerializerOptions options = new JsonSerializerOptions { WriteIndented = true };
            // Serialize the tier as lower-case "core"/"leaf", the design's names.
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return options;
        }

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create a generator. Logging is optional; when null, per-file skips are silent.</summary>
        public ContextIndexGenerator(LoggingModule? logging = null)
        {
            _Logging = logging;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the index in memory from the given roots, without writing anything. Either root may be
        /// null or missing; a missing root simply contributes no chunks. Never throws.
        /// </summary>
        /// <param name="aiMemoryRoot">The AI-Memory root path, or null to index no memory.</param>
        /// <param name="docsRoot">The Armada docs directory path, or null to index no docs.</param>
        /// <param name="chunkMetadataPath">
        /// An explicit path to the chunk-metadata sidecar, or null to resolve it under the docs root
        /// (<c>context-index/chunk-metadata.json</c>). The sidecar is optional; a missing one changes
        /// nothing.
        /// </param>
        public ContextIndex Build(string? aiMemoryRoot, string? docsRoot, string? chunkMetadataPath = null)
        {
            List<ContextChunk> chunks = new List<ContextChunk>();

            if (!String.IsNullOrWhiteSpace(aiMemoryRoot) && Directory.Exists(aiMemoryRoot))
            {
                foreach ((string absPath, string relPath) in EnumerateMemoryFiles(aiMemoryRoot!))
                {
                    chunks.AddRange(ProcessFile(absPath, "AI-Memory", relPath));
                }
            }

            if (!String.IsNullOrWhiteSpace(docsRoot) && Directory.Exists(docsRoot))
            {
                foreach ((string absPath, string relPath) in EnumerateDocsFiles(docsRoot!))
                {
                    chunks.AddRange(ProcessFile(absPath, "docs", relPath));
                }
            }

            // The synthesized index/map core chunk (allowlist item 11).
            chunks.Add(BuildMapChunk());

            // Merge the optional repository-versioned sidecar over the auto-derived metadata. The
            // sidecar wins where a field is present; the auto-derived value fills every gap. AI-Memory
            // is never written; this only enriches the index. Merge before topic uniquing so a sidecar
            // key matches the chunk's natural id.
            ApplySidecar(chunks, LoadSidecar(chunkMetadataPath, docsRoot));

            // Deterministic ordering and unique topics.
            EnsureUniqueTopics(chunks);
            chunks.Sort((a, b) => String.CompareOrdinal(a.Topic, b.Topic));

            ContextIndex index = new ContextIndex { Chunks = chunks };
            index.Manifest = BuildManifest(chunks);
            index.ManifestJson = JsonSerializer.Serialize(index.Manifest, _JsonOptions).Replace("\r\n", "\n");
            index.CoreBundle = BuildCoreBundle(chunks);
            return index;
        }

        /// <summary>
        /// Build the index and write the manifest and core bundle into <paramref name="outputDirectory"/>.
        /// Fail-open: a write failure is reported in the summary, not thrown. Returns a summary the caller
        /// logs.
        /// </summary>
        public ContextIndexGenerationSummary Generate(string? aiMemoryRoot, string? docsRoot, string outputDirectory, string? chunkMetadataPath = null)
        {
            ContextIndex index = Build(aiMemoryRoot, docsRoot, chunkMetadataPath);

            ContextIndexGenerationSummary summary = new ContextIndexGenerationSummary
            {
                ChunkCount = index.Chunks.Count,
                CoreCount = index.Chunks.Count(c => c.Tier == ContextTierEnum.Core),
                CoreBundleBytes = Encoding.UTF8.GetByteCount(index.CoreBundle),
                TotalChunkBytes = index.Chunks.Sum(c => c.Bytes)
            };

            bool hadMemory = !String.IsNullOrWhiteSpace(aiMemoryRoot) && Directory.Exists(aiMemoryRoot);
            bool hadDocs = !String.IsNullOrWhiteSpace(docsRoot) && Directory.Exists(docsRoot);
            if (!hadMemory) summary.Note = "AI-Memory root missing; core bundle may be incomplete";
            else if (!hadDocs) summary.Note = "docs root missing; docs leaves not indexed";

            try
            {
                Directory.CreateDirectory(outputDirectory);
                string manifestPath = Path.Combine(outputDirectory, ManifestFileName);
                string bundlePath = Path.Combine(outputDirectory, CoreBundleFileName);
                File.WriteAllText(manifestPath, index.ManifestJson, new UTF8Encoding(false));
                File.WriteAllText(bundlePath, index.CoreBundle, new UTF8Encoding(false));
                summary.Written = true;
                summary.ManifestPath = manifestPath;
                summary.CoreBundlePath = bundlePath;
            }
            catch (Exception ex)
            {
                summary.Written = false;
                summary.Note = "write failed: " + ex.Message;
                _Logging?.Warn(_Header + "write failed: " + ex.Message);
            }

            return summary;
        }

        #endregion

        #region Private-Methods

        // Only shared/, repos/, and machine-notes/ under the memory root, .md only, sorted, no AppleDouble sidecars.
        private IEnumerable<(string absPath, string relPath)> EnumerateMemoryFiles(string memoryRoot)
        {
            List<(string, string)> results = new List<(string, string)>();
            foreach (string sub in MemorySubdirectories)
            {
                string subDir = Path.Combine(memoryRoot, sub);
                if (!Directory.Exists(subDir)) continue;
                foreach (string abs in SafeEnumerateMarkdown(subDir))
                {
                    string rel = ToRelative(memoryRoot, abs);
                    results.Add((abs, rel));
                }
            }
            results.Sort((a, b) => String.CompareOrdinal(a.Item2, b.Item2));
            return results;
        }

        private IEnumerable<(string absPath, string relPath)> EnumerateDocsFiles(string docsRoot)
        {
            List<(string, string)> results = new List<(string, string)>();
            foreach (string abs in SafeEnumerateMarkdown(docsRoot))
            {
                string rel = ToRelative(docsRoot, abs);
                results.Add((abs, rel));
            }
            results.Sort((a, b) => String.CompareOrdinal(a.Item2, b.Item2));
            return results;
        }

        private IEnumerable<string> SafeEnumerateMarkdown(string root)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                _Logging?.Warn(_Header + "enumerate failed under a source root: " + ex.Message);
                yield break;
            }

            foreach (string f in files)
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith("._", StringComparison.Ordinal)) continue; // macOS AppleDouble sidecar.
                yield return f;
            }
        }

        private static string ToRelative(string root, string abs)
        {
            string rel = Path.GetRelativePath(root, abs);
            return rel.Replace('\\', '/');
        }

        // Turn one source file into one or more chunks.
        private List<ContextChunk> ProcessFile(string absPath, string logicalRootPrefix, string relPath)
        {
            string raw;
            try
            {
                raw = File.ReadAllText(absPath).Replace("\r\n", "\n");
            }
            catch (Exception ex)
            {
                _Logging?.Warn(_Header + "read failed, skipping " + logicalRootPrefix + "/" + relPath + ": " + ex.Message);
                return new List<ContextChunk>();
            }

            string logicalPath = logicalRootPrefix + "/" + relPath;
            string baseTopic = BuildBaseTopic(logicalRootPrefix, relPath);

            ContextChunkFrontMatter fm = ContextChunkFrontMatter.Parse(raw);

            // 1. A source that carries its own front-matter is one chunk; the front-matter wins, and
            //    the tier configuration is the fallback only for a field front-matter omits.
            if (fm.HasFrontMatter)
            {
                return new List<ContextChunk> { BuildFrontMatterChunk(fm, baseTopic, logicalPath, relPath) };
            }

            // 2. Whole-file core (memory only; docs are never on the core allowlist in v1).
            if (String.Equals(logicalRootPrefix, "AI-Memory", StringComparison.Ordinal) &&
                _Tier.IsWholeFileCore(relPath, out ContextTierConfig.CoreRuleMeta coreMeta))
            {
                return new List<ContextChunk> { BuildWholeFileChunk(raw, baseTopic, logicalPath, ContextTierEnum.Core, coreMeta) };
            }

            // 3. Section-anchored (mixed) file: split into sections; named sections are core, the rest leaf.
            if (String.Equals(logicalRootPrefix, "AI-Memory", StringComparison.Ordinal) &&
                _Tier.IsSectionAnchored(relPath))
            {
                return BuildSectionChunks(raw, baseTopic, logicalPath, relPath);
            }

            // 4. Default: one whole-file leaf.
            return new List<ContextChunk> { BuildWholeFileChunk(raw, baseTopic, logicalPath, ContextTierEnum.Leaf, null) };
        }

        private ContextChunk BuildFrontMatterChunk(ContextChunkFrontMatter fm, string baseTopic, string logicalPath, string relPath)
        {
            ContextTierEnum tier = fm.Tier ?? ResolveFallbackTier(relPath, "", out _);
            return new ContextChunk
            {
                Topic = String.IsNullOrWhiteSpace(fm.Topic) ? baseTopic : fm.Topic!,
                Path = logicalPath,
                Summary = fm.Summary ?? FirstHeadingOrLine(fm.Body),
                ReadWhen = fm.ReadWhen ?? "",
                AppliesTo = fm.AppliesTo != null && fm.AppliesTo.Count > 0 ? fm.AppliesTo : new List<string> { "all" },
                Tier = tier,
                MustRetrieve = fm.MustRetrieve ?? new List<string>(),
                Text = fm.Body,
                CoreOrder = int.MaxValue
            };
        }

        private ContextChunk BuildWholeFileChunk(string text, string topic, string logicalPath, ContextTierEnum tier, ContextTierConfig.CoreRuleMeta? meta)
        {
            return new ContextChunk
            {
                Topic = topic,
                Path = logicalPath,
                Summary = meta?.Summary ?? FirstHeadingOrLine(text),
                ReadWhen = meta?.ReadWhen ?? "",
                AppliesTo = meta?.AppliesTo ?? new List<string> { "all" },
                Tier = tier,
                Text = text,
                CoreOrder = meta?.Order ?? int.MaxValue
            };
        }

        private List<ContextChunk> BuildSectionChunks(string text, string baseTopic, string logicalPath, string relPath)
        {
            List<ContextChunk> chunks = new List<ContextChunk>();
            foreach ((string heading, string sectionText) in SplitSections(text))
            {
                string anchor = ContextTierConfig.Slug(heading);
                string lastSegment = baseTopic.Substring(baseTopic.LastIndexOf('.') + 1);
                string topic = String.Equals(anchor, lastSegment, StringComparison.Ordinal) || anchor.Length == 0
                    ? baseTopic
                    : baseTopic + "." + anchor;
                string sectionPath = anchor.Length == 0 ? logicalPath : logicalPath + "#" + anchor;

                bool isCore = _Tier.IsSectionCore(relPath, heading, out ContextTierConfig.CoreRuleMeta coreMeta);
                chunks.Add(new ContextChunk
                {
                    Topic = topic,
                    Path = sectionPath,
                    Summary = isCore ? coreMeta.Summary : heading,
                    ReadWhen = isCore ? coreMeta.ReadWhen : "",
                    AppliesTo = isCore ? coreMeta.AppliesTo : new List<string> { "all" },
                    Tier = isCore ? ContextTierEnum.Core : ContextTierEnum.Leaf,
                    Text = sectionText,
                    CoreOrder = isCore ? coreMeta.Order : int.MaxValue
                });
            }
            return chunks;
        }

        // Split a markdown document at H2 (## ) boundaries, fence-aware. The content before the first
        // H2 (the H1 title and any preamble) is one section whose heading is the H1 title text. An H2
        // section runs until the next H2 or EOF, and includes any nested H3.
        private static IEnumerable<(string heading, string text)> SplitSections(string text)
        {
            string[] lines = text.Split('\n');
            List<(string, string)> sections = new List<(string, string)>();

            string currentHeading = FindH1(lines);
            StringBuilder current = new StringBuilder();
            bool inFence = false;

            foreach (string line in lines)
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                }

                bool isH2 = !inFence && line.StartsWith("## ", StringComparison.Ordinal);
                if (isH2)
                {
                    sections.Add((currentHeading, current.ToString()));
                    current.Clear();
                    currentHeading = line.Substring(3).Trim();
                }
                current.Append(line).Append('\n');
            }
            sections.Add((currentHeading, current.ToString()));
            return sections;
        }

        private static string FindH1(string[] lines)
        {
            foreach (string line in lines)
            {
                if (line.StartsWith("# ", StringComparison.Ordinal)) return line.Substring(2).Trim();
            }
            return "";
        }

        private ContextChunk BuildMapChunk()
        {
            string text =
                "# Context Index — Map And Retrieval\n\n" +
                "This always-on core bundle ships inline in every orchestrator session and every captain\n" +
                "brief. Every rule in it is CORE: it applies to every task and is never retrieval-gated.\n" +
                "Read it first and keep it in force.\n\n" +
                "- The full topic map is `manifest.json` beside this bundle. Each entry gives a topic, a\n" +
                "  one-line summary, a read-when trigger, the tier, the byte size, and a logical path.\n" +
                "- To use more than the core, find the topic whose `read_when` matches the task, then read\n" +
                "  the source at its `path` under the AI-Memory root or the Armada docs tree.\n" +
                "- A leaf tagged `must_retrieve` for the task's domain is always included by retrieval,\n" +
                "  even without a keyword match.\n" +
                "- The core is an owner allowlist. A model never widens it. Retrieval only ever adds\n" +
                "  leaves; it never gates a core rule, and a degraded retrieval still returns every core\n" +
                "  chunk.\n";

            return new ContextChunk
            {
                Topic = "core.context-index.map",
                Path = "context-index/" + ManifestFileName,
                Summary = "The context index map and how to retrieve more.",
                ReadWhen = "Always. Core rule; never retrieval-gated.",
                AppliesTo = new List<string> { "all" },
                Tier = ContextTierEnum.Core,
                Text = text,
                CoreOrder = ContextTierConfig.MapCoreOrder
            };
        }

        private ContextTierEnum ResolveFallbackTier(string relPath, string heading, out ContextTierConfig.CoreRuleMeta? meta)
        {
            meta = null;
            if (_Tier.IsWholeFileCore(relPath, out ContextTierConfig.CoreRuleMeta wm)) { meta = wm; return ContextTierEnum.Core; }
            if (!String.IsNullOrEmpty(heading) && _Tier.IsSectionCore(relPath, heading, out ContextTierConfig.CoreRuleMeta sm)) { meta = sm; return ContextTierEnum.Core; }
            return ContextTierEnum.Leaf;
        }

        private static string BuildBaseTopic(string logicalRootPrefix, string relPath)
        {
            string prefix = String.Equals(logicalRootPrefix, "AI-Memory", StringComparison.Ordinal) ? "memory" : "docs";
            string noExt = relPath;
            int dot = noExt.LastIndexOf('.');
            if (dot > noExt.LastIndexOf('/')) noExt = noExt.Substring(0, dot);

            IEnumerable<string> segments = noExt.Split('/')
                .Where(s => s.Length > 0)
                .Select(ContextTierConfig.Slug)
                .Where(s => s.Length > 0);
            return prefix + "." + String.Join(".", segments);
        }

        private static string FirstHeadingOrLine(string text)
        {
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("#", StringComparison.Ordinal)) return t.TrimStart('#').Trim();
                return t.Length > 160 ? t.Substring(0, 160) : t;
            }
            return "";
        }

        private static void EnsureUniqueTopics(List<ContextChunk> chunks)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ContextChunk c in chunks)
            {
                if (seen.Add(c.Topic)) continue;
                int n = 2;
                string candidate;
                do { candidate = c.Topic + "-" + n; n++; } while (!seen.Add(candidate));
                c.Topic = candidate;
            }
        }

        // Load the optional chunk-metadata sidecar. Resolution: an explicit path wins; otherwise the
        // default file under the docs root. Never throws; a missing sidecar yields an empty one.
        private ChunkMetadataSidecar LoadSidecar(string? explicitPath, string? docsRoot)
        {
            string? path = ChunkMetadataSidecar.ResolvePath(explicitPath, docsRoot);
            return ChunkMetadataSidecar.Load(path, _Logging);
        }

        // Merge the sidecar over the auto-derived metadata of each chunk it names. A sidecar field
        // wins only when present and non-empty; every gap keeps the auto-derived value, and a chunk
        // the sidecar does not name is untouched. The tier is never changed here: the sidecar carries
        // no tier, so the core allowlist and the core bundle are unaffected.
        private void ApplySidecar(List<ContextChunk> chunks, ChunkMetadataSidecar sidecar)
        {
            if (sidecar == null || sidecar.Count == 0) return;

            foreach (ContextChunk c in chunks)
            {
                if (!sidecar.TryGet(c.Id, out ChunkMetadataOverride ov)) continue;

                if (!String.IsNullOrWhiteSpace(ov.Summary)) c.Summary = ov.Summary!.Trim();
                if (!String.IsNullOrWhiteSpace(ov.ReadWhen)) c.ReadWhen = ov.ReadWhen!.Trim();
                if (ov.AppliesTo != null && ov.AppliesTo.Count > 0)
                    c.AppliesTo = new List<string>(ov.AppliesTo);
                if (ov.MustRetrieve != null && ov.MustRetrieve.Count > 0)
                    c.MustRetrieve = new List<string>(ov.MustRetrieve);
            }
        }

        private ContextIndexManifest BuildManifest(List<ContextChunk> chunks)
        {
            ContextIndexManifest manifest = new ContextIndexManifest
            {
                ChunkCount = chunks.Count,
                CoreCount = chunks.Count(c => c.Tier == ContextTierEnum.Core),
                CoreBundlePath = CoreBundleFileName
            };
            foreach (ContextChunk c in chunks)
            {
                manifest.Chunks.Add(new ContextManifestEntry
                {
                    Id = c.Id,
                    Topic = c.Topic,
                    Path = c.Path,
                    Summary = c.Summary,
                    ReadWhen = c.ReadWhen,
                    AppliesTo = c.AppliesTo,
                    Tier = c.Tier,
                    MustRetrieve = c.MustRetrieve,
                    Bytes = c.Bytes
                });
            }
            return manifest;
        }

        private string BuildCoreBundle(List<ContextChunk> chunks)
        {
            List<ContextChunk> core = chunks.Where(c => c.Tier == ContextTierEnum.Core).ToList();
            core.Sort((a, b) =>
            {
                int byOrder = a.CoreOrder.CompareTo(b.CoreOrder);
                return byOrder != 0 ? byOrder : String.CompareOrdinal(a.Topic, b.Topic);
            });

            StringBuilder sb = new StringBuilder();
            sb.Append("# Armada Context — Always-On Core Bundle\n\n");
            sb.Append("Generated from AI-Memory and the Armada docs. Do not edit; this file is regenerated.\n");
            sb.Append("Every rule here is CORE: it ships inline in every orchestrator session and every captain\n");
            sb.Append("brief and is never retrieval-gated. The machine index is `manifest.json` beside this file.\n\n");
            sb.Append("Core chunks: ").Append(core.Count).Append(".\n");

            foreach (ContextChunk c in core)
            {
                sb.Append('\n');
                sb.Append("---\n\n");
                sb.Append("## ").Append(c.Topic).Append('\n').Append('\n');
                if (!String.IsNullOrEmpty(c.Summary))
                {
                    sb.Append("> ").Append(c.Summary).Append('\n');
                    sb.Append(">\n");
                    sb.Append("> Source: ").Append(c.Path).Append('\n').Append('\n');
                }
                string body = (c.Text ?? "").TrimEnd('\n');
                sb.Append(body).Append('\n');
            }

            return sb.ToString();
        }

        #endregion
    }
}
