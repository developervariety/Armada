namespace Armada.Core.Services.FleetActionOutputParsers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Xml.Linq;

    /// <summary>Parses yarn-audit reports without retaining source findings.</summary>
    public sealed class YarnAuditParser : FleetActionOutputParser
    {
        /// <inheritdoc />
        protected override FleetActionParseResult ParseReport(string output, CancellationToken token)
        {
            FleetActionParseResult result = new();
            bool summary = false;
            FleetActionParseResult summaryCounts = new();
            foreach (JsonElement root in JsonSequence(output, token))
            {
                token.ThrowIfCancellationRequested();
                string? type = Str(root, "type");
                if (type == "auditSummary")
                {
                    summary = true;
                    foreach (JsonProperty count in root.GetProperty("data").GetProperty("vulnerabilities").EnumerateObject())
                    {
                        int number = count.Value.GetInt32();
                        if (number < 0) return new();
                        summaryCounts.Total += number;
                        Severity(summaryCounts, count.Name, number);
                    }
                }
                if (type == "auditAdvisory")
                {
                    JsonElement advisory = root.GetProperty("data").GetProperty("advisory");
                    Add(result, Str(advisory, "severity"), token);
                }
            }
            if (!summary) return new();
            return Finish(summaryCounts.Total > 0 ? summaryCounts : result);
        }
    }
}
