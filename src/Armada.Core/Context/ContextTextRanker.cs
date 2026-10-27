namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using Armada.Core.Models;

    /// <summary>Local BM25 ranking over already-authorized context candidates.</summary>
    internal static class ContextTextRanker
    {
        private static readonly Regex _Words = new Regex(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);
        private static readonly HashSet<string> _StopWords = new HashSet<string>(
            "a an and are as at be by can do does for from how i in is it of on or should that the their this to was we what when where which who why with would".Split(' '),
            StringComparer.Ordinal);

        internal static List<ContextSearchItem> Rank(string query, List<ContextSearchItem> candidates, CancellationToken token)
        {
            HashSet<string> terms = new HashSet<string>(Words(query), StringComparer.Ordinal);
            if (terms.Count == 0 || candidates.Count == 0) return new List<ContextSearchItem>();
            List<Dictionary<string, int>> frequencies = new List<Dictionary<string, int>>();
            List<int> lengths = new List<int>();
            Dictionary<string, int> documentCounts = terms.ToDictionary(term => term, _ => 0, StringComparer.Ordinal);
            foreach (ContextSearchItem item in candidates)
            {
                token.ThrowIfCancellationRequested();
                List<string> words = Words(item.Title + " " + item.Title + " " + item.Title + " " + item.SearchMetadata + " " + item.Text);
                Dictionary<string, int> counts = words.GroupBy(word => word, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                frequencies.Add(counts);
                lengths.Add(Math.Max(1, words.Count));
                foreach (string term in terms) if (counts.ContainsKey(term)) documentCounts[term]++;
            }

            double averageLength = lengths.Average();
            List<ContextSearchItem> matches = new List<ContextSearchItem>();
            for (int i = 0; i < candidates.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                double score = 0;
                foreach (string term in terms)
                {
                    if (!frequencies[i].TryGetValue(term, out int count)) continue;
                    double inverseFrequency = Math.Log(1 + (candidates.Count - documentCounts[term] + 0.5) / (documentCounts[term] + 0.5));
                    score += inverseFrequency * count * 2.2 / (count + 1.2 * (0.25 + 0.75 * lengths[i] / averageLength));
                }
                if (score <= 0) continue;
                candidates[i].Score = score;
                matches.Add(candidates[i]);
            }
            return matches.OrderByDescending(item => item.Score).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();
        }

        private static List<string> Words(string text)
        {
            return _Words.Matches(text.ToLowerInvariant()).Select(match => match.Value)
                .Where(word => !_StopWords.Contains(word)).ToList();
        }
    }
}
