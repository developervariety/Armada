namespace Armada.Core.Services.FleetActionOutputParsers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Xml.Linq;

    /// <summary>Parses dotnet-test-trx reports without retaining source findings.</summary>
    public sealed class DotnetTestTrxParser : FleetActionOutputParser
    {
        /// <inheritdoc />
        protected override FleetActionParseResult ParseReport(string output, CancellationToken token)
        {
            FleetActionParseResult result = new();
            XDocument doc = ReadXml(output);
            token.ThrowIfCancellationRequested();
            XElement? counters = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Counters");
            if (doc.Root?.Name.LocalName != "TestRun" || counters == null || counters.Attribute("total") == null) return result;
            result.Passed = Attr(counters, "passed");
            result.Failed = Attr(counters, "failed") + Attr(counters, "error") + Attr(counters, "timeout") + Attr(counters, "aborted");
            result.Skipped = Attr(counters, "notExecuted");
            return Finish(result, true);
        }
    }
}
