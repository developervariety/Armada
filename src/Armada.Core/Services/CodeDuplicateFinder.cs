namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Finds groups of similar chunks among code index records.
    ///
    /// Two passes feed one grouping. The exact pass pairs chunks whose content is the same after trimming
    /// each line and dropping blank lines; it needs no embeddings. The similarity pass compares every pair
    /// of embedded chunks by cosine similarity and keeps pairs at or above the threshold. Pairs are joined
    /// transitively into groups. The comparison is exhaustive rather than approximate, so a pair above the
    /// threshold is never missed; pair collection stops at a cap and the report says so.
    /// </summary>
    public static class CodeDuplicateFinder
    {
        #region Public-Members

        /// <summary>
        /// Default cap on collected pairs. Past it the report is marked truncated.
        /// </summary>
        public const int DefaultMaxPairs = 200000;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Compare records and build the duplicate report body: coverage, pair and group counts, and groups.
        /// Availability, freshness and warnings about the index itself are the caller's.
        /// </summary>
        /// <param name="records">Index records for one vessel.</param>
        /// <param name="request">Request filters and limits.</param>
        /// <param name="threshold">Resolved similarity threshold.</param>
        /// <param name="minLines">Resolved minimum non-blank lines.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="maxPairs">Cap on collected pairs.</param>
        /// <returns>Report with coverage and groups filled in.</returns>
        public static CodeDuplicateReport Find(
            IReadOnlyList<CodeIndexRecord> records,
            CodeDuplicateRequest request,
            double threshold,
            int minLines,
            CancellationToken token = default,
            int maxPairs = DefaultMaxPairs)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (maxPairs < 1) throw new ArgumentOutOfRangeException(nameof(maxPairs));

            CodeDuplicateReport report = new CodeDuplicateReport
            {
                VesselId = request.VesselId,
                Available = true,
                Threshold = threshold,
                MinLines = minLines
            };
            CodeDuplicateCoverage coverage = report.Coverage;
            coverage.ChunksInIndex = records.Count;

            List<CodeIndexRecord> compared = new List<CodeIndexRecord>();
            List<string> normalized = new List<string>();
            List<int> nonBlankCounts = new List<int>();
            foreach (CodeIndexRecord record in records)
            {
                if (record.IsReferenceOnly) { coverage.SkippedReferenceOnly++; continue; }
                if (!MatchesFilter(record, request)) { coverage.SkippedByFilter++; continue; }
                if (IsExcluded(record.Path, request.ExcludePathFragments)) { coverage.SkippedExcluded++; continue; }

                string normalizedContent = NormalizeContent(record.Content, out int nonBlank);
                if (nonBlank < minLines) { coverage.SkippedTooShort++; continue; }

                compared.Add(record);
                normalized.Add(normalizedContent);
                nonBlankCounts.Add(nonBlank);
                string language = String.IsNullOrWhiteSpace(record.Language) ? "unknown" : record.Language;
                coverage.Languages[language] = coverage.Languages.TryGetValue(language, out int count) ? count + 1 : 1;
            }

            coverage.ChunksCompared = compared.Count;
            coverage.FilesCompared = compared.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            PairSink pairs = new PairSink(maxPairs);
            AddExactPairs(normalized, pairs);

            int dimensions = MostCommonVectorLength(compared);
            List<int> embedded = new List<int>();
            for (int i = 0; i < compared.Count; i++)
            {
                float[]? vector = compared[i].EmbeddingVector;
                if (dimensions > 0 && vector != null && vector.Length == dimensions && HasNonZero(vector)) embedded.Add(i);
            }

            coverage.ChunksWithEmbeddings = embedded.Count;
            coverage.WithoutEmbedding = compared.Count - embedded.Count;
            report.SimilarityCompared = embedded.Count > 1;
            if (report.SimilarityCompared) AddSimilarPairs(compared, normalized, embedded, dimensions, (float)threshold, pairs, token);

            report.PairCount = pairs.Count;
            report.Truncated = pairs.Truncated;
            BuildGroups(compared, normalized, nonBlankCounts, pairs, request, report);
            return report;
        }

        #endregion

        #region Private-Methods

        private static bool MatchesFilter(CodeIndexRecord record, CodeDuplicateRequest request)
        {
            if (!String.IsNullOrWhiteSpace(request.PathPrefix)
                && !record.Path.StartsWith(request.PathPrefix.Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase))
                return false;
            if (!String.IsNullOrWhiteSpace(request.Language)
                && !String.Equals(record.Language, request.Language, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        private static bool IsExcluded(string path, List<string>? fragments)
        {
            if (fragments == null || fragments.Count == 0) return false;
            string normalizedPath = "/" + path.Replace('\\', '/').Trim('/') + "/";
            foreach (string fragment in fragments)
            {
                if (String.IsNullOrWhiteSpace(fragment)) continue;
                if (normalizedPath.Contains(fragment.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static string NormalizeContent(string? content, out int nonBlankLines)
        {
            nonBlankLines = 0;
            if (String.IsNullOrEmpty(content)) return String.Empty;

            List<string> kept = new List<string>();
            foreach (string line in content.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                kept.Add(trimmed);
            }

            nonBlankLines = kept.Count;
            return String.Join("\n", kept);
        }

        private static void AddExactPairs(List<string> normalized, PairSink pairs)
        {
            Dictionary<string, int> firstIndexByContent = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < normalized.Count; i++)
            {
                if (firstIndexByContent.TryGetValue(normalized[i], out int first))
                {
                    pairs.Add(first, i, 1F);
                    continue;
                }

                firstIndexByContent[normalized[i]] = i;
            }
        }

        private static int MostCommonVectorLength(List<CodeIndexRecord> records)
        {
            Dictionary<int, int> counts = new Dictionary<int, int>();
            foreach (CodeIndexRecord record in records)
            {
                int length = record.EmbeddingVector?.Length ?? 0;
                if (length == 0) continue;
                counts[length] = counts.TryGetValue(length, out int count) ? count + 1 : 1;
            }

            return counts.Count == 0 ? 0 : counts.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key).First().Key;
        }

        private static bool HasNonZero(float[] vector)
        {
            foreach (float value in vector)
            {
                if (value != 0F) return true;
            }

            return false;
        }

        private static void AddSimilarPairs(
            List<CodeIndexRecord> compared,
            List<string> normalized,
            List<int> embedded,
            int dimensions,
            float threshold,
            PairSink pairs,
            CancellationToken token)
        {
            int count = embedded.Count;
            float[] flat = new float[count * dimensions];
            for (int e = 0; e < count; e++)
            {
                float[] source = compared[embedded[e]].EmbeddingVector!;
                double norm = 0;
                foreach (float value in source) norm += value * (double)value;
                float scale = (float)(1.0 / Math.Sqrt(norm));
                int offset = e * dimensions;
                for (int d = 0; d < dimensions; d++) flat[offset + d] = source[d] * scale;
            }

            ParallelOptions options = new ParallelOptions { CancellationToken = token };
            Parallel.For(0, count, options, (e, state) =>
            {
                if (pairs.Truncated)
                {
                    state.Stop();
                    return;
                }

                ReadOnlySpan<float> left = new ReadOnlySpan<float>(flat, e * dimensions, dimensions);
                for (int other = e + 1; other < count; other++)
                {
                    ReadOnlySpan<float> right = new ReadOnlySpan<float>(flat, other * dimensions, dimensions);
                    float similarity = Dot(left, right);
                    if (similarity < threshold) continue;

                    // The exact pass already linked identical content, so count that pair once.
                    if (String.Equals(normalized[embedded[e]], normalized[embedded[other]], StringComparison.Ordinal)) continue;
                    if (!pairs.Add(embedded[e], embedded[other], Math.Min(1F, similarity)))
                    {
                        state.Stop();
                        return;
                    }
                }
            });
        }

        private static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        {
            int width = Vector<float>.Count;
            int i = 0;
            float sum = 0F;
            if (Vector.IsHardwareAccelerated && left.Length >= width)
            {
                Vector<float> accumulator = Vector<float>.Zero;
                for (; i <= left.Length - width; i += width)
                {
                    accumulator += new Vector<float>(left.Slice(i, width)) * new Vector<float>(right.Slice(i, width));
                }

                sum = Vector.Sum(accumulator);
            }

            for (; i < left.Length; i++) sum += left[i] * right[i];
            return sum;
        }

        private static void BuildGroups(
            List<CodeIndexRecord> compared,
            List<string> normalized,
            List<int> nonBlankCounts,
            PairSink pairs,
            CodeDuplicateRequest request,
            CodeDuplicateReport report)
        {
            int[] parent = new int[compared.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            List<int> lefts = pairs.Lefts;
            List<int> rights = pairs.Rights;
            List<float> similarities = pairs.Similarities;
            for (int p = 0; p < lefts.Count; p++) Union(parent, lefts[p], rights[p]);

            Dictionary<int, double> maxByRoot = new Dictionary<int, double>();
            Dictionary<int, double> minByRoot = new Dictionary<int, double>();
            for (int p = 0; p < lefts.Count; p++)
            {
                int root = Find(parent, lefts[p]);
                double similarity = similarities[p];
                maxByRoot[root] = maxByRoot.TryGetValue(root, out double max) ? Math.Max(max, similarity) : similarity;
                minByRoot[root] = minByRoot.TryGetValue(root, out double min) ? Math.Min(min, similarity) : similarity;
            }

            Dictionary<int, List<int>> membersByRoot = new Dictionary<int, List<int>>();
            foreach (int root in maxByRoot.Keys) membersByRoot[root] = new List<int>();
            for (int i = 0; i < parent.Length; i++)
            {
                int root = Find(parent, i);
                if (membersByRoot.TryGetValue(root, out List<int>? members)) members.Add(i);
            }

            List<CodeDuplicateGroup> groups = new List<CodeDuplicateGroup>();
            foreach (KeyValuePair<int, List<int>> entry in membersByRoot)
            {
                List<int> members = entry.Value;
                if (members.Count < 2) continue;

                string firstContent = normalized[members[0]];
                groups.Add(new CodeDuplicateGroup
                {
                    MaxPairSimilarity = Math.Round(maxByRoot[entry.Key], 4),
                    MinPairSimilarity = Math.Round(minByRoot[entry.Key], 4),
                    IdenticalContent = members.All(m => String.Equals(normalized[m], firstContent, StringComparison.Ordinal)),
                    Members = members
                        .Select(m => new CodeDuplicateMember
                        {
                            Path = compared[m].Path,
                            StartLine = compared[m].StartLine,
                            EndLine = compared[m].EndLine,
                            Language = compared[m].Language,
                            NonBlankLines = nonBlankCounts[m],
                            Content = request.IncludeContent ? compared[m].Content : null
                        })
                        .OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(m => m.StartLine)
                        .ToList()
                });
            }

            int maxGroups = Math.Clamp(request.MaxGroups, 1, 500);
            report.GroupCount = groups.Count;
            report.Groups = groups
                .OrderByDescending(g => g.Members.Count)
                .ThenByDescending(g => g.MaxPairSimilarity)
                .ThenByDescending(g => g.Members.Sum(m => m.NonBlankLines))
                .ThenBy(g => g.Members[0].Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Members[0].StartLine)
                .Take(maxGroups)
                .ToList();
        }

        private static int Find(int[] parent, int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        private static void Union(int[] parent, int left, int right)
        {
            int leftRoot = Find(parent, left);
            int rightRoot = Find(parent, right);
            if (leftRoot != rightRoot) parent[Math.Max(leftRoot, rightRoot)] = Math.Min(leftRoot, rightRoot);
        }

        #endregion

        #region Private-Classes

        /// <summary>
        /// Thread-safe pair collector with a cap.
        /// </summary>
        private sealed class PairSink
        {
            private readonly object _Lock = new object();
            private readonly int _MaxPairs;

            public PairSink(int maxPairs)
            {
                _MaxPairs = maxPairs;
            }

            public List<int> Lefts { get; } = new List<int>();

            public List<int> Rights { get; } = new List<int>();

            public List<float> Similarities { get; } = new List<float>();

            public int Count
            {
                get
                {
                    lock (_Lock) return Lefts.Count;
                }
            }

            public bool Truncated { get; private set; } = false;

            /// <summary>Add a pair. Returns false once the cap is reached.</summary>
            public bool Add(int left, int right, float similarity)
            {
                lock (_Lock)
                {
                    if (Lefts.Count >= _MaxPairs)
                    {
                        Truncated = true;
                        return false;
                    }

                    Lefts.Add(left);
                    Rights.Add(right);
                    Similarities.Add(similarity);
                    return true;
                }
            }
        }

        #endregion
    }
}
