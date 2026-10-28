namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    /// <summary>
    /// Read-only Git facts for one vessel. The text form carries no paths, so a runner
    /// can return it without naming its checkout.
    /// </summary>
    public sealed class RepositoryHealthGitFacts
    {
        /// <summary>At least one configured path is a Git repository.</summary>
        public bool RepositoryReachable { get; set; }

        /// <summary>Every present repository resolves the default branch.</summary>
        public bool DefaultBranchResolvable { get; set; }

        /// <summary>A working checkout path was configured and is a non-bare repository.</summary>
        public bool WorkingCheckoutPresent { get; set; }

        /// <summary>True when the working checkout has no porcelain changes. Null when there is no checkout.</summary>
        public bool? WorkingCheckoutClean { get; set; }

        /// <summary>Commits on the working checkout that origin does not have.</summary>
        public int? WorkingAhead { get; set; }

        /// <summary>Commits on origin that the working checkout does not have.</summary>
        public int? WorkingBehind { get; set; }

        /// <summary>The working tip and the remote tip differ and the counts could not be read.</summary>
        public bool WorkingComparisonIncomplete { get; set; }

        /// <summary>The bare repository path is a bare Git repository.</summary>
        public bool BarePresent { get; set; }

        /// <summary>Commits on the bare repository that origin does not have.</summary>
        public int? BareAhead { get; set; }

        /// <summary>Commits on origin that the bare repository does not have.</summary>
        public int? BareBehind { get; set; }

        /// <summary>The bare tip and the remote tip differ and the counts could not be read.</summary>
        public bool BareComparisonIncomplete { get; set; }

        /// <summary>Write the facts as one record with no paths.</summary>
        /// <returns>The line record.</returns>
        public string Format()
        {
            return String.Join("\n", new[]
            {
                "reachable=" + Bit(RepositoryReachable),
                "default_branch=" + Bit(DefaultBranchResolvable),
                "working_present=" + Bit(WorkingCheckoutPresent),
                "working_clean=" + Tri(WorkingCheckoutClean),
                "working_ahead=" + Num(WorkingAhead),
                "working_behind=" + Num(WorkingBehind),
                "working_incomplete=" + Bit(WorkingComparisonIncomplete),
                "bare_present=" + Bit(BarePresent),
                "bare_ahead=" + Num(BareAhead),
                "bare_behind=" + Num(BareBehind),
                "bare_incomplete=" + Bit(BareComparisonIncomplete)
            });
        }

        /// <summary>Read a record produced by <see cref="Format"/>.</summary>
        /// <param name="text">The record.</param>
        /// <returns>The facts, or null when the record is incomplete.</returns>
        public static RepositoryHealthGitFacts? Parse(string? text)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int split = raw.IndexOf('=');
                if (split <= 0) return null;
                fields[raw.Substring(0, split)] = raw.Substring(split + 1);
            }

            if (!TryBit(fields, "reachable", out bool reachable)
                || !TryBit(fields, "default_branch", out bool branch)
                || !TryBit(fields, "working_present", out bool workingPresent)
                || !TryTri(fields, "working_clean", out bool? clean)
                || !TryNum(fields, "working_ahead", out int? workingAhead)
                || !TryNum(fields, "working_behind", out int? workingBehind)
                || !TryBit(fields, "working_incomplete", out bool workingIncomplete)
                || !TryBit(fields, "bare_present", out bool barePresent)
                || !TryNum(fields, "bare_ahead", out int? bareAhead)
                || !TryNum(fields, "bare_behind", out int? bareBehind)
                || !TryBit(fields, "bare_incomplete", out bool bareIncomplete))
                return null;

            return new RepositoryHealthGitFacts
            {
                RepositoryReachable = reachable,
                DefaultBranchResolvable = branch,
                WorkingCheckoutPresent = workingPresent,
                WorkingCheckoutClean = clean,
                WorkingAhead = workingAhead,
                WorkingBehind = workingBehind,
                WorkingComparisonIncomplete = workingIncomplete,
                BarePresent = barePresent,
                BareAhead = bareAhead,
                BareBehind = bareBehind,
                BareComparisonIncomplete = bareIncomplete
            };
        }

        private static string Bit(bool value) => value ? "1" : "0";

        private static string Tri(bool? value) => value == null ? "x" : Bit(value.Value);

        private static string Num(int? value) => value == null ? "x" : value.Value.ToString(CultureInfo.InvariantCulture);

        private static bool TryBit(Dictionary<string, string> fields, string name, out bool value)
        {
            value = false;
            if (!fields.TryGetValue(name, out string? text)) return false;
            if (text == "1") { value = true; return true; }
            return text == "0";
        }

        private static bool TryTri(Dictionary<string, string> fields, string name, out bool? value)
        {
            value = null;
            if (!fields.TryGetValue(name, out string? text)) return false;
            if (text == "x") return true;
            if (!TryBit(new Dictionary<string, string> { [name] = text }, name, out bool bit)) return false;
            value = bit;
            return true;
        }

        private static bool TryNum(Dictionary<string, string> fields, string name, out int? value)
        {
            value = null;
            if (!fields.TryGetValue(name, out string? text)) return false;
            if (text == "x") return true;
            if (!Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 0) return false;
            value = parsed;
            return true;
        }
    }
}
