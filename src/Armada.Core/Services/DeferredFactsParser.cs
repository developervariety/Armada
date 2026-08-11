namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Armada.Core.Models;

    /// <summary>
    /// Reads a vessel's deferred-facts file and enforces the rules that keep the list honest.
    ///
    /// The file lives beside the vessel's other durable memory, so an operator edits it with ordinary
    /// git and every change is reviewable in a diff. Armada does not store it; Armada enforces it.
    ///
    /// An entry that names no fix objective, or carries no expiry, is REFUSED rather than rendered.
    /// That refusal is the mechanism: a list anyone can append to without committing to remove the
    /// entry becomes a place to record problems instead of solving them.
    ///
    /// Expected form, one block per fact, blank line between blocks:
    ///
    ///   fact: the SocketCAN suite needs a real can0 and the dock has none
    ///   fix: obj_example0000
    ///   expires: 2026-09-30
    ///   verified-at: 8510ae4
    /// </summary>
    public static class DeferredFactsParser
    {
        #region Public-Methods

        /// <summary>
        /// Parses the file content into complete entries and refusals.
        /// </summary>
        /// <param name="content">Raw file content; may be null or empty.</param>
        /// <param name="accepted">Entries that carry everything they need.</param>
        /// <param name="refusals">One line per refused entry, saying which field was missing.</param>
        public static void Parse(string? content, out List<DeferredFact> accepted, out List<string> refusals)
        {
            accepted = new List<DeferredFact>();
            refusals = new List<string>();

            if (String.IsNullOrWhiteSpace(content)) return;

            List<string> block = new List<string>();
            string[] lines = content!.Replace("\r\n", "\n").Split('\n');

            foreach (string line in lines)
            {
                string trimmed = line.Trim();

                if (trimmed.Length == 0)
                {
                    FlushBlock(block, accepted, refusals);
                    block.Clear();
                    continue;
                }

                if (trimmed.StartsWith("#", StringComparison.Ordinal)) continue;

                block.Add(trimmed);
            }

            FlushBlock(block, accepted, refusals);
        }

        #endregion

        #region Private-Methods

        private static void FlushBlock(List<string> block, List<DeferredFact> accepted, List<string> refusals)
        {
            if (block.Count == 0) return;

            DeferredFact fact = new DeferredFact();
            bool expirySeen = false;
            bool expiryValid = false;

            foreach (string line in block)
            {
                int separator = line.IndexOf(':');
                if (separator <= 0) continue;

                string key = line.Substring(0, separator).Trim().ToLowerInvariant();
                string value = line.Substring(separator + 1).Trim();

                switch (key)
                {
                    case "fact":
                        fact.Text = value;
                        break;
                    case "fix":
                        fact.FixObjectiveId = value;
                        break;
                    case "verified-at":
                        fact.LastVerifiedCommit = value;
                        break;
                    case "expires":
                        expirySeen = true;
                        DateTime parsed;
                        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
                        {
                            fact.ExpiresUtc = parsed;
                            expiryValid = true;
                        }
                        break;
                }
            }

            string label = String.IsNullOrEmpty(fact.Text)
                ? "an entry with no fact line"
                : "\"" + Shorten(fact.Text) + "\"";

            if (String.IsNullOrEmpty(fact.Text))
            {
                refusals.Add("refused " + label + ": no fact text");
                return;
            }

            if (String.IsNullOrEmpty(fact.FixObjectiveId))
            {
                refusals.Add("refused " + label + ": no fix objective. Every deferred fact names the objective that removes it.");
                return;
            }

            if (!expirySeen || !expiryValid)
            {
                refusals.Add("refused " + label + ": no usable expiry. Use expires: yyyy-MM-dd.");
                return;
            }

            accepted.Add(fact);
        }

        private static string Shorten(string text)
        {
            const int max = 60;
            if (text.Length <= max) return text;
            return text.Substring(0, max) + "...";
        }

        #endregion
    }
}
