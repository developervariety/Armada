namespace Armada.Tui.Widgets
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using Armada.Core.Models;
    using Armada.Tui.Screens.Activity;

    /// <summary>Daily UTC token calendar, retaining long token totals while reusing calendar navigation and themes.</summary>
    public class TokenActivityHeatmap : CommitHeatmap
    {
        private Dictionary<DateTime, long> _Values = new Dictionary<DateTime, long>();

        /// <summary>Populate the calendar from daily summary buckets for total, input, output, or cached tokens.</summary>
        /// <param name="data">Daily summary or null.</param>
        /// <param name="from">First UTC day.</param>
        /// <param name="to">Last UTC day.</param>
        /// <param name="metric">Token type.</param>
        public void SetUsage(TokenUsageSummaryResult? data, DateTime from, DateTime to, string metric = "total")
        {
            _Values.Clear();
            foreach (TokenUsageBucket bucket in data?.Buckets ?? new List<TokenUsageBucket>())
            {
                DateTime day = bucket.BucketStartUtc.Date;
                if (day < from.Date || day > to.Date) continue;
                long value = metric switch { "input" => bucket.InputTokens, "output" => bucket.OutputTokens, "cached" => bucket.CachedTokens, _ => bucket.TotalTokens };
                _Values[day] = _Values.GetValueOrDefault(day) + value;
            }
            long max = _Values.Count == 0 ? 0 : _Values.Values.Max();
            Dictionary<DateTime, int> levels = _Values.ToDictionary(p => p.Key, p => max <= 0 || p.Value <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(4.0 * p.Value / max), 1, 4));
            SetData(from, to, levels);
            DayDescription = day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " UTC: " + TokenUsageFormat.Tokens(_Values.GetValueOrDefault(day)) + " " + T("Tokens");
            Summary = T("Total") + ": " + TokenUsageFormat.Tokens(_Values.Values.Sum()) + "  " + T("Peak day") + ": " + TokenUsageFormat.Tokens(max);
        }

        /// <summary>Actual count on a UTC calendar day, without intensity quantization or integer truncation.</summary>
        /// <param name="day">UTC calendar day.</param>
        /// <returns>Tokens.</returns>
        public long TokensOn(DateTime day)
        {
            return _Values.GetValueOrDefault(day.Date);
        }
    }
}
