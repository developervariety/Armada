namespace Armada.Core.Services.FleetActionOutputParsers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Xml.Linq;

    /// <summary>Parses dotnet-vulnerable reports without retaining source findings.</summary>
    public sealed class DotnetVulnerableParser : FleetActionOutputParser
    {
        /// <inheritdoc />
        protected override FleetActionParseResult ParseReport(string output, CancellationToken token)
        {
            FleetActionParseResult result = new();
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement data = document.RootElement;
            if (!Has(data, "projects")) return result;
            foreach (JsonElement project in data.GetProperty("projects").EnumerateArray())
            foreach (JsonElement framework in Array(project, "frameworks"))
            foreach (string category in new[] { "topLevelPackages", "transitivePackages" })
            foreach (JsonElement package in Array(framework, category))
            foreach (JsonElement vuln in Array(package, "vulnerabilities")) Add(result, FleetActionSeverity.FromFinding(vuln), token);

            return Finish(result);
        }
    }
}
