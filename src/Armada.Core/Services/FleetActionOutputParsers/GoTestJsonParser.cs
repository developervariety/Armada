namespace Armada.Core.Services.FleetActionOutputParsers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Xml.Linq;

    /// <summary>Parses go-test-json reports without retaining source findings.</summary>
    public sealed class GoTestJsonParser : FleetActionOutputParser
    {
        /// <inheritdoc />
        protected override FleetActionParseResult ParseReport(string output, CancellationToken token)
        {
            FleetActionParseResult result = new();
            HashSet<string> pending = new(StringComparer.Ordinal);
            Dictionary<string, string> tests = new(StringComparer.Ordinal);
            foreach (JsonElement root in JsonSequence(output, token))
            {
                token.ThrowIfCancellationRequested();
                string? action = Str(root, "Action");
                if (action == null || !Has(root, "Package")) return new();
                string key = Str(root, "Package") + "/" + Str(root, "Test");
                if (action == "start" || action == "run") pending.Add(key);
                if (action == "pass" || action == "fail" || action == "skip")
                {
                    pending.Remove(key);
                    tests[key] = action;
                }
            }
            if (tests.Count == 0 || pending.Count > 0) return new();
            // Package terminal events accompany individual tests; do not count them twice.
            foreach (var test in tests.Where(t => !t.Key.EndsWith("/", StringComparison.Ordinal) || !tests.Keys.Any(k => k.StartsWith(t.Key, StringComparison.Ordinal) && k != t.Key)))
            {
                token.ThrowIfCancellationRequested();
                if (test.Value == "pass") result.Passed++;
                else if (test.Value == "fail") result.Failed++;
                else result.Skipped++;
            }
            return Finish(result, true);
        }
    }
}
