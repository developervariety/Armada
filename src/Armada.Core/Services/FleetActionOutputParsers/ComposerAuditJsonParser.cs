namespace Armada.Core.Services.FleetActionOutputParsers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Xml.Linq;

    /// <summary>Parses composer-audit-json reports without retaining source findings.</summary>
    public sealed class ComposerAuditJsonParser : FleetActionOutputParser
    {
        /// <inheritdoc />
        protected override FleetActionParseResult ParseReport(string output, CancellationToken token)
        {
            FleetActionParseResult result = new();
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement data = document.RootElement;
            JsonElement advisories = data.GetProperty("advisories");
            if (advisories.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty package in advisories.EnumerateObject())
                foreach (JsonElement vuln in package.Value.EnumerateArray()) Add(result, FleetActionSeverity.FromFinding(vuln), token);
            else if (advisories.ValueKind != JsonValueKind.Array || advisories.GetArrayLength() != 0) return result;

            return Finish(result);
        }
    }
}
