namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A minimal parser for the chunk front-matter block a chunk file may carry: a leading
    /// <c>---</c> fenced YAML-subset block with the fields <c>topic</c>, <c>summary</c>,
    /// <c>read_when</c>, <c>applies_to</c>, <c>tier</c>, and optional <c>must_retrieve</c>. Lists are
    /// accepted in inline form (<c>[a, b]</c>) or block form (a dash item per line). This is a
    /// deliberately small parser, not a full YAML engine: the front-matter shape is fixed and
    /// documented, and a full engine would add a dependency for one bounded use.
    ///
    /// Today's AI-Memory and docs files carry no front-matter; the generator falls back to
    /// whole-file or section chunks and takes the tier from configuration. This parser is the seam
    /// for the later state, when a source is sub-chunked into files that each carry front-matter.
    /// </summary>
    public sealed class ContextChunkFrontMatter
    {
        /// <summary>The <c>topic</c> field, or null when absent.</summary>
        public string? Topic { get; set; }

        /// <summary>The <c>summary</c> field, or null when absent.</summary>
        public string? Summary { get; set; }

        /// <summary>The <c>read_when</c> field, or null when absent.</summary>
        public string? ReadWhen { get; set; }

        /// <summary>The <c>applies_to</c> list, or null when absent.</summary>
        public List<string>? AppliesTo { get; set; }

        /// <summary>The <c>tier</c> field parsed to the enum, or null when absent or unrecognized.</summary>
        public ContextTierEnum? Tier { get; set; }

        /// <summary>The <c>must_retrieve</c> list, or null when absent.</summary>
        public List<string>? MustRetrieve { get; set; }

        /// <summary>The document body after the front-matter block.</summary>
        public string Body { get; set; } = String.Empty;

        /// <summary>Whether a front-matter block was found and parsed.</summary>
        public bool HasFrontMatter { get; set; }

        /// <summary>
        /// Parse a source document. When it opens with a <c>---</c> fence, the enclosed block is read
        /// as front-matter and <see cref="Body"/> holds the remainder; otherwise the whole text is the
        /// body and <see cref="HasFrontMatter"/> is false. Never throws: a malformed block is treated
        /// as no front-matter.
        /// </summary>
        public static ContextChunkFrontMatter Parse(string text)
        {
            ContextChunkFrontMatter result = new ContextChunkFrontMatter { Body = text ?? String.Empty };
            if (String.IsNullOrEmpty(text)) return result;

            // A front-matter block must be the very first line. Accept both \n and \r\n.
            string normalized = text.Replace("\r\n", "\n");
            if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return result;

            int closeIndex = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (closeIndex < 0) return result;

            string block = normalized.Substring(4, closeIndex - 4);

            // The body begins after the closing fence's line.
            int afterClose = closeIndex + "\n---".Length;
            int bodyStart = normalized.IndexOf('\n', afterClose);
            string body = bodyStart < 0 ? String.Empty : normalized.Substring(bodyStart + 1);

            try
            {
                ParseBlock(block, result);
                result.Body = body;
                result.HasFrontMatter = true;
            }
            catch
            {
                // A malformed block is not a chunk with partial metadata; it is no front-matter.
                return new ContextChunkFrontMatter { Body = text };
            }

            return result;
        }

        private static void ParseBlock(string block, ContextChunkFrontMatter result)
        {
            string[] lines = block.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (String.IsNullOrWhiteSpace(line)) continue;
                if (line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;

                int colon = line.IndexOf(':');
                if (colon <= 0) continue;

                string key = line.Substring(0, colon).Trim().ToLowerInvariant();
                string value = line.Substring(colon + 1).Trim();

                switch (key)
                {
                    case "topic": result.Topic = Unquote(value); break;
                    case "summary": result.Summary = Unquote(value); break;
                    case "read_when": result.ReadWhen = Unquote(value); break;
                    case "tier": result.Tier = ParseTier(Unquote(value)); break;
                    case "applies_to": result.AppliesTo = ParseList(value, lines, ref i); break;
                    case "must_retrieve": result.MustRetrieve = ParseList(value, lines, ref i); break;
                }
            }
        }

        private static ContextTierEnum? ParseTier(string value)
        {
            if (String.Equals(value, "core", StringComparison.OrdinalIgnoreCase)) return ContextTierEnum.Core;
            if (String.Equals(value, "leaf", StringComparison.OrdinalIgnoreCase)) return ContextTierEnum.Leaf;
            return null;
        }

        // Reads an inline list ([a, b]) on the key line, or a block list (following "- " lines).
        private static List<string> ParseList(string inlineValue, string[] lines, ref int index)
        {
            List<string> items = new List<string>();
            string trimmed = inlineValue.Trim();

            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                string inner = trimmed.Substring(1, trimmed.Length - 2);
                foreach (string part in inner.Split(','))
                {
                    string item = Unquote(part.Trim());
                    if (!String.IsNullOrEmpty(item)) items.Add(item);
                }
                return items;
            }

            if (!String.IsNullOrEmpty(trimmed) && trimmed != "|" && trimmed != ">")
            {
                // A single scalar value on the key line is a one-item list.
                items.Add(Unquote(trimmed));
                return items;
            }

            // Block form: consume following "- item" lines.
            int j = index + 1;
            while (j < lines.Length)
            {
                string next = lines[j].Trim();
                if (!next.StartsWith("-", StringComparison.Ordinal)) break;
                string item = Unquote(next.Substring(1).Trim());
                if (!String.IsNullOrEmpty(item)) items.Add(item);
                j++;
            }
            index = j - 1;
            return items;
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[value.Length - 1] == '"') ||
                 (value[0] == '\'' && value[value.Length - 1] == '\'')))
            {
                return value.Substring(1, value.Length - 2);
            }
            return value;
        }
    }
}
