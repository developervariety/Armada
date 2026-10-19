namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using Armada.Core.Settings;

    public partial class MissionService
    {
        #region Private-Methods

        /// <summary>
        /// Rewrite configured host path prefixes in operator-written brief text to the paths a captain resolves.
        /// A prefix matches only at a path boundary on both sides, so a longer directory that shares its first
        /// characters, or a path that merely ends in the prefix, is left alone.
        /// </summary>
        /// <param name="text">Operator-written text (mission description or objective scope).</param>
        /// <param name="rewrites">Configured rewrites; null or empty leaves the text unchanged.</param>
        /// <returns>The rewritten text.</returns>
        internal static string? RewriteBriefPaths(string? text, IReadOnlyList<BriefPathRewrite>? rewrites)
        {
            if (String.IsNullOrEmpty(text) || rewrites == null || rewrites.Count == 0) return text;
            string result = text;
            foreach (BriefPathRewrite rewrite in rewrites)
            {
                if (rewrite == null || String.IsNullOrEmpty(rewrite.From) || String.Equals(rewrite.From, rewrite.To, StringComparison.Ordinal)) continue;
                string pattern = @"(?<![\w./\\-])" + Regex.Escape(rewrite.From) + @"(?=[/\\]|$|[^\w.-])";
                result = Regex.Replace(result, pattern, rewrite.To.Replace("$", "$$"));
            }
            return result;
        }

        #endregion
    }
}
