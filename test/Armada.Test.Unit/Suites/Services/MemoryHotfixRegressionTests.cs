namespace Armada.Test.Unit.Suites.Services
{
    using System.IO;
    using System.Text.RegularExpressions;
    using Armada.Test.Common;

    /// <summary>
    /// Regression tests for memory-sensitive dashboard and mission list surfaces.
    /// </summary>
    public class MemoryHotfixRegressionTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Memory Hotfix Regression";

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("DashboardHomeRefresh DoesNotRequestUnboundedMissionPage", () =>
            {
                string dashboard = ReadRepositoryFile("src", "Armada.Dashboard", "src", "pages", "Dashboard.tsx");
                bool requestsUnboundedMissionPage = Regex.IsMatch(
                    dashboard,
                    @"listMissions\s*\(\s*\{[^}]*pageSize\s*:\s*9999",
                    RegexOptions.Singleline);

                AssertFalse(
                    requestsUnboundedMissionPage,
                    "Dashboard home/refresh must not request an effectively unbounded mission page.");
            });

            await RunTest("DashboardHomeRefresh DoesNotRetainRawMissionPage", () =>
            {
                string dashboard = ReadRepositoryFile("src", "Armada.Dashboard", "src", "pages", "Dashboard.tsx");
                bool retainsRawMissionPage = Regex.IsMatch(
                    dashboard,
                    @"setAllMissions\s*\(\s*missionRes\.objects\s*\)",
                    RegexOptions.Singleline);

                AssertFalse(
                    retainsRawMissionPage,
                    "Dashboard home/refresh must not retain the raw mission page response.");
            });

            await RunTest("MissionEnumerationRoutes DefaultResponsesStripHeavyMissionFields", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "MissionRoutes.cs");
                string listEndpoint = ExtractBetween(routes, "app.Get(\"/api/v1/missions\"", "app.Post<EnumerationQuery>");
                string enumerateEndpoint = ExtractBetween(routes, "app.Post<EnumerationQuery>(\"/api/v1/missions/enumerate\"", "app.Post<Mission>");

                AssertStripsHeavyMissionFields(listEndpoint, "GET /api/v1/missions");
                AssertStripsHeavyMissionFields(enumerateEndpoint, "POST /api/v1/missions/enumerate");
            });
        }

        private void AssertStripsHeavyMissionFields(string endpointBlock, string surface)
        {
            AssertContains("m.DiffSnapshot = null", endpointBlock, surface + " should strip diff snapshots by default.");
            AssertContains("m.Description = null", endpointBlock, surface + " should strip mission descriptions by default.");
            AssertContains("m.AgentOutput = null", endpointBlock, surface + " should strip agent output by default.");
        }

        private static string ExtractBetween(string contents, string startToken, string endToken)
        {
            int start = contents.IndexOf(startToken, StringComparison.Ordinal);
            if (start < 0)
            {
                throw new InvalidOperationException("Start token not found: " + startToken);
            }

            int end = contents.IndexOf(endToken, start + startToken.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException("End token not found: " + endToken);
            }

            return contents.Substring(start, end - start);
        }

        private static string ReadRepositoryFile(params string[] relativePath)
        {
            return File.ReadAllText(Path.Combine(FindRepositoryRoot(), Path.Combine(relativePath)));
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src")) &&
                    Directory.Exists(Path.Combine(current.FullName, "test")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
        }
    }
}
