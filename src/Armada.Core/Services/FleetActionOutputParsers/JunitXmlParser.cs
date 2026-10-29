namespace Armada.Core.Services.FleetActionOutputParsers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Xml.Linq;

    /// <summary>Parses junit-xml reports without retaining source findings.</summary>
    public sealed class JunitXmlParser : FleetActionOutputParser
    {
        /// <inheritdoc />
        protected override FleetActionParseResult ParseReport(string output, CancellationToken token)
        {
            FleetActionParseResult result = new();
            XDocument doc = ReadXml(output);
            if (doc.Root?.Name.LocalName != "testsuite" && doc.Root?.Name.LocalName != "testsuites") return result;
            foreach (XElement suite in doc.Root.DescendantsAndSelf().Where(e => e.Name.LocalName == "testsuite" && !e.Descendants().Any(child => child.Name.LocalName == "testsuite")))
            {
                token.ThrowIfCancellationRequested();
                List<XElement> tests = suite.Descendants().Where(e => e.Name.LocalName == "testcase").ToList();
                if (tests.Count == 0 && suite.Attribute("tests") != null)
                {
                    int failed = Attr(suite, "failures") + Attr(suite, "errors");
                    int skipped = Attr(suite, "skipped");
                    int total = Attr(suite, "tests");
                    if (failed + skipped > total) return new();
                    result.Failed += failed; result.Skipped += skipped; result.Passed += total - failed - skipped;
                }
                foreach (XElement test in tests)
                {
                    token.ThrowIfCancellationRequested();
                    if (test.Elements().Any(e => e.Name.LocalName == "failure" || e.Name.LocalName == "error")) result.Failed++;
                    else if (test.Elements().Any(e => e.Name.LocalName == "skipped")) result.Skipped++;
                    else result.Passed++;
                }
            }
            return Finish(result, true);
        }
    }
}
