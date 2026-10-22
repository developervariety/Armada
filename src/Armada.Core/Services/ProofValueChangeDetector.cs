namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Finds a brief sentence that states its proof value as two different values, one for the corrected code and one
    /// for the defective code: "freezes 2 after the fix and 3 at the old commit", "one line before the change and zero
    /// after it", or "changes from 3 to 2 with the fix". Such a sentence answers dispatch preflight question 9 (the
    /// proof value is the same before and after the fix) deterministically: the brief says it differs.
    ///
    /// A value is a number (decimal or hexadecimal) or a number word from zero to ten. Each value is paired with the
    /// nearest side phrase within a short gap, and the closest pair of two different value tokens wins. A sentence in
    /// a section that declares values unchanged on purpose (parity or control values) is never read.
    /// </summary>
    internal static class ProofValueChangeDetector
    {
        #region Private-Members

        private const int _MaxGap = 40;

        private static readonly Regex _FixedSide = new Regex(
            @"\b(?:after|with|post)[- ](?:the |this |a )?(?:fix|change|patch|repair|correction)\b|\bafter (?:it|fixing)\b|\b(?:fixed|corrected|patched|new) (?:code|commit|build|tip)\b|\bpost-fix\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex _OldSide = new Regex(
            @"\b(?:before|without|pre)[- ](?:the |this |a )?(?:fix|change|patch|repair|correction)\b|\bbefore it\b|\b(?:old|defective|unfixed|base|start|original|buggy) (?:code|commit|build|tip)\b|\bpre-fix\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex _Value = new Regex(
            @"\b(?:0x[0-9A-Fa-f]+|\d+(?:\.\d+)?|zero|one|two|three|four|five|six|seven|eight|nine|ten)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex _FromTo = new Regex(
            @"\bfrom\s+(?<from>0x[0-9A-Fa-f]+|\d+(?:\.\d+)?|zero|one|two|three|four|five|six|seven|eight|nine|ten)\s+to\s+(?<to>0x[0-9A-Fa-f]+|\d+(?:\.\d+)?|zero|one|two|three|four|five|six|seven|eight|nine|ten)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex _FixWord = new Regex(
            @"\bfix(?:es|ed)?\b|\bcorrect(?:ed|ion)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex _ParityLine = new Regex(
            @"\bparity\b|\bunchanged\s+on\s+purpose\b|\bunchanged\s+by\s+design\b|\bcontrol\s+values?\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex _SentenceEnd = new Regex(@"(?<=[.!?])\s+", RegexOptions.Compiled);

        private static readonly string[] _NumberWords = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };

        #endregion

        #region Public-Methods

        /// <summary>
        /// The first sentence of the description or acceptance criteria that states two different proof values for the
        /// corrected and the defective code, or null when none does.
        /// </summary>
        /// <param name="description">Objective description.</param>
        /// <param name="acceptanceCriteria">Objective acceptance criteria.</param>
        /// <returns>The sentence, trimmed, or null.</returns>
        public static string? Find(string? description, IEnumerable<string>? acceptanceCriteria)
        {
            List<string> lines = new List<string>();
            lines.AddRange((description ?? String.Empty).Split('\n'));
            lines.AddRange(acceptanceCriteria ?? Enumerable.Empty<string>());

            bool inParity = false;
            foreach (string raw in lines)
            {
                string line = (raw ?? String.Empty).Trim();
                if (line.Length == 0)
                {
                    inParity = false;
                    continue;
                }
                bool heading = line.StartsWith("#", StringComparison.Ordinal) || line.EndsWith(":", StringComparison.Ordinal);
                bool declaresParity = _ParityLine.IsMatch(line);
                if (heading) inParity = declaresParity;
                if (declaresParity || inParity) continue;

                foreach (string sentence in _SentenceEnd.Split(line))
                {
                    if (StatesChange(sentence)) return sentence.Trim();
                }
            }
            return null;
        }

        #endregion

        #region Private-Methods

        private static bool StatesChange(string sentence)
        {
            if (String.IsNullOrWhiteSpace(sentence)) return false;

            List<Match> fixedSide = _FixedSide.Matches(sentence).Cast<Match>().ToList();
            List<Match> oldSide = _OldSide.Matches(sentence).Cast<Match>().ToList();

            foreach (Match fromTo in _FromTo.Matches(sentence))
            {
                bool aboutTheFix = fixedSide.Count > 0 || oldSide.Count > 0 || _FixWord.IsMatch(sentence);
                if (aboutTheFix && Normalize(fromTo.Groups["from"].Value) != Normalize(fromTo.Groups["to"].Value)) return true;
            }

            if (fixedSide.Count == 0 || oldSide.Count == 0) return false;

            List<Match> values = _Value.Matches(sentence).Cast<Match>()
                .Where(value => !Overlaps(value, fixedSide) && !Overlaps(value, oldSide))
                .ToList();

            int best = Int32.MaxValue;
            bool differs = false;
            foreach (Match fixedValue in values)
            {
                int fixedGap = NearestGap(fixedValue, fixedSide);
                if (fixedGap > _MaxGap) continue;
                foreach (Match oldValue in values)
                {
                    if (oldValue.Index == fixedValue.Index) continue;
                    int oldGap = NearestGap(oldValue, oldSide);
                    if (oldGap > _MaxGap) continue;
                    int total = fixedGap + oldGap;
                    if (total >= best) continue;
                    best = total;
                    differs = Normalize(fixedValue.Value) != Normalize(oldValue.Value);
                }
            }
            return differs;
        }

        private static bool Overlaps(Match value, List<Match> phrases)
        {
            return phrases.Any(phrase => value.Index < phrase.Index + phrase.Length && phrase.Index < value.Index + value.Length);
        }

        // Characters between the value and the nearest side phrase, on either side of it.
        private static int NearestGap(Match value, List<Match> phrases)
        {
            int best = Int32.MaxValue;
            foreach (Match phrase in phrases)
            {
                int gap = value.Index >= phrase.Index + phrase.Length
                    ? value.Index - (phrase.Index + phrase.Length)
                    : phrase.Index - (value.Index + value.Length);
                if (gap >= 0 && gap < best) best = gap;
            }
            return best;
        }

        private static string Normalize(string value)
        {
            string text = value.Trim().ToLowerInvariant();
            int word = Array.IndexOf(_NumberWords, text);
            if (word >= 0) return word.ToString(CultureInfo.InvariantCulture);
            if (text.StartsWith("0x", StringComparison.Ordinal)
                && Int64.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
                return hex.ToString(CultureInfo.InvariantCulture);
            if (Decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number))
                return number.ToString("G29", CultureInfo.InvariantCulture);
            return text;
        }

        #endregion
    }
}
