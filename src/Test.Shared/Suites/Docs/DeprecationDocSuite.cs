namespace Test.Shared.Suites.Docs
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.ApiSurface;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// DEPRECATION.md is the managed list of deprecations: its Active deprecations table and
    /// <see cref="DeprecatedSurface"/> must name the same REST routes and MCP tools with the same replacements, every row
    /// must carry an ID, a deprecation release, and a removal release, and the CHANGELOG must announce each item.
    /// </summary>
    public sealed class DeprecationDocSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Docs.Deprecation";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the suite descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(new TestCaseDescriptor(SuiteId, "doc_matches_deprecated_surface", "DEPRECATION.md and DeprecatedSurface list the same routes and tools with the same replacements", (CancellationToken ct) =>
            {
                List<DeprecationRow> rows = ReadRows();
                AssertTrue(rows.Count > 0, "DEPRECATION.md has active deprecations");
                List<string> problems = new List<string>();

                Dictionary<string, DeprecationRow> rest = rows.Where(r => r.Surface == "REST").ToDictionary(r => r.Item, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, DeprecationRow> mcp = rows.Where(r => r.Surface == "MCP").ToDictionary(r => r.Item, StringComparer.Ordinal);

                foreach (string route in DeprecatedSurface.Routes())
                {
                    if (!rest.TryGetValue(route, out DeprecationRow? row)) { problems.Add("DeprecatedSurface route missing from DEPRECATION.md: " + route); continue; }
                    string[] parts = route.Split(' ');
                    string replacement = DeprecatedSurface.RouteReplacement(parts[0], parts[1])!;
                    if (!String.Equals(row.Replacement, replacement, StringComparison.Ordinal)) problems.Add(route + ": replacement differs (doc '" + row.Replacement + "', code '" + replacement + "')");
                }

                foreach (string route in rest.Keys)
                {
                    string[] parts = route.Split(' ');
                    if (parts.Length != 2 || DeprecatedSurface.RouteReplacement(parts[0], parts[1]) == null) problems.Add("DEPRECATION.md route not in DeprecatedSurface: " + route);
                }

                foreach (string tool in DeprecatedSurface.Tools())
                {
                    if (!mcp.TryGetValue(tool, out DeprecationRow? row)) { problems.Add("DeprecatedSurface tool missing from DEPRECATION.md: " + tool); continue; }
                    if (!String.Equals(row.Replacement, DeprecatedSurface.ToolReplacement(tool), StringComparison.Ordinal)) problems.Add(tool + ": replacement differs");
                }

                foreach (string tool in mcp.Keys)
                {
                    if (DeprecatedSurface.ToolReplacement(tool) == null) problems.Add("DEPRECATION.md tool not in DeprecatedSurface: " + tool);
                }

                AssertTrue(problems.Count == 0, "DEPRECATION.md and DeprecatedSurface differ:\n" + String.Join("\n", problems));
                return Task.CompletedTask;
            }));

            cases.Add(new TestCaseDescriptor(SuiteId, "rows_are_complete_and_announced", "Every active deprecation has a unique ID, a deprecation release, removal in the next major, and a CHANGELOG entry", (CancellationToken ct) =>
            {
                List<DeprecationRow> rows = ReadRows();
                string changelog = File.ReadAllText(Path.Combine(DashboardSource.RepoRoot(), "CHANGELOG.md"));
                List<string> problems = new List<string>();
                foreach (IGrouping<string, DeprecationRow> duplicate in rows.GroupBy(r => r.Id).Where(g => g.Count() > 1)) problems.Add("duplicate ID " + duplicate.Key);
                foreach (DeprecationRow row in rows)
                {
                    if (!Regex.IsMatch(row.Id, "^D-\\d{3}$")) problems.Add(row.Item + ": ID '" + row.Id + "'");
                    if (String.IsNullOrWhiteSpace(row.DeprecatedIn)) problems.Add(row.Id + ": no deprecation release");
                    if (!row.RemovedIn.StartsWith(DeprecatedSurface.RemovedIn, StringComparison.Ordinal)) problems.Add(row.Id + ": removal '" + row.RemovedIn + "' is not " + DeprecatedSurface.RemovedIn);
                    if (!row.Clients.StartsWith("Migrated", StringComparison.Ordinal)) problems.Add(row.Id + ": first-party clients not migrated ('" + row.Clients + "')");
                    if (row.Surface != "Behavior" && changelog.IndexOf(row.Item, StringComparison.Ordinal) < 0) problems.Add(row.Id + ": the CHANGELOG does not announce " + row.Item);
                }

                AssertTrue(problems.Count == 0, "DEPRECATION.md rows:\n" + String.Join("\n", problems));
                return Task.CompletedTask;
            }));

            return new TestSuiteDescriptor(SuiteId, "DEPRECATION.md is the managed deprecation list", cases);
        }

        #endregion

        #region Private-Methods

        private static List<DeprecationRow> ReadRows()
        {
            string text = File.ReadAllText(Path.Combine(DashboardSource.RepoRoot(), "DEPRECATION.md"));
            int start = text.IndexOf("## Active deprecations", StringComparison.Ordinal);
            int end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
            AssertTrue(start >= 0 && end > start, "DEPRECATION.md has an Active deprecations section");
            List<DeprecationRow> rows = new List<DeprecationRow>();
            foreach (string line in text.Substring(start, end - start).Split('\n'))
            {
                if (!line.StartsWith("| D-", StringComparison.Ordinal)) continue;
                string[] cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                AssertTrue(cells.Length == 7, "seven columns: " + line);
                rows.Add(new DeprecationRow
                {
                    Id = cells[0],
                    Surface = cells[1],
                    Item = Unquote(cells[2]),
                    Replacement = Unquote(cells[3]),
                    DeprecatedIn = cells[4],
                    RemovedIn = cells[5],
                    Clients = cells[6]
                });
            }

            return rows;
        }

        private static string Unquote(string cell)
        {
            Match code = Regex.Match(cell, "^`([^`]*)`");
            return code.Success ? code.Groups[1].Value : cell;
        }

        #endregion
    }
}
