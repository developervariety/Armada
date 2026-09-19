namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    /// <summary>
    /// The Judge must walk every acceptance criterion from the brief. A PASS that omits the walk,
    /// or that names a criterion NOT MET, is refused. The walk is a real ground: review_substance
    /// never overturns it.
    /// </summary>
    public static class JudgeAcceptanceWalk
    {
        /// <summary>Section heading the brief and the Judge share for the walk.</summary>
        public const string SectionName = "Acceptance Criteria";

        private static readonly Regex _NotMet = new Regex(@"\bNOT\s+MET\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex _Met = new Regex(@"\bMET\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Bullet items under the brief's Acceptance Criteria heading.</summary>
        public static List<string> ExtractCriteria(string? brief)
        {
            List<string> items = new List<string>();
            string? block = ExtractSection(brief, SectionName);
            if (String.IsNullOrWhiteSpace(block)) return items;
            foreach (string raw in block.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                {
                    string item = line.Substring(2).Trim();
                    if (item.Length > 0 && !item.StartsWith("[additional", StringComparison.OrdinalIgnoreCase))
                        items.Add(item);
                }
            }
            return items;
        }

        /// <summary>
        /// A compact criteria block for brief pinning: heading plus bullets only.
        /// Null when the brief lists no criteria.
        /// </summary>
        public static string? FormatPinnedBlock(string? brief)
        {
            List<string> items = ExtractCriteria(brief);
            if (items.Count == 0) return null;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("## ").Append(SectionName).Append("\n");
            foreach (string item in items)
                sb.Append("- ").Append(item).Append("\n");
            return sb.ToString();
        }

public static string? ValidatePass(string? agentOutput, string? brief)
        {
            List<string> expected = ExtractCriteria(brief);
            if (expected.Count == 0) return null;

            string? block = ExtractSection(agentOutput, SectionName);
            if (String.IsNullOrWhiteSpace(block))
                return "Judge PASS verdict missing required review sections: " + SectionName;

            int marked = 0;
            bool notMet = false;
            foreach (string raw in block.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (_NotMet.IsMatch(line))
                {
                    notMet = true;
                    marked++;
                    continue;
                }
                if (_Met.IsMatch(line))
                    marked++;
            }

            if (notMet)
                return "Judge PASS names a NOT MET acceptance criterion";
            if (marked < expected.Count)
                return "Judge PASS does not list every acceptance criterion from the brief";
            return null;
        }

        /// <summary>The markdown section body for a heading, or null when absent.</summary>
        public static string? ExtractSection(string? text, string heading)
        {
            if (String.IsNullOrWhiteSpace(text) || String.IsNullOrWhiteSpace(heading)) return null;
            string needle = "## " + heading;
            int start = IndexOfHeading(text, needle);
            if (start < 0) return null;
            int body = text.IndexOf('\n', start);
            if (body < 0) return String.Empty;
            body++;
            int end = text.Length;
            int cursor = body;
            while (cursor < text.Length)
            {
                int lineEnd = text.IndexOf('\n', cursor);
                if (lineEnd < 0) lineEnd = text.Length;
                string line = text.Substring(cursor, lineEnd - cursor).TrimStart();
                if (line.StartsWith("## ", StringComparison.Ordinal) && !line.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                {
                    end = cursor;
                    break;
                }
                if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("...(mission", StringComparison.Ordinal))
                {
                    end = cursor;
                    break;
                }
                cursor = lineEnd < text.Length ? lineEnd + 1 : text.Length;
            }
            return text.Substring(body, end - body).Trim();
        }

        private static int IndexOfHeading(string text, string needle)
        {
            int from = 0;
            while (from < text.Length)
            {
                int at = text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return -1;
                if (at == 0 || text[at - 1] == '\n') return at;
                from = at + 1;
            }
            return -1;
        }
    }
}
