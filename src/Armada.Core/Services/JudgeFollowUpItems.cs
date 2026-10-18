namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Splits a Judge's suggested follow-ups text into individual items. One bullet or numbered line is
    /// one item, and an indented or unmarked line continues the item above it. A text that says only
    /// that there is nothing to follow up ("none", "(none)", "n/a") has no items.
    /// </summary>
    public static class JudgeFollowUpItems
    {
        #region Private-Members

        private static readonly Regex _ItemStart = new Regex(@"^\s{0,3}(?:[-*+]|\d{1,3}[.)])\s+", RegexOptions.Compiled);
        private static readonly Regex _NothingToFollowUp = new Regex(@"^[\s\-*+.()]*(?:none|n/?a|no follow-?ups?(?: needed| required)?)[\s.()]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Split suggested follow-ups text into items.
        /// </summary>
        /// <param name="text">The stored suggested follow-ups text, or null.</param>
        /// <returns>The items in order; empty when the text holds nothing to follow up.</returns>
        public static List<string> Split(string? text)
        {
            List<string> items = new List<string>();
            if (String.IsNullOrWhiteSpace(text)) return items;

            string? current = null;
            foreach (string rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (String.IsNullOrWhiteSpace(rawLine)) continue;
                Match start = _ItemStart.Match(rawLine);
                if (start.Success)
                {
                    Add(items, current);
                    current = rawLine.Substring(start.Length).Trim();
                }
                else
                {
                    current = current == null ? rawLine.Trim() : current + " " + rawLine.Trim();
                }
            }

            Add(items, current);
            return items;
        }

        #endregion

        #region Private-Methods

        private static void Add(List<string> items, string? item)
        {
            if (String.IsNullOrWhiteSpace(item) || _NothingToFollowUp.IsMatch(item)) return;
            items.Add(item);
        }

        #endregion
    }
}
