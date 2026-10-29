namespace Armada.Core.Services.FleetActionOutputParsers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Xml.Linq;

    /// <summary>Parses osv-scanner-json reports without retaining source findings.</summary>
    public sealed class OsvScannerJsonParser : FleetActionOutputParser
    {
        /// <inheritdoc />
        protected override FleetActionParseResult ParseReport(string output, CancellationToken token)
        {
            FleetActionParseResult result = new();
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement data = document.RootElement;
            foreach (JsonElement source in data.GetProperty("results").EnumerateArray())
            foreach (JsonElement package in source.GetProperty("packages").EnumerateArray())
            foreach (JsonElement vuln in package.GetProperty("vulnerabilities").EnumerateArray()) Add(result, FleetActionSeverity.FromFinding(vuln), token);

            return Finish(result);
        }
    }
}
