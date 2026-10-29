namespace Armada.Core.Services.FleetActionOutputParsers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Xml.Linq;

    /// <summary>Parses govulncheck-json reports without retaining source findings.</summary>
    public sealed class GovulncheckJsonParser : FleetActionOutputParser
    {
        /// <inheritdoc />
        protected override FleetActionParseResult ParseReport(string output, CancellationToken token)
        {
            FleetActionParseResult result = new();
            bool recognized = false;
            HashSet<string> findings = new(StringComparer.Ordinal);
            foreach (JsonElement root in JsonSequence(output, token))
            {
                token.ThrowIfCancellationRequested();
                recognized |= Has(root, "config") || Has(root, "progress") || Has(root, "finding") || Has(root, "osv");
                if (root.TryGetProperty("finding", out JsonElement finding))
                {
                    if (!finding.TryGetProperty("osv", out JsonElement osv) || osv.ValueKind != JsonValueKind.String || String.IsNullOrWhiteSpace(osv.GetString())) return new();
                    findings.Add(osv.GetString()!);
                }
            }
            if (!recognized) return result;
            result.Total = findings.Count;
            return Finish(result);
        }
    }
}
