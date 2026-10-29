namespace Armada.Core.Services.FleetActionOutputParsers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Xml.Linq;

    /// <summary>Parses bundle-audit reports without retaining source findings.</summary>
    public sealed class BundleAuditParser : FleetActionOutputParser
    {
        /// <inheritdoc />
        protected override FleetActionParseResult ParseReport(string output, CancellationToken token)
        {
            FleetActionParseResult result = new();
            // The CLI's text report emits one Name line per vulnerable gem advisory.
            string[] lines = output.Split('\n');
            result.Total = lines.Count(l => l.StartsWith("Name: ", StringComparison.Ordinal));
            if (result.Total == 0 && !output.Contains("No vulnerabilities found", StringComparison.Ordinal)) return result;
            if (result.Total > 0 && lines.Count(l => l.StartsWith("Solution: ", StringComparison.Ordinal)) != result.Total) return new();
            foreach (string line in lines.Where(l => l.StartsWith("Criticality: ", StringComparison.Ordinal))) Severity(result, line.Substring(13).Trim());
            return Finish(result);
        }
    }
}
