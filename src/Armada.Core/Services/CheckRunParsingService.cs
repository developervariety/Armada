namespace Armada.Core.Services
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Xml.Linq;
    using Armada.Core.Models;

    /// <summary>
    /// Parses structured test-result and coverage summaries from check-run output and artifacts.
    /// </summary>
    public static class CheckRunParsingService
    {
        private static readonly Regex _DotNetSummaryRegex = new Regex(
            @"(?im)^(?:Passed|Failed)!\s*-\s*Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+),\s*Total:\s*(?<total>\d+),\s*Duration:\s*(?<duration>[^\r\n]+)\r?$",
            RegexOptions.Compiled);

        private static readonly Regex _PytestSummaryRegex = new Regex(
            @"(?im)^=+\s*(?<body>.+?)\s+in\s+(?<duration>[0-9A-Za-z\.\:\s]+)\s*=+\r?$",
            RegexOptions.Compiled);

        private static readonly Regex _PytestTokenRegex = new Regex(
            @"(?<count>\d+)\s+(?<kind>failed|passed|skipped|error|errors|xfailed|xpassed)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Jest prints "Tests:       1 failed, 6 passed, 7 total" (after a "Test Suites:" line); Vitest prints
        // "      Tests  1 failed | 6 passed (7)" (after a "Test Files" line). Only the per-test line is wanted.
        private static readonly Regex _JavascriptTestsLineRegex = new Regex(
            @"(?im)^[ \t]*Tests?(?![ \t]+(?:Suites|Files)\b)[ \t]*:?[ \t]+(?<body>[^\r\n]*?)[ \t]*\r?$",
            RegexOptions.Compiled);

        private static readonly Regex _VitestTotalRegex = new Regex(
            @"\((?<total>\d+)\)\s*$",
            RegexOptions.Compiled);

        private static readonly Regex _JavascriptDurationLineRegex = new Regex(
            @"(?im)^[ \t]*(?:Duration:?|Time:)[ \t]+(?<value>\d+(?:\.\d+)?[ \t]*(?:ms|s|m)\b)",
            RegexOptions.Compiled);

        private static readonly Regex _JavascriptTokenRegex = new Regex(
            @"(?<count>\d+)\s+(?<kind>failed|passed|skipped|todo|total)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _CargoSummaryRegex = new Regex(
            @"(?im)^test result:\s+(?<result>ok|failed)\.\s+(?<passed>\d+)\s+passed;\s+(?<failed>\d+)\s+failed;\s+(?<ignored>\d+)\s+ignored(?:;\s+(?<measured>\d+)\s+measured)?(?:;\s+(?<filtered>\d+)\s+filtered out)?(?:;\s+finished in\s+(?<duration>[^\r\n]+))?",
            RegexOptions.Compiled);

        private static readonly Regex _MavenSurefireSummaryRegex = new Regex(
            @"(?im)^Tests run:\s*(?<total>\d+),\s*Failures:\s*(?<failures>\d+),\s*Errors:\s*(?<errors>\d+),\s*Skipped:\s*(?<skipped>\d+)(?:,\s*Time elapsed:\s*(?<duration>[0-9A-Za-z\.\s:]+))?",
            RegexOptions.Compiled);

        private static readonly Regex _NUnitConsoleSummaryRegex = new Regex(
            @"(?im)^Test Count:\s*(?<total>\d+),\s*Passed:\s*(?<passed>\d+),\s*Failed:\s*(?<failed>\d+),\s*Warnings:\s*(?<warnings>\d+),\s*Inconclusive:\s*(?<inconclusive>\d+),\s*Skipped:\s*(?<skipped>\d+)",
            RegexOptions.Compiled);

        private static readonly Regex _ConsoleTablePrefixRegex = new Regex(
            @"(?m)^[ \t]*(?:unit(?:-shard-[1-9]\d*)?|automated|runtimes|shared)[ \t]+Total:",
            RegexOptions.Compiled);

        private static readonly Regex _ConsoleTableRowRegex = new Regex(
            @"(?m)^[ \t]*(?<name>unit(?:-shard-[1-9]\d*)?|automated|runtimes|shared)[ \t]+Total:[ \t]*(?<total>\d+)[ \t]+Passed:[ \t]*(?<passed>\d+)[ \t]+Failed:[ \t]*(?<failed>\d+)[ \t]+Skipped:[ \t]*(?<skipped>\d+)(?:[ \t]+Runtime:[ \t]*\d+ms)?(?:[ \t]+\((?<seen>\d+) of (?<expected>\d+) shards summarised\))?[ \t]*\r?$",
            RegexOptions.Compiled);

        private static readonly Regex _DurationTokenRegex = new Regex(
            @"(?<value>\d+(?:\.\d+)?)\s*(?<unit>ms|milliseconds?|s|sec|secs|seconds?|m|min|mins|minutes?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Parse a test summary from command output when possible.
        /// </summary>
        public static CheckRunTestSummary? ParseTestSummary(string? output)
        {
            return ParseTestSummary(output, null, null);
        }

        /// <summary>
        /// Parse a test summary from collected artifacts, with command output as fallback.
        /// Callers collecting local artifacts must supply only files produced by the current execution.
        /// </summary>
        public static CheckRunTestSummary? ParseTestSummary(string? output, string? workingDirectory, IEnumerable<CheckRunArtifact>? artifacts)
        {
            CheckRunTestSummary? artifactSummary = ParseTestSummaryFromArtifacts(workingDirectory, artifacts);
            if (artifactSummary != null) return artifactSummary;

            CheckRunTestSummary? outputSummary = null;
            if (String.IsNullOrWhiteSpace(output))
            {
                outputSummary = null;
            }
            else
            {
                CheckRunTestSummary? table = ParseConsoleTable(output, out string remainder);
                if (table == null && _ConsoleTablePrefixRegex.IsMatch(output)) return null;
                if (table == null) outputSummary = ParseNonConsoleSummary(output);
                else
                {
                    List<CheckRunTestSummary> outside = new[]
                    {
                        ParseDotNetSummary(remainder), ParsePytestSummary(remainder), ParseJavascriptSummary(remainder),
                        ParseCargoSummary(remainder), ParseMavenSurefireSummary(remainder), ParseNUnitConsoleSummary(remainder)
                    }.Where(summary => summary != null).Select(summary => summary!).ToList();
                    // Several outside formats can describe overlapping runs. Leave their combined
                    // counts unknown rather than hide a failure through parser precedence.
                    if (outside.Count > 1) return null;
                    if (outside.Count == 0) return table;
                    outputSummary = outside[0];
                    // Concurrent shard runtimes are not this combined run's duration.
                    try
                    {
                        checked
                        {
                            return new CheckRunTestSummary
                            {
                                Format = "combined", Total = table.Total + outputSummary.Total,
                                Passed = table.Passed + outputSummary.Passed, Failed = table.Failed + outputSummary.Failed,
                                Skipped = table.Skipped + outputSummary.Skipped
                            };
                        }
                    }
                    catch (OverflowException) { return null; }
                }
            }

            if (outputSummary != null)
                return outputSummary;

            return null;
        }

        /// <summary>
        /// Describe per-test timing from the fresh TRX artifacts of a run: the test count, each assembly's summed
        /// test time slowest first, and the ten slowest tests. The description never throws: without a TRX artifact
        /// it says so, and a TRX artifact that cannot be read is named with its reason.
        /// </summary>
        /// <param name="workingDirectory">Directory the artifact paths are relative to.</param>
        /// <param name="artifacts">The run's fresh artifacts.</param>
        /// <returns>One line of text without a trailing newline.</returns>
        public static string DescribeTestTiming(string? workingDirectory, IEnumerable<CheckRunArtifact>? artifacts)
        {
            const string none = "none (no fresh TRX artifact)";
            if (String.IsNullOrWhiteSpace(workingDirectory) || artifacts == null) return none;

            string root;
            try
            {
                root = Path.GetFullPath(workingDirectory);
            }
            catch (Exception ex)
            {
                return "unreadable (working directory: " + OneLine(ex.Message) + ")";
            }

            Dictionary<string, long> assemblyMs = new Dictionary<string, long>(StringComparer.Ordinal);
            List<KeyValuePair<string, long>> tests = new List<KeyValuePair<string, long>>();
            bool found = false;
            foreach (CheckRunArtifact artifact in artifacts)
            {
                if (String.IsNullOrWhiteSpace(artifact.Path)) continue;
                if (!Path.GetExtension(artifact.Path).Equals(".trx", StringComparison.OrdinalIgnoreCase)) continue;
                string? fullPath = PathContainment.TryResolve(root, artifact.Path);
                if (fullPath == null || !File.Exists(fullPath)) continue;

                try
                {
                    XDocument document = XDocument.Load(fullPath);
                    Dictionary<string, string> storageById = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (XElement definition in document.Descendants().Where(node => node.Name.LocalName == "UnitTest"))
                    {
                        string? id = definition.Attribute("id")?.Value;
                        string? storage = definition.Attribute("storage")?.Value;
                        if (!String.IsNullOrEmpty(id) && !String.IsNullOrEmpty(storage)) storageById[id] = storage;
                    }

                    foreach (XElement result in document.Descendants().Where(node => node.Name.LocalName == "UnitTestResult"))
                    {
                        if (!TimeSpan.TryParse(result.Attribute("duration")?.Value, CultureInfo.InvariantCulture, out TimeSpan duration)) continue;
                        long ms = Convert.ToInt64(Math.Round(duration.TotalMilliseconds));
                        string name = result.Attribute("testName")?.Value ?? "unnamed";
                        string? testId = result.Attribute("testId")?.Value;
                        string assembly = testId != null && storageById.TryGetValue(testId, out string? storagePath)
                            ? Path.GetFileName(storagePath.Replace('\\', '/'))
                            : "unknown";
                        assemblyMs[assembly] = assemblyMs.TryGetValue(assembly, out long sum) ? sum + ms : ms;
                        tests.Add(new KeyValuePair<string, long>(name, ms));
                    }

                    found = true;
                }
                catch (Exception ex)
                {
                    return "unreadable (" + artifact.Path.Replace('\\', '/') + ": " + OneLine(ex.Message) + ")";
                }
            }

            if (!found) return none;

            string assemblies = String.Join(", ", assemblyMs
                .OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => OneLine(pair.Key) + "=" + pair.Value.ToString(CultureInfo.InvariantCulture) + "ms"));
            string slowest = String.Join(", ", tests
                .OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(10)
                .Select(pair => OneLine(pair.Key) + "=" + pair.Value.ToString(CultureInfo.InvariantCulture) + "ms"));
            return "tests=" + tests.Count.ToString(CultureInfo.InvariantCulture)
                + "; assemblies=" + (assemblies.Length == 0 ? "none" : assemblies)
                + "; slowest=" + (slowest.Length == 0 ? "none" : slowest);
        }

        /// <summary>
        /// Parse a coverage summary from collected artifacts when possible.
        /// </summary>
        public static CheckRunCoverageSummary? ParseCoverageSummary(string? workingDirectory, IEnumerable<CheckRunArtifact>? artifacts)
        {
            if (String.IsNullOrWhiteSpace(workingDirectory) || artifacts == null)
                return null;

            string root = Path.GetFullPath(workingDirectory);
            foreach (CheckRunArtifact artifact in artifacts)
            {
                if (String.IsNullOrWhiteSpace(artifact.Path))
                    continue;

                try
                {
                    string? fullPath = PathContainment.TryResolve(root, artifact.Path);
                    if (fullPath == null)
                        continue;
                    if (!File.Exists(fullPath))
                        continue;

                    CheckRunCoverageSummary? parsed = ParseCoverageArtifact(fullPath, artifact.Path.Replace('\\', '/'));
                    if (parsed != null)
                        return parsed;
                }
                catch
                {
                }
            }

            return null;
        }

        /// <summary>Read the run-tests console table once, sharing its row ownership with the test digest.</summary>
        internal static CheckRunTestSummary? ParseConsoleTable(string output, out string remainder)
        {
            remainder = output;
            MatchCollection matches = _ConsoleTableRowRegex.Matches(output);
            if (matches.Count == 0 || matches.Count != _ConsoleTablePrefixRegex.Matches(output).Count) return null;
            Dictionary<string, CheckRunTestSummary> rows = new Dictionary<string, CheckRunTestSummary>(StringComparer.Ordinal);
            Match? aggregate = null;
            foreach (Match match in matches)
            {
                if (!Int32.TryParse(match.Groups["total"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int total)
                    || !Int32.TryParse(match.Groups["passed"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int passed)
                    || !Int32.TryParse(match.Groups["failed"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int failed)
                    || !Int32.TryParse(match.Groups["skipped"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int skipped)
                    || (long)passed + failed + skipped != total) return null;
                string name = match.Groups["name"].Value;
                if (!rows.TryAdd(name, new CheckRunTestSummary { Total = total, Passed = passed, Failed = failed, Skipped = skipped })) return null;
                if (name == "unit") aggregate = match;
            }
            List<CheckRunTestSummary> shards = rows.Where(row => row.Key.StartsWith("unit-shard-", StringComparison.Ordinal)).Select(row => row.Value).ToList();
            if (aggregate != null)
            {
                CheckRunTestSummary unit = rows["unit"];
                if (aggregate.Groups["seen"].Success
                    && (!Int32.TryParse(aggregate.Groups["seen"].Value, out int seen)
                        || !Int32.TryParse(aggregate.Groups["expected"].Value, out int expected)
                        || seen != expected || (shards.Count > 0 && seen != shards.Count))) return null;
                if (shards.Count > 0 && (shards.Sum(row => (long)row.Total!.Value) != unit.Total
                    || shards.Sum(row => (long)row.Passed!.Value) != unit.Passed
                    || shards.Sum(row => (long)row.Failed!.Value) != unit.Failed
                    || shards.Sum(row => (long)row.Skipped!.Value) != unit.Skipped)) return null;
            }
            List<CheckRunTestSummary> selected = rows.Where(row => aggregate == null || !row.Key.StartsWith("unit-shard-", StringComparison.Ordinal)).Select(row => row.Value).ToList();
            long allTotal = selected.Sum(row => (long)row.Total!.Value);
            if (allTotal > Int32.MaxValue) return null;
            remainder = _ConsoleTableRowRegex.Replace(output, String.Empty);
            return new CheckRunTestSummary
            {
                Format = "console", Total = (int)allTotal,
                Passed = (int)selected.Sum(row => (long)row.Passed!.Value),
                Failed = (int)selected.Sum(row => (long)row.Failed!.Value),
                Skipped = (int)selected.Sum(row => (long)row.Skipped!.Value)
            };
        }

        /// <summary>Whether output contains a console table row, including one with invalid counts.</summary>
        internal static bool HasConsoleTableRows(string output) => _ConsoleTablePrefixRegex.IsMatch(output);

        private static CheckRunTestSummary? ParseNonConsoleSummary(string output)
        {
            return ParseDotNetSummary(output) ?? ParsePytestSummary(output) ?? ParseJavascriptSummary(output)
                ?? ParseCargoSummary(output) ?? ParseMavenSurefireSummary(output) ?? ParseNUnitConsoleSummary(output);
        }

        private static CheckRunTestSummary? ParseDotNetSummary(string output)
        {
            // dotnet test emits one Passed!/Failed! line per test project, so a multi-project run
            // produces several summary lines and no single line describes the run. Aggregate every
            // line: reading only one of them reports that project's counts as the whole suite, which
            // hides a failure in any other project behind a clean-looking count.
            MatchCollection matches = _DotNetSummaryRegex.Matches(output);
            if (matches.Count == 0)
                return null;

            int failed = 0;
            int passed = 0;
            int skipped = 0;
            int total = 0;
            long longestDurationMs = 0;
            bool anyDuration = false;

            foreach (Match match in matches)
            {
                failed += ParseInt(match, "failed");
                passed += ParseInt(match, "passed");
                skipped += ParseInt(match, "skipped");
                total += ParseInt(match, "total");

                long? duration = ParseDurationMilliseconds(match.Groups["duration"].Value);
                if (duration.HasValue)
                {
                    anyDuration = true;
                    if (duration.Value > longestDurationMs)
                        longestDurationMs = duration.Value;
                }
            }

            return new CheckRunTestSummary
            {
                Format = "dotnet",
                Failed = failed,
                Passed = passed,
                Skipped = skipped,
                Total = total,

                // Test projects run concurrently, so the longest project approximates elapsed time
                // while a sum would overstate it. Counts aggregate; duration does not.
                DurationMs = anyDuration ? longestDurationMs : null
            };
        }

        private static CheckRunTestSummary? ParsePytestSummary(string output)
        {
            Match match = _PytestSummaryRegex.Matches(output).Cast<Match>().LastOrDefault(m => m.Success) ?? Match.Empty;
            if (!match.Success)
                return null;

            int passed = 0;
            int failed = 0;
            int skipped = 0;
            foreach (Match token in _PytestTokenRegex.Matches(match.Groups["body"].Value))
            {
                int count = Int32.Parse(token.Groups["count"].Value, CultureInfo.InvariantCulture);
                string kind = token.Groups["kind"].Value.ToLowerInvariant();
                switch (kind)
                {
                    case "passed":
                    case "xpassed":
                        passed += count;
                        break;
                    case "failed":
                    case "error":
                    case "errors":
                    case "xfailed":
                        failed += count;
                        break;
                    case "skipped":
                        skipped += count;
                        break;
                }
            }

            int total = passed + failed + skipped;
            if (total == 0)
                return null;

            return new CheckRunTestSummary
            {
                Format = "pytest",
                Passed = passed,
                Failed = failed,
                Skipped = skipped,
                Total = total,
                DurationMs = ParseDurationMilliseconds(match.Groups["duration"].Value)
            };
        }

        private static CheckRunTestSummary? ParseJavascriptSummary(string output)
        {
            // The last per-test summary line that carries counts wins (watch mode and reruns print several).
            List<Match> lines = _JavascriptTestsLineRegex.Matches(output).Cast<Match>().Where(m => m.Success).ToList();
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                CheckRunTestSummary? summary = ParseJavascriptTestsLine(lines[i].Groups["body"].Value);
                if (summary == null)
                    continue;

                Match durationMatch = _JavascriptDurationLineRegex.Matches(output).Cast<Match>().LastOrDefault(m => m.Success) ?? Match.Empty;
                if (durationMatch.Success)
                    summary.DurationMs = ParseDurationMilliseconds(durationMatch.Groups["value"].Value);
                return summary;
            }

            return null;
        }

        private static CheckRunTestSummary? ParseJavascriptTestsLine(string body)
        {
            int? passed = null;
            int? failed = null;
            int? skipped = null;
            int? total = null;

            foreach (Match token in _JavascriptTokenRegex.Matches(body))
            {
                int count = Int32.Parse(token.Groups["count"].Value, CultureInfo.InvariantCulture);
                switch (token.Groups["kind"].Value.ToLowerInvariant())
                {
                    case "passed":
                        passed = (passed ?? 0) + count;
                        break;
                    case "failed":
                        failed = (failed ?? 0) + count;
                        break;
                    case "skipped":
                    case "todo":
                        skipped = (skipped ?? 0) + count;
                        break;
                    case "total":
                        total = count;
                        break;
                }
            }

            if (!passed.HasValue && !failed.HasValue && !skipped.HasValue)
                return null;

            if (!total.HasValue)
            {
                Match vitestTotal = _VitestTotalRegex.Match(body);
                if (vitestTotal.Success)
                    total = Int32.Parse(vitestTotal.Groups["total"].Value, CultureInfo.InvariantCulture);
            }

            total ??= (passed ?? 0) + (failed ?? 0) + (skipped ?? 0);

            return new CheckRunTestSummary
            {
                Format = "javascript",
                Passed = passed ?? 0,
                Failed = failed ?? 0,
                Skipped = skipped ?? 0,
                Total = total
            };
        }

        private static CheckRunTestSummary? ParseCargoSummary(string output)
        {
            Match match = _CargoSummaryRegex.Matches(output).Cast<Match>().LastOrDefault(candidate => candidate.Success) ?? Match.Empty;
            if (!match.Success)
                return null;

            int passed = ParseInt(match, "passed");
            int failed = ParseInt(match, "failed");
            int ignored = ParseInt(match, "ignored");
            int total = passed + failed + ignored;

            return new CheckRunTestSummary
            {
                Format = "cargo",
                Passed = passed,
                Failed = failed,
                Skipped = ignored,
                Total = total,
                DurationMs = ParseDurationMilliseconds(match.Groups["duration"].Value)
            };
        }

        private static CheckRunTestSummary? ParseMavenSurefireSummary(string output)
        {
            Match match = _MavenSurefireSummaryRegex.Matches(output).Cast<Match>().LastOrDefault(candidate => candidate.Success) ?? Match.Empty;
            if (!match.Success)
                return null;

            int total = ParseInt(match, "total");
            int failures = ParseInt(match, "failures");
            int errors = ParseInt(match, "errors");
            int skipped = ParseInt(match, "skipped");
            int failed = failures + errors;
            int passed = Math.Max(0, total - failed - skipped);

            return new CheckRunTestSummary
            {
                Format = "maven-surefire",
                Passed = passed,
                Failed = failed,
                Skipped = skipped,
                Total = total,
                DurationMs = ParseDurationMilliseconds(match.Groups["duration"].Value)
            };
        }

        private static CheckRunTestSummary? ParseNUnitConsoleSummary(string output)
        {
            Match match = _NUnitConsoleSummaryRegex.Matches(output).Cast<Match>().LastOrDefault(candidate => candidate.Success) ?? Match.Empty;
            if (!match.Success)
                return null;

            int total = ParseInt(match, "total");
            int passed = ParseInt(match, "passed");
            int failed = ParseInt(match, "failed");
            int skipped = ParseInt(match, "skipped") + ParseInt(match, "inconclusive") + ParseInt(match, "warnings");

            return new CheckRunTestSummary
            {
                Format = "nunit-console",
                Passed = passed,
                Failed = failed,
                Skipped = skipped,
                Total = total
            };
        }

        private static CheckRunCoverageSummary? ParseCoverageArtifact(string fullPath, string relativePath)
        {
            string extension = Path.GetExtension(fullPath);
            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                return ParseCoverageXml(fullPath, relativePath);
            }

            if (extension.Equals(".info", StringComparison.OrdinalIgnoreCase))
            {
                return ParseLcov(fullPath, relativePath);
            }

            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                return ParseCoverageJson(fullPath, relativePath);
            }

            return null;
        }

        private static CheckRunTestSummary? ParseTestSummaryFromArtifacts(string? workingDirectory, IEnumerable<CheckRunArtifact>? artifacts)
        {
            if (String.IsNullOrWhiteSpace(workingDirectory) || artifacts == null)
                return null;

            string root = Path.GetFullPath(workingDirectory);
            foreach (CheckRunArtifact artifact in artifacts)
            {
                if (String.IsNullOrWhiteSpace(artifact.Path))
                    continue;

                try
                {
                    string? fullPath = PathContainment.TryResolve(root, artifact.Path);
                    if (fullPath == null)
                        continue;
                    if (!File.Exists(fullPath))
                        continue;

                    CheckRunTestSummary? summary = ParseTestArtifact(fullPath);
                    if (summary != null)
                        return summary;
                }
                catch
                {
                }
            }

            return null;
        }

        private static CheckRunTestSummary? ParseTestArtifact(string fullPath)
        {
            string extension = Path.GetExtension(fullPath);
            if (!extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".trx", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            XDocument document = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
            string rootName = document.Root?.Name.LocalName ?? String.Empty;
            if (rootName.Equals("TestRun", StringComparison.OrdinalIgnoreCase))
                return ParseTrx(document);
            if (rootName.Equals("test-run", StringComparison.OrdinalIgnoreCase))
                return ParseNUnitXml(document);
            if (rootName.Equals("testsuite", StringComparison.OrdinalIgnoreCase)
                || rootName.Equals("testsuites", StringComparison.OrdinalIgnoreCase))
            {
                return ParseJUnit(document);
            }

            return null;
        }

        private static CheckRunTestSummary? ParseTrx(XDocument document)
        {
            XElement? counters = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "Counters");
            if (counters == null)
                return null;

            int? total = ParseNullableInt(counters.Attribute("total")?.Value);
            int? passed = ParseNullableInt(counters.Attribute("passed")?.Value);
            int? failed = ParseNullableInt(counters.Attribute("failed")?.Value);
            int? notExecuted = ParseNullableInt(counters.Attribute("notExecuted")?.Value);
            if (!total.HasValue && !passed.HasValue && !failed.HasValue && !notExecuted.HasValue)
                return null;

            return new CheckRunTestSummary
            {
                Format = "trx",
                Total = total,
                Passed = passed,
                Failed = failed,
                Skipped = notExecuted
            };
        }

        private static CheckRunTestSummary? ParseJUnit(XDocument document)
        {
            IEnumerable<XElement> suites = document.Root?.Name.LocalName.Equals("testsuite", StringComparison.OrdinalIgnoreCase) == true
                ? new[] { document.Root! }
                : document.Descendants().Where(node => node.Name.LocalName == "testsuite");

            int total = 0;
            int failed = 0;
            int skipped = 0;
            double durationSeconds = 0;
            bool found = false;

            foreach (XElement suite in suites)
            {
                int? tests = ParseNullableInt(suite.Attribute("tests")?.Value);
                int? failures = ParseNullableInt(suite.Attribute("failures")?.Value);
                int? errors = ParseNullableInt(suite.Attribute("errors")?.Value);
                int? skippedCount = ParseNullableInt(suite.Attribute("skipped")?.Value);
                double? time = ParseNullableDouble(suite.Attribute("time")?.Value, scaleFromFraction: false);

                if (!tests.HasValue && !failures.HasValue && !errors.HasValue && !skippedCount.HasValue)
                    continue;

                found = true;
                total += tests ?? 0;
                failed += (failures ?? 0) + (errors ?? 0);
                skipped += skippedCount ?? 0;
                durationSeconds += time ?? 0;
            }

            if (!found)
                return null;

            int passed = Math.Max(0, total - failed - skipped);
            return new CheckRunTestSummary
            {
                Format = "junit",
                Total = total,
                Passed = passed,
                Failed = failed,
                Skipped = skipped,
                DurationMs = Convert.ToInt64(Math.Round(durationSeconds * 1000.0))
            };
        }

        private static CheckRunTestSummary? ParseNUnitXml(XDocument document)
        {
            XElement? root = document.Root;
            if (root == null)
                return null;

            int? total = ParseNullableInt(root.Attribute("total")?.Value);
            int? passed = ParseNullableInt(root.Attribute("passed")?.Value);
            int? failed = ParseNullableInt(root.Attribute("failed")?.Value);
            int? inconclusive = ParseNullableInt(root.Attribute("inconclusive")?.Value);
            int? skipped = ParseNullableInt(root.Attribute("skipped")?.Value);
            int? warnings = ParseNullableInt(root.Attribute("warnings")?.Value);

            if (!total.HasValue && !passed.HasValue && !failed.HasValue && !inconclusive.HasValue && !skipped.HasValue && !warnings.HasValue)
                return null;

            int aggregateSkipped = (skipped ?? 0) + (inconclusive ?? 0) + (warnings ?? 0);
            long? durationMs = null;
            double? durationSeconds = ParseNullableDouble(root.Attribute("duration")?.Value, scaleFromFraction: false);
            if (durationSeconds.HasValue)
                durationMs = Convert.ToInt64(Math.Round(durationSeconds.Value * 1000.0));

            return new CheckRunTestSummary
            {
                Format = "nunit",
                Total = total,
                Passed = passed,
                Failed = failed,
                Skipped = aggregateSkipped,
                DurationMs = durationMs
            };
        }

        private static CheckRunCoverageSummary? ParseCoverageXml(string fullPath, string relativePath)
        {
            XDocument document = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);
            string rootName = document.Root?.Name.LocalName ?? String.Empty;

            if (rootName.Equals("coverage", StringComparison.OrdinalIgnoreCase))
                return ParseCobertura(document, relativePath);
            if (rootName.Equals("report", StringComparison.OrdinalIgnoreCase))
                return ParseJacoco(document, relativePath);
            if (rootName.Equals("CoverageSession", StringComparison.OrdinalIgnoreCase))
                return ParseOpenCover(document, relativePath);

            return null;
        }

        private static CheckRunCoverageSummary? ParseCobertura(XDocument document, string relativePath)
        {
            XElement? root = document.Root;
            if (root == null)
                return null;

            return new CheckRunCoverageSummary
            {
                Format = "cobertura",
                SourcePath = relativePath,
                Lines = CreateMetric(
                    ParseNullableInt(root.Attribute("lines-covered")?.Value),
                    ParseNullableInt(root.Attribute("lines-valid")?.Value),
                    ParseNullableDouble(root.Attribute("line-rate")?.Value, scaleFromFraction: true)),
                Branches = CreateMetric(
                    ParseNullableInt(root.Attribute("branches-covered")?.Value),
                    ParseNullableInt(root.Attribute("branches-valid")?.Value),
                    ParseNullableDouble(root.Attribute("branch-rate")?.Value, scaleFromFraction: true))
            };
        }

        private static CheckRunCoverageSummary? ParseJacoco(XDocument document, string relativePath)
        {
            XElement[] counters = document.Descendants().Where(node => node.Name.LocalName == "counter").ToArray();
            if (counters.Length == 0)
                return null;

            return new CheckRunCoverageSummary
            {
                Format = "jacoco",
                SourcePath = relativePath,
                Lines = CreateMetricFromJaCoCo(counters, "LINE"),
                Branches = CreateMetricFromJaCoCo(counters, "BRANCH"),
                Functions = CreateMetricFromJaCoCo(counters, "METHOD"),
                Statements = CreateMetricFromJaCoCo(counters, "INSTRUCTION")
            };
        }

        private static CheckRunCoverageSummary? ParseOpenCover(XDocument document, string relativePath)
        {
            XElement? summary = document.Descendants().FirstOrDefault(node => node.Name.LocalName == "Summary");
            if (summary == null)
                return null;

            return new CheckRunCoverageSummary
            {
                Format = "opencover",
                SourcePath = relativePath,
                Lines = CreateMetric(
                    ParseNullableInt(summary.Attribute("visitedSequencePoints")?.Value),
                    ParseNullableInt(summary.Attribute("numSequencePoints")?.Value),
                    null),
                Branches = CreateMetric(
                    ParseNullableInt(summary.Attribute("visitedBranchPoints")?.Value),
                    ParseNullableInt(summary.Attribute("numBranchPoints")?.Value),
                    null)
            };
        }

        private static CheckRunCoverageSummary? ParseLcov(string fullPath, string relativePath)
        {
            int? linesTotal = null;
            int? linesCovered = null;
            int? branchesTotal = null;
            int? branchesCovered = null;
            int? functionsTotal = null;
            int? functionsCovered = null;

            foreach (string rawLine in File.ReadAllLines(fullPath))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("LF:", StringComparison.OrdinalIgnoreCase))
                    linesTotal = ParseNullableInt(line.Substring(3));
                else if (line.StartsWith("LH:", StringComparison.OrdinalIgnoreCase))
                    linesCovered = ParseNullableInt(line.Substring(3));
                else if (line.StartsWith("BRF:", StringComparison.OrdinalIgnoreCase))
                    branchesTotal = ParseNullableInt(line.Substring(4));
                else if (line.StartsWith("BRH:", StringComparison.OrdinalIgnoreCase))
                    branchesCovered = ParseNullableInt(line.Substring(4));
                else if (line.StartsWith("FNF:", StringComparison.OrdinalIgnoreCase))
                    functionsTotal = ParseNullableInt(line.Substring(4));
                else if (line.StartsWith("FNH:", StringComparison.OrdinalIgnoreCase))
                    functionsCovered = ParseNullableInt(line.Substring(4));
            }

            if (!linesTotal.HasValue && !branchesTotal.HasValue && !functionsTotal.HasValue)
                return null;

            return new CheckRunCoverageSummary
            {
                Format = "lcov",
                SourcePath = relativePath,
                Lines = CreateMetric(linesCovered, linesTotal, null),
                Branches = CreateMetric(branchesCovered, branchesTotal, null),
                Functions = CreateMetric(functionsCovered, functionsTotal, null)
            };
        }

        private static CheckRunCoverageSummary? ParseCoverageJson(string fullPath, string relativePath)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fullPath));
            if (!document.RootElement.TryGetProperty("total", out JsonElement total))
                return null;

            return new CheckRunCoverageSummary
            {
                Format = "istanbul-summary",
                SourcePath = relativePath,
                Lines = CreateMetricFromIstanbul(total, "lines"),
                Branches = CreateMetricFromIstanbul(total, "branches"),
                Functions = CreateMetricFromIstanbul(total, "functions"),
                Statements = CreateMetricFromIstanbul(total, "statements")
            };
        }

        private static CheckRunCoverageMetric? CreateMetricFromJaCoCo(IEnumerable<XElement> counters, string type)
        {
            XElement? counter = counters.FirstOrDefault(node =>
                node.Attribute("type")?.Value.Equals(type, StringComparison.OrdinalIgnoreCase) == true);
            if (counter == null)
                return null;

            int? missed = ParseNullableInt(counter.Attribute("missed")?.Value);
            int? covered = ParseNullableInt(counter.Attribute("covered")?.Value);
            int? total = missed.HasValue || covered.HasValue ? (missed ?? 0) + (covered ?? 0) : null;
            return CreateMetric(covered, total, null);
        }

        private static CheckRunCoverageMetric? CreateMetricFromIstanbul(JsonElement total, string propertyName)
        {
            if (!total.TryGetProperty(propertyName, out JsonElement metric))
                return null;

            int? covered = metric.TryGetProperty("covered", out JsonElement coveredElement) ? coveredElement.GetInt32() : null;
            int? count = metric.TryGetProperty("total", out JsonElement totalElement) ? totalElement.GetInt32() : null;
            double? percentage = metric.TryGetProperty("pct", out JsonElement pctElement) ? pctElement.GetDouble() : null;
            return CreateMetric(covered, count, percentage);
        }

        private static CheckRunCoverageMetric? CreateMetric(int? covered, int? total, double? percentage)
        {
            if (!covered.HasValue && !total.HasValue && !percentage.HasValue)
                return null;

            if (!percentage.HasValue && covered.HasValue && total.HasValue && total.Value > 0)
                percentage = Math.Round((double)covered.Value / total.Value * 100.0, 2);

            return new CheckRunCoverageMetric
            {
                Covered = covered,
                Total = total,
                Percentage = percentage
            };
        }

        private static int? ParseNullableInt(string? value)
        {
            if (Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return parsed;
            return null;
        }

        private static double? ParseNullableDouble(string? value, bool scaleFromFraction)
        {
            if (!Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                return null;

            if (scaleFromFraction)
                parsed *= 100.0;

            return Math.Round(parsed, 2);
        }

        private static int ParseInt(Match match, string groupName)
        {
            return Int32.Parse(match.Groups[groupName].Value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Text safe inside one semicolon-separated line: line breaks become spaces and semicolons become commas.
        /// </summary>
        private static string OneLine(string text)
        {
            return text.Replace('\r', ' ').Replace('\n', ' ').Replace(';', ',');
        }

        private static long? ParseDurationMilliseconds(string? value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return null;

            string trimmed = value.Trim();
            if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out TimeSpan duration))
                return Convert.ToInt64(Math.Round(duration.TotalMilliseconds));

            double totalMilliseconds = 0;
            bool matched = false;
            foreach (Match token in _DurationTokenRegex.Matches(trimmed))
            {
                matched = true;
                double amount = Double.Parse(token.Groups["value"].Value, CultureInfo.InvariantCulture);
                string unit = token.Groups["unit"].Value.ToLowerInvariant();
                if (unit.StartsWith("ms", StringComparison.Ordinal))
                    totalMilliseconds += amount;
                else if (unit.StartsWith("s", StringComparison.Ordinal) || unit.StartsWith("sec", StringComparison.Ordinal))
                    totalMilliseconds += amount * 1000.0;
                else if (unit.StartsWith("m", StringComparison.Ordinal))
                    totalMilliseconds += amount * 60_000.0;
            }

            return matched ? Convert.ToInt64(Math.Round(totalMilliseconds)) : null;
        }
    }
}
