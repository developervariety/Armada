namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The deterministic tier configuration: the single place that maps the owner-approved core
    /// allowlist to <c>tier: core</c>. Nothing else is core. A model never promotes or demotes a
    /// chunk; only this table does, and only the owner edits it.
    ///
    /// The bar for CORE (owner-approved 2026-09-16): a rule is core only if its ABSENCE on a task
    /// could cause a leak, an unsafe or unauthorized outward action, an unproven success claim, a
    /// destructive remote or git operation, or dispatching Armada work that must be direct-edit.
    ///
    /// v1 maps at two granularities, because the AI-Memory files are not yet sub-chunked into
    /// front-matter files:
    ///   * WHOLE-FILE core, for files that are core in full.
    ///   * SECTION core, for mixed files, by a list of H2/H3 section anchors; only the named sections
    ///     are core and the rest of the file is leaf.
    /// When a source later carries its own front-matter, the front-matter tier wins and this table is
    /// consulted only for a source that has none.
    ///
    /// The approved allowlist (~11 rules). Each core source below cites its allowlist number in the
    /// <see cref="CoreRuleMeta.Order"/> it is given, which is also the core-bundle print order:
    ///   1. Repository boundary and leak-prevention, in full.
    ///   2. Never write keys, seeds, passwords, or tokens into memory or a repository; keys live only
    ///      in the admiral environment. (Inside unified "Boundaries".)
    ///   3. Land-then-sync hard limits. (Whole file in v1; the hard-limits section is its core.)
    ///   4. Stop before shared-state or outward actions; wait for owner authority. (unified "Boundaries".)
    ///   5. Proving a fix: reproduce the symptom; a self-reported success is not evidence.
    ///   6. Domain scope and hard guardrails: the owner's domain work authorized; firmware reflash banned.
    ///   7. Armada is direct-edit only. (repos/armada/README.md "Where Armada runs".)
    ///   8. Typed-decision non-negotiables. (Whole file in v1; the non-negotiables section is its core.)
    ///   9. ASD-STE100 reporting style. (unified "Reporting style".)
    ///  10. Sole-memory-source pointer plus the four-loaders rule.
    ///  11. The generated index/map itself, plus how to retrieve more. (Synthesized by the generator.)
    ///
    /// Allowlist items 2 and 4 both live inside unified "Boundaries", so that one section carries both.
    /// Items 3 and 8 are whole-file in v1: over-including a safety file as core is always safe (the
    /// design's core over-inclusion rule), and this table already supports narrowing them to their
    /// hard-limits and non-negotiables sections later by moving them into the section map.
    /// </summary>
    public sealed class ContextTierConfig
    {
        /// <summary>Metadata attached to a core source: its bundle order and its manifest summary and trigger.</summary>
        public sealed class CoreRuleMeta
        {
            /// <summary>The allowlist order; the deterministic core-bundle print order (ties break by topic).</summary>
            public int Order { get; set; }

            /// <summary>The manifest summary for this core chunk.</summary>
            public string Summary { get; set; } = "";

            /// <summary>The manifest read-when trigger. For core it is always "Always; core rule, never retrieval-gated."</summary>
            public string ReadWhen { get; set; } = "Always. Core rule; never retrieval-gated.";

            /// <summary>Who the chunk applies to.</summary>
            public List<string> AppliesTo { get; set; } = new List<string> { "all" };
        }

        // Memory-root-relative paths, forward-slashed and lower-cased at lookup.
        private readonly Dictionary<string, CoreRuleMeta> _WholeFileCore;
        private readonly Dictionary<string, Dictionary<string, CoreRuleMeta>> _SectionCore;

        /// <summary>The allowlist order given to the synthesized index/map core chunk (allowlist item 11).</summary>
        public const int MapCoreOrder = 11;

        /// <summary>Build the fixed configuration.</summary>
        public ContextTierConfig()
        {
            _WholeFileCore = new Dictionary<string, CoreRuleMeta>(StringComparer.OrdinalIgnoreCase)
            {
                ["shared/repository-boundary-and-leak-prevention.md"] = new CoreRuleMeta
                {
                    Order = 1,
                    Summary = "What must never reach a repository; the pre-push scan list; the owner approval gate; re-leak surfaces.",
                },
                ["shared/land-then-sync.md"] = new CoreRuleMeta
                {
                    Order = 3,
                    Summary = "Land-then-sync, and its hard limits: never push upstream, never force-push, never auto-push mission branches or PRs.",
                },
                ["repos/armada/typed-decisions.md"] = new CoreRuleMeta
                {
                    Order = 8,
                    Summary = "Typed-decision non-negotiables: the model never approves, lands, dispatches, deletes, or silences; keys stay in env.",
                },
            };

            _SectionCore = new Dictionary<string, Dictionary<string, CoreRuleMeta>>(StringComparer.OrdinalIgnoreCase)
            {
                ["shared/unified-project-memory.md"] = new Dictionary<string, CoreRuleMeta>(StringComparer.OrdinalIgnoreCase)
                {
                    // Allowlist 9.
                    ["reporting-style"] = new CoreRuleMeta
                    {
                        Order = 9,
                        Summary = "ASD-STE100 reporting style for every report and status update to the owner.",
                    },
                    // Allowlist 2 and 4: keys-only-in-env and stop-before-outward-actions both live here.
                    ["boundaries"] = new CoreRuleMeta
                    {
                        Order = 4,
                        Summary = "Never write key, seed, password, or RSA bytes into memory; stop before shared-state or outward actions the owner did not ask for.",
                    },
                    // Allowlist 5.
                    ["proving-a-fix"] = new CoreRuleMeta
                    {
                        Order = 5,
                        Summary = "A report, commit message, or passing unrelated suite is not evidence; reproduce the symptom and show it stops.",
                    },
                    // Allowlist 6.
                    ["domain-scope"] = new CoreRuleMeta
                    {
                        Order = 6,
                        Summary = "The owner's domain work is authorized engineering; the hard guardrail is that a firmware download or reflash request is banned.",
                    },
                },
                ["shared/sole-memory-source.md"] = new Dictionary<string, CoreRuleMeta>(StringComparer.OrdinalIgnoreCase)
                {
                    // Allowlist 10, first half: the "sole memory source" pointer (the file preamble under the H1).
                    ["sole-memory-source"] = new CoreRuleMeta
                    {
                        Order = 10,
                        Summary = "AI-Memory is the sole durable memory source for every tool; do not create a second store.",
                    },
                    // Allowlist 10, second half: the four-loaders rule.
                    ["how-each-runtime-loads-it"] = new CoreRuleMeta
                    {
                        Order = 10,
                        Summary = "When a memory file is added or removed, update INDEX.md, CLAUDE.md, AGENTS.md, and opencode.json in the same commit.",
                    },
                },
                ["repos/armada/README.md"] = new Dictionary<string, CoreRuleMeta>(StringComparer.OrdinalIgnoreCase)
                {
                    // Allowlist 7.
                    ["where-armada-runs"] = new CoreRuleMeta
                    {
                        Order = 7,
                        Summary = "Armada's own repository is direct-edit only; never dispatch Armada voyages or rescues for Armada bugs.",
                    },
                },
            };
        }

        /// <summary>True when the whole file at this memory-root-relative path is core.</summary>
        public bool IsWholeFileCore(string relativePath, out CoreRuleMeta meta)
        {
            return _WholeFileCore.TryGetValue(Normalize(relativePath), out meta!);
        }

        /// <summary>True when this file is section-anchored (mixed): some sections core, the rest leaf.</summary>
        public bool IsSectionAnchored(string relativePath)
        {
            return _SectionCore.ContainsKey(Normalize(relativePath));
        }

        /// <summary>
        /// True when the section with this heading text, in this file, is core. The heading text is
        /// slugified before lookup, so "Proving a fix" matches the anchor "proving-a-fix".
        /// </summary>
        public bool IsSectionCore(string relativePath, string headingText, out CoreRuleMeta meta)
        {
            meta = null!;
            if (!_SectionCore.TryGetValue(Normalize(relativePath), out Dictionary<string, CoreRuleMeta>? anchors)) return false;
            return anchors.TryGetValue(Slug(headingText), out meta!);
        }

        /// <summary>Normalize a path for lookup: forward slashes, trimmed, lower-cased.</summary>
        public static string Normalize(string path)
        {
            if (String.IsNullOrEmpty(path)) return "";
            return path.Replace('\\', '/').Trim().TrimStart('/');
        }

        /// <summary>
        /// Slugify a heading into an anchor: lower-case, every run of non-alphanumeric characters to a
        /// single hyphen, trimmed. Deterministic and dependency-free.
        /// </summary>
        public static string Slug(string text)
        {
            if (String.IsNullOrEmpty(text)) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(text.Length);
            bool lastHyphen = false;
            foreach (char c in text.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                    lastHyphen = false;
                }
                else
                {
                    if (!lastHyphen && sb.Length > 0) sb.Append('-');
                    lastHyphen = true;
                }
            }
            string slug = sb.ToString();
            return slug.TrimEnd('-');
        }
    }
}
