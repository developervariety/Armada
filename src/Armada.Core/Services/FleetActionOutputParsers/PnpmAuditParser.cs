namespace Armada.Core.Services.FleetActionOutputParsers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Xml.Linq;

    /// <summary>Parses pnpm-audit reports without retaining source findings.</summary>
    public sealed class PnpmAuditParser : FleetActionOutputParser
    {
        /// <inheritdoc />
        protected override FleetActionParseResult ParseReport(string output, CancellationToken token)
        {
            FleetActionParseResult result = new();
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement data = document.RootElement;
            if (data.TryGetProperty("metadata", out JsonElement metadata) && metadata.TryGetProperty("vulnerabilities", out JsonElement counts))
            {
                foreach (JsonProperty count in counts.EnumerateObject())
                {
                    if (count.Name == "total") continue;
                    int number = count.Value.GetInt32();
                    if (number < 0) return new();
                    result.Total += number;
                    Severity(result, count.Name, number);
                }
            }
            else return result;

            return Finish(result);
        }
    }
}
