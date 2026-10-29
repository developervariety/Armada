namespace Armada.Core.Services.FleetActionOutputParsers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Xml.Linq;

    /// <summary>Parses pip-audit-json reports without retaining source findings.</summary>
    public sealed class PipAuditJsonParser : FleetActionOutputParser
    {
        /// <inheritdoc />
        protected override FleetActionParseResult ParseReport(string output, CancellationToken token)
        {
            FleetActionParseResult result = new();
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement data = document.RootElement;
            JsonElement dependencies = data.ValueKind == JsonValueKind.Array ? data : data.GetProperty("dependencies");
            foreach (JsonElement dependency in dependencies.EnumerateArray())
            foreach (JsonElement vuln in dependency.GetProperty("vulns").EnumerateArray()) Add(result, FleetActionSeverity.FromFinding(vuln), token);

            return Finish(result);
        }
    }
}
