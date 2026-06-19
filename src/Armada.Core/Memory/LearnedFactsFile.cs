namespace Armada.Core.Memory
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    /// <summary>
    /// Helpers for the canonical per-vessel learned-facts file stored in the repository.
    /// </summary>
    public static class LearnedFactsFile
    {
        #region Public-Members

        /// <summary>
        /// Relative path from the repository root to the canonical learned-facts file.
        /// </summary>
        public const string RelativePath = ".armada/LEARNED.md";

        /// <summary>
        /// Marker captains must emit to propose a change to the protected learned-facts file
        /// instead of editing it directly.
        /// </summary>
        public const string ProposalMarker = "[LEARNED-FACT-PROPOSAL]";

        /// <summary>
        /// Legacy empty-state content used before the in-dock discovery pointer was introduced.
        /// </summary>
        public const string LegacyTemplateContent = "# Vessel Learned Facts\n\nNo accepted reflection facts yet.";

        /// <summary>
        /// Current empty-state content that includes the canonical path and propose-not-edit pointer.
        /// </summary>
        public const string DefaultTemplateContent = "# Vessel Learned Facts\n\nNo accepted reflection facts yet.\n\nThe canonical source of truth for this vessel is `.armada/LEARNED.md` in the repository root.\nCaptains must PROPOSE changes via `[LEARNED-FACT-PROPOSAL]` and never edit that file directly.";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Reads the canonical learned-facts file from the repository root.
        /// Returns null when the file is missing or contains only the empty-state template.
        /// </summary>
        /// <param name="repoRoot">Repository root directory.</param>
        /// <returns>File content, or null when the file is absent or template-only.</returns>
        public static async Task<string?> ReadAsync(string repoRoot)
        {
            if (String.IsNullOrWhiteSpace(repoRoot))
                return null;

            string path = Path.Combine(repoRoot, RelativePath);
            if (!File.Exists(path))
                return null;

            string content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            string normalized = content.Trim();

            if (String.Equals(normalized, LegacyTemplateContent, StringComparison.Ordinal)
                || String.Equals(normalized, DefaultTemplateContent, StringComparison.Ordinal))
            {
                return null;
            }

            return content;
        }

        /// <summary>
        /// Extracts captain-authored learned-fact proposals from mission output.
        /// Supports inline marker text and a contiguous block immediately following the marker.
        /// </summary>
        /// <param name="content">Mission output to scan.</param>
        /// <returns>Trimmed proposal bodies in source order.</returns>
        public static List<string> ExtractProposals(string? content)
        {
            List<string> proposals = new List<string>();
            if (String.IsNullOrWhiteSpace(content))
                return proposals;

            string[] lines = NormalizeLineEndings(content).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int markerIndex = line.IndexOf(ProposalMarker, StringComparison.Ordinal);
                if (markerIndex < 0) continue;

                string inline = line.Substring(markerIndex + ProposalMarker.Length).Trim();
                if (!String.IsNullOrWhiteSpace(inline))
                {
                    proposals.Add(inline);
                    continue;
                }

                StringBuilder block = new StringBuilder();
                int j = i + 1;
                while (j < lines.Length)
                {
                    string next = lines[j];
                    if (next.IndexOf(ProposalMarker, StringComparison.Ordinal) >= 0)
                    {
                        j--;
                        break;
                    }

                    if (String.IsNullOrWhiteSpace(next))
                        break;

                    block.AppendLine(next);
                    j++;
                }

                string proposal = block.ToString().Trim();
                if (!String.IsNullOrWhiteSpace(proposal))
                    proposals.Add(proposal);

                i = j;
            }

            return proposals;
        }

        /// <summary>
        /// Appends or merges a learned fact into the canonical learned-facts file.
        /// The file is written with LF line endings and UTF-8 without a BOM.
        /// </summary>
        /// <param name="repoRoot">Repository root directory.</param>
        /// <param name="contentToApply">Learned-fact markdown to append or merge.</param>
        /// <returns>Result indicating success or the reason the land failed.</returns>
        public static async Task<LearnedFactsFileApplyResult> ApplyAsync(string repoRoot, string contentToApply)
        {
            return await ApplyAsync(repoRoot, contentToApply, null).ConfigureAwait(false);
        }

        /// <summary>
        /// Appends or merges a learned fact into the canonical learned-facts file and optionally
        /// prunes the result so the file stays bounded. The file is written with LF line endings
        /// and UTF-8 without a BOM.
        /// </summary>
        /// <param name="repoRoot">Repository root directory.</param>
        /// <param name="contentToApply">Learned-fact markdown to append or merge.</param>
        /// <param name="pruneOptions">Optional prune configuration applied after the new content is merged.</param>
        /// <returns>Result indicating success or the reason the land failed.</returns>
        public static async Task<LearnedFactsFileApplyResult> ApplyAsync(
            string repoRoot,
            string contentToApply,
            LearnedFactsPruneOptions? pruneOptions)
        {
            if (String.IsNullOrWhiteSpace(repoRoot))
            {
                return new LearnedFactsFileApplyResult
                {
                    Success = false,
                    Error = "repo_root_missing"
                };
            }

            if (String.IsNullOrWhiteSpace(contentToApply))
            {
                return new LearnedFactsFileApplyResult
                {
                    Success = false,
                    Error = "content_missing"
                };
            }

            try
            {
                if (!Directory.Exists(repoRoot))
                {
                    return new LearnedFactsFileApplyResult
                    {
                        Success = false,
                        Error = "repo_root_not_found"
                    };
                }

                string dir = Path.Combine(repoRoot, ".armada");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "LEARNED.md");

                string? existing = await ReadAsync(repoRoot).ConfigureAwait(false);
                string normalizedNew = NormalizeLineEndings(contentToApply).Trim();

                string finalContent;
                if (String.IsNullOrEmpty(existing))
                {
                    finalContent = normalizedNew;
                }
                else
                {
                    string normalizedExisting = NormalizeLineEndings(existing).Trim();
                    if (normalizedExisting.Contains(normalizedNew, StringComparison.Ordinal))
                    {
                        return new LearnedFactsFileApplyResult { Success = true };
                    }

                    finalContent = normalizedExisting + "\n\n" + normalizedNew;
                }

                finalContent = NormalizeLineEndings(finalContent);

                LearnedFactsPruneResult? pruneResult = null;
                if (pruneOptions != null && pruneOptions.Enabled)
                {
                    pruneResult = PruneContent(finalContent, pruneOptions);
                    if (pruneResult.Success)
                    {
                        finalContent = pruneResult.PrunedContent ?? finalContent;
                    }
                }

                await File.WriteAllTextAsync(path, finalContent, new UTF8Encoding(false)).ConfigureAwait(false);
                return new LearnedFactsFileApplyResult
                {
                    Success = true,
                    PrunedRemovedCount = pruneResult?.RemovedCount ?? 0,
                    PrunedMergedCount = pruneResult?.MergedCount ?? 0
                };
            }
            catch (Exception ex)
            {
                return new LearnedFactsFileApplyResult
                {
                    Success = false,
                    Error = "apply_failed: " + ex.Message
                };
            }
        }

        /// <summary>
        /// Prunes the canonical learned-facts file in place using the supplied options.
        /// Returns a result describing what changed without writing anything when the file is absent.
        /// </summary>
        /// <param name="repoRoot">Repository root directory.</param>
        /// <param name="pruneOptions">Prune configuration.</param>
        /// <returns>Result describing the prune outcome.</returns>
        public static async Task<LearnedFactsPruneResult> PruneAsync(string repoRoot, LearnedFactsPruneOptions pruneOptions)
        {
            if (String.IsNullOrWhiteSpace(repoRoot))
            {
                return new LearnedFactsPruneResult
                {
                    Success = false,
                    Error = "repo_root_missing"
                };
            }

            if (pruneOptions == null)
            {
                return new LearnedFactsPruneResult
                {
                    Success = false,
                    Error = "prune_options_missing"
                };
            }

            if (!pruneOptions.Enabled)
            {
                return new LearnedFactsPruneResult
                {
                    Success = true,
                    Changed = false
                };
            }

            try
            {
                string path = Path.Combine(repoRoot, RelativePath);
                if (!File.Exists(path))
                {
                    return new LearnedFactsPruneResult
                    {
                        Success = true,
                        Changed = false
                    };
                }

                string content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                LearnedFactsPruneResult result = PruneContent(content, pruneOptions);
                if (result.Success && result.Changed)
                {
                    await File.WriteAllTextAsync(path, result.PrunedContent ?? content, new UTF8Encoding(false)).ConfigureAwait(false);
                }

                return result;
            }
            catch (Exception ex)
            {
                return new LearnedFactsPruneResult
                {
                    Success = false,
                    Error = "prune_failed: " + ex.Message
                };
            }
        }

        #endregion

        #region Private-Methods

        private static string NormalizeLineEndings(string content)
        {
            if (String.IsNullOrEmpty(content))
                return content;

            return content.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static LearnedFactsPruneResult PruneContent(string content, LearnedFactsPruneOptions options)
        {
            List<LearnedFactsEntry> entries = ParseEntries(content);
            List<LearnedFactsEntry> kept = new List<LearnedFactsEntry>(entries);
            List<string> removed = new List<string>();
            List<string> merged = new List<string>();

            // Dedupe near-duplicate entries before applying the count cap so the cap operates
            // on distinct durable knowledge rather than duplicate restatements.
            DedupeEntries(kept, options.DedupeSimilarityThreshold, merged);

            // Count cap: drop oldest entries, but prefer keeping higher-confidence facts.
            if (options.MaxEntries > 0 && kept.Count > options.MaxEntries)
            {
                // Stable ordering: confidence descending, then original order ascending.
                List<LearnedFactsEntry> ranked = kept
                    .OrderByDescending(e => ConfidenceRank(e.Confidence))
                    .ThenBy(e => e.OriginalIndex)
                    .ToList();

                List<LearnedFactsEntry> survivors = ranked.Take(options.MaxEntries).ToList();
                foreach (LearnedFactsEntry entry in kept)
                {
                    if (!survivors.Contains(entry))
                    {
                        removed.Add(entry.Body);
                    }
                }

                kept = survivors.OrderBy(e => e.OriginalIndex).ToList();
            }

            string prunedContent = FormatEntries(content, kept);
            bool changed = !String.Equals(
                NormalizeLineEndings(content).Trim(),
                NormalizeLineEndings(prunedContent).Trim(),
                StringComparison.Ordinal);

            return new LearnedFactsPruneResult
            {
                Success = true,
                Changed = changed,
                PrunedContent = prunedContent,
                RemovedCount = removed.Count,
                MergedCount = merged.Count,
                RemovedEntries = removed,
                MergedEntries = merged
            };
        }

        private static List<LearnedFactsEntry> ParseEntries(string content)
        {
            List<LearnedFactsEntry> entries = new List<LearnedFactsEntry>();
            if (String.IsNullOrWhiteSpace(content))
                return entries;

            string[] lines = NormalizeLineEndings(content).Split('\n');
            int index = 0;
            while (index < lines.Length)
            {
                string trimmed = lines[index].TrimStart();
                Match tagMatch = Regex.Match(trimmed, @"^\[(?<c>high|medium|low)\]", RegexOptions.IgnoreCase);
                if (!tagMatch.Success)
                {
                    index++;
                    continue;
                }

                int startIndex = index;
                string confidence = tagMatch.Groups["c"].Value.ToLowerInvariant();
                StringBuilder body = new StringBuilder();
                body.AppendLine(trimmed);

                int next = index + 1;
                while (next < lines.Length)
                {
                    string peek = lines[next];
                    string peekTrim = peek.TrimStart();
                    if (String.IsNullOrWhiteSpace(peek)) break;
                    if (peekTrim.StartsWith("#", StringComparison.Ordinal)) break;
                    if (Regex.IsMatch(peekTrim, @"^\[(high|medium|low)\]", RegexOptions.IgnoreCase)) break;
                    body.AppendLine(peek);
                    next++;
                }

                LearnedFactsEntry entry = new LearnedFactsEntry
                {
                    StartIndex = startIndex,
                    EndIndex = next - 1,
                    OriginalIndex = startIndex,
                    Confidence = confidence,
                    Body = body.ToString().Trim()
                };
                entries.Add(entry);
                index = next;
            }

            return entries;
        }

        private static void DedupeEntries(
            List<LearnedFactsEntry> entries,
            double threshold,
            List<string> mergedDescriptions)
        {
            if (entries.Count < 2)
                return;

            // Process in original order so the first occurrence of a pattern is retained.
            for (int i = 0; i < entries.Count; i++)
            {
                LearnedFactsEntry current = entries[i];
                if (current.Removed)
                    continue;

                for (int j = i + 1; j < entries.Count; j++)
                {
                    LearnedFactsEntry candidate = entries[j];
                    if (candidate.Removed)
                        continue;

            double similarity = Armada.Core.Memory.HabitPatternMiner.Jaccard3GramSimilarity(current.Body, candidate.Body);
            if (similarity < threshold)
                continue;

            // Never merge entries that contradict each other sentiment-wise.
            if (Armada.Core.Memory.HabitPatternMiner.SentimentDisagrees(current.Body, candidate.Body))
                continue;

            candidate.Removed = true;
            mergedDescriptions.Add(candidate.Body);
                }
            }

            entries.RemoveAll(e => e.Removed);
        }

        private static int ConfidenceRank(string confidence)
        {
            return String.Equals(confidence, "high", StringComparison.OrdinalIgnoreCase) ? 2
                : String.Equals(confidence, "medium", StringComparison.OrdinalIgnoreCase) ? 1
                : 0;
        }

        private static string FormatEntries(string originalContent, List<LearnedFactsEntry> keptEntries)
        {
            if (keptEntries.Count == 0)
                return NormalizeLineEndings(originalContent).Trim();

            string[] lines = NormalizeLineEndings(originalContent).Split('\n');
            HashSet<int> keepLines = new HashSet<int>();
            foreach (LearnedFactsEntry entry in keptEntries)
            {
                for (int i = entry.StartIndex; i <= entry.EndIndex; i++)
                    keepLines.Add(i);
            }

            StringBuilder output = new StringBuilder();
            bool lastKeptWasBlank = true;
            int index = 0;
            while (index < lines.Length)
            {
                string trimmed = lines[index].TrimStart();
                bool isEntryStart = Regex.IsMatch(trimmed, @"^\[(high|medium|low)\]", RegexOptions.IgnoreCase);

                if (isEntryStart && !keepLines.Contains(index))
                {
                    // Skip the entire removed entry.
                    index++;
                    while (index < lines.Length)
                    {
                        string peek = lines[index];
                        string peekTrim = peek.TrimStart();
                        if (String.IsNullOrWhiteSpace(peek)
                            || peekTrim.StartsWith("#", StringComparison.Ordinal)
                            || Regex.IsMatch(peekTrim, @"^\[(high|medium|low)\]", RegexOptions.IgnoreCase))
                        {
                            break;
                        }

                        index++;
                    }

                    continue;
                }

                bool keep = keepLines.Contains(index) || !isEntryStart;
                if (!keep)
                {
                    index++;
                    continue;
                }

                bool isBlank = String.IsNullOrWhiteSpace(lines[index]);
                if (isBlank && lastKeptWasBlank)
                {
                    index++;
                    continue;
                }

                output.AppendLine(lines[index]);
                lastKeptWasBlank = isBlank;
                index++;
            }

            return NormalizeLineEndings(output.ToString()).Trim();
        }

        #endregion

        #region Private-Classes

        private sealed class LearnedFactsEntry
        {
            public int StartIndex { get; set; }
            public int EndIndex { get; set; }
            public int OriginalIndex { get; set; }
            public string Confidence { get; set; } = "";
            public string Body { get; set; } = "";
            public bool Removed { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Result of applying a learned fact to the canonical per-vessel learned-facts file.
    /// </summary>
    public sealed class LearnedFactsFileApplyResult
    {
        /// <summary>
        /// Gets or sets a value indicating whether the file land succeeded.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets an error code or message when the land failed.
        /// </summary>
        public string? Error { get; set; }

        /// <summary>
        /// Gets or sets the number of entries removed by post-apply pruning.
        /// </summary>
        public int PrunedRemovedCount { get; set; }

        /// <summary>
        /// Gets or sets the number of entries merged by post-apply deduplication.
        /// </summary>
        public int PrunedMergedCount { get; set; }
    }
}
