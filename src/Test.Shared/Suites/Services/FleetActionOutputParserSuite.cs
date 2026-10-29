namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services.FleetActionOutputParsers;
    using Touchstone.Core;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Representative native reports, malformed reports and empty results for every parser.</summary>
    public sealed class FleetActionOutputParserSuite : IArmadaTestSuite
    {
        /// <summary>Build parser contract checks.</summary>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new();
            (string Id, string Clean, string Findings)[] samples =
            {
                ("dotnet-vulnerable", "{\"version\":1,\"projects\":[{\"path\":\"app.csproj\",\"frameworks\":[{\"framework\":\"net10.0\"}]}]}", "{\"version\":1,\"projects\":[{\"frameworks\":[{\"topLevelPackages\":[{\"id\":\"Example\",\"vulnerabilities\":[{\"severity\":\"High\",\"advisoryurl\":\"https://example.test/advisory\"}]}]}]}]}"),
                ("npm-audit-json", "{\"auditReportVersion\":2,\"metadata\":{\"vulnerabilities\":{\"info\":0,\"low\":0,\"moderate\":0,\"high\":0,\"critical\":0,\"total\":0}}}", "{\"auditReportVersion\":2,\"metadata\":{\"vulnerabilities\":{\"info\":0,\"low\":0,\"moderate\":0,\"high\":1,\"critical\":0,\"total\":1}}}"),
                ("pnpm-audit", "{\"actions\":[],\"advisories\":{},\"metadata\":{\"vulnerabilities\":{\"high\":0}}}", "{\"actions\":[],\"advisories\":{},\"metadata\":{\"vulnerabilities\":{\"high\":1}}}"),
                ("yarn-audit", "{\"type\":\"auditSummary\",\"data\":{\"vulnerabilities\":{\"high\":0}}}", "{\"type\":\"auditAdvisory\",\"data\":{\"advisory\":{\"id\":1,\"severity\":\"high\"}}}\n{\"type\":\"auditSummary\",\"data\":{\"vulnerabilities\":{\"high\":1}}}"),
                ("pip-audit-json", "{\"dependencies\":[{\"name\":\"example\",\"version\":\"1.0\",\"vulns\":[]}]}", "{\"dependencies\":[{\"name\":\"example\",\"version\":\"1.0\",\"vulns\":[{\"id\":\"PYSEC-2026-1\",\"fix_versions\":[\"2.0\"]}]}]}"),
                ("bundle-audit", "No vulnerabilities found", "Name: example\nVersion: 1.0\nAdvisory: CVE-2026-1\nCriticality: High\nSolution: upgrade to >= 2.0\n"),
                ("govulncheck-json", "{\"config\":{\"protocol_version\":\"v1.0.0\",\"scanner_name\":\"govulncheck\"}}", "{\"config\":{\"protocol_version\":\"v1.0.0\"}}\n{\"finding\":{\"osv\":\"GO-2026-1\",\"trace\":[{\"module\":\"example.test/app\"}]}}"),
                ("cargo-audit-json", "{\"vulnerabilities\":{\"found\":false,\"count\":0,\"list\":[]}}", "{\"vulnerabilities\":{\"found\":true,\"count\":1,\"list\":[{\"advisory\":{\"id\":\"RUSTSEC-2026-0001\",\"severity\":\"high\"}}]}}"),
                ("composer-audit-json", "{\"advisories\":[],\"abandoned\":[]}", "{\"advisories\":{\"vendor/package\":[{\"advisoryId\":\"PKSA-example\",\"severity\":\"high\"}]},\"abandoned\":[]}"),
                ("osv-scanner-json", "{\"results\":[]}", "{\"results\":[{\"source\":{\"path\":\"go.sum\",\"type\":\"lockfile\"},\"packages\":[{\"package\":{\"name\":\"example\",\"ecosystem\":\"Go\"},\"vulnerabilities\":[{\"id\":\"GO-2026-1\"}]}]}]}"),
                ("gitleaks-json", "[]", "[{\"RuleID\":\"generic-api-key\",\"Secret\":\"never-persist-this-secret\",\"Match\":\"key=never-persist-this-secret\",\"File\":\"config.env\",\"StartLine\":1}]"),
                ("dotnet-test-trx", "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><ResultSummary><Counters total=\"1\" passed=\"1\" failed=\"0\" notExecuted=\"0\" /></ResultSummary></TestRun>", "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><ResultSummary><Counters total=\"2\" passed=\"0\" failed=\"1\" notExecuted=\"1\" /></ResultSummary></TestRun>"),
                ("junit-xml", "<testsuites><testsuite tests=\"1\"><testcase name=\"works\" /></testsuite></testsuites>", "<testsuites><testsuite tests=\"3\"><testcase name=\"works\"/><testcase name=\"fails\"><failure message=\"bad\"/></testcase><testcase name=\"skip\"><skipped/></testcase></testsuite></testsuites>"),
                ("go-test-json", "{\"Action\":\"pass\",\"Package\":\"example.test/app\",\"Test\":\"TestWorks\",\"Elapsed\":0.01}\n{\"Action\":\"pass\",\"Package\":\"example.test/app\",\"Elapsed\":0.01}", "{\"Action\":\"fail\",\"Package\":\"example.test/app\",\"Test\":\"TestFails\",\"Elapsed\":0.01}\n{\"Action\":\"fail\",\"Package\":\"example.test/app\",\"Elapsed\":0.01}")
            };
            foreach (var sample in samples)
            {
                Add(sample.Id + "_clean", () => { var r = FleetActionParserRegistry.Get(sample.Id)!.Parse(sample.Clean); AssertTrue(r.Parsed, "recognized clean report"); AssertEqual(0, r.Total); });
                Add(sample.Id + "_findings", () => { var r = FleetActionParserRegistry.Get(sample.Id)!.Parse(sample.Findings); AssertTrue(r.Parsed, "recognized findings"); AssertEqual(1, r.Total); AssertFalse(r.Summary.Contains("never-persist-this-secret"), "secret absent from summary"); });
                Add(sample.Id + "_malformed", () => { var r = FleetActionParserRegistry.Get(sample.Id)!.Parse("malformed output"); AssertFalse(r.Parsed, "invalid report"); AssertEqual(0, r.Total); });
                Add(sample.Id + "_truncated", () => { var r = FleetActionParserRegistry.Get(sample.Id)!.Parse(sample.Findings.Substring(0, sample.Findings.Length / 2)); AssertFalse(r.Parsed, "truncated report"); });
                Add(sample.Id + "_empty", () => { var r = FleetActionParserRegistry.Get(sample.Id)!.Parse(""); AssertFalse(r.Parsed, "empty report"); AssertEqual(0, r.Total); });
                Add(sample.Id + "_cancelled", () => { using CancellationTokenSource ct = new(); ct.Cancel(); AssertThrows<OperationCanceledException>(() => FleetActionParserRegistry.Get(sample.Id)!.Parse(sample.Findings, ct.Token)); });
            }
            Add("go_test_eof_without_terminal", () => {
                var r = FleetActionParserRegistry.Get("go-test-json")!.Parse("{\"Action\":\"start\",\"Package\":\"app\"}\n{\"Action\":\"run\",\"Package\":\"app\",\"Test\":\"TestA\"}\n{\"Action\":\"pass\",\"Package\":\"app\",\"Test\":\"TestA\"}");
                AssertFalse(r.Parsed, "EOF with the package still running is incomplete");
            });
            Add("govulncheck_pretty_json_sequence", () => {
                var r = FleetActionParserRegistry.Get("govulncheck-json")!.Parse("{\n  \"config\": {\"protocol_version\": \"v1.0.0\"}\n}\n{\n  \"finding\": {\"osv\":\"GO-2026-1\"}\n}");
                AssertTrue(r.Parsed, "native pretty-printed JSON sequence"); AssertEqual(1, r.Total);
            });
            Add("parser_cancelled_during_large_report", () => {
                string output = "[" + String.Join(",", System.Linq.Enumerable.Repeat("{\"RuleID\":\"generic-key\",\"Secret\":\"sensitive-value\"}", 250000)) + "]";
                using CancellationTokenSource deadline = new();
                deadline.CancelAfter(1);
                AssertThrows<OperationCanceledException>(() => FleetActionParserRegistry.Get("gitleaks-json")!.Parse(output, deadline.Token));
            });
            Add("severity_counts", () => {
                var r = FleetActionParserRegistry.Get("npm-audit-json")!.Parse("{\"metadata\":{\"vulnerabilities\":{\"critical\":1,\"high\":2,\"moderate\":3,\"low\":4,\"total\":10}}}");
                AssertEqual(10, r.Total); AssertEqual(1, r.Critical); AssertEqual(2, r.High); AssertEqual(3, r.Medium); AssertEqual(4, r.Low);
            });
            Add("native_cvss_severity", () => {
                var r = FleetActionParserRegistry.Get("cargo-audit-json")!.Parse("{\"vulnerabilities\":{\"list\":[{\"advisory\":{\"cvss\":\"CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H\"}}]}}");
                AssertEqual(1, r.Critical); AssertEqual(0, r.Unrated);
                var osv = FleetActionParserRegistry.Get("osv-scanner-json")!.Parse("{\"results\":[{\"packages\":[{\"vulnerabilities\":[{\"severity\":[{\"type\":\"CVSS_V3\",\"score\":\"CVSS:3.1/AV:N/AC:L/PR:N/UI:R/S:U/C:H/I:H/A:H\"}]}]}]}]}");
                AssertEqual(1, osv.High); AssertEqual(0, osv.Unrated);
            });
            return new TestSuiteDescriptor("Services.FleetActionOutputParsers", "Fleet action output parser formats", cases);
            void Add(string id, Action body) => cases.Add(new TestCaseDescriptor("Services.FleetActionOutputParsers", id, id, ct => { body(); return Task.CompletedTask; }));
        }
    }
}
