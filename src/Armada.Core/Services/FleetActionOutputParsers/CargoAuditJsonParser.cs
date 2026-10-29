namespace Armada.Core.Services.FleetActionOutputParsers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Xml.Linq;

    /// <summary>Parses cargo-audit-json reports without retaining source findings.</summary>
    public sealed class CargoAuditJsonParser : FleetActionOutputParser
    {
        /// <inheritdoc />
        protected override FleetActionParseResult ParseReport(string output, CancellationToken token)
        {
            FleetActionParseResult result = new();
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement data = document.RootElement;
            JsonElement vulnerabilities = data.GetProperty("vulnerabilities");
            foreach (JsonElement vuln in vulnerabilities.GetProperty("list").EnumerateArray())
            {
                string? severity = FleetActionSeverity.FromFinding(vuln.GetProperty("advisory"));
                Add(result, severity, token);
            }

            return Finish(result);
        }
    }
}
