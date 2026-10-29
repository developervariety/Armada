namespace Armada.Core.Services.FleetActionOutputParsers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Xml.Linq;

    /// <summary>Parses gitleaks-json reports without retaining source findings.</summary>
    public sealed class GitleaksJsonParser : FleetActionOutputParser
    {
        /// <inheritdoc />
        protected override FleetActionParseResult ParseReport(string output, CancellationToken token)
        {
            FleetActionParseResult result = new();
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement data = document.RootElement;
            foreach (JsonElement leak in data.EnumerateArray())
            {
                if (!Has(leak, "RuleID")) return new();
                Add(result, null, token);
            }

            return Finish(result);
        }
    }
}
