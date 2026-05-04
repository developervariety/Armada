namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Text.RegularExpressions;
    using Armada.Test.Common;

    /// <summary>
    /// Static regression tests for memory-sensitive mission payload paths.
    /// </summary>
    public class MemoryHotfixRegressionTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Memory Hotfix Regressions";

        /// <summary>
        /// Run tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
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

            await RunTest("MissionEnumerationRoutes UseSummaryProjection", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "MissionRoutes.cs");
                string listEndpoint = ExtractBetween(routes, "app.Get(\"/api/v1/missions\"", "app.Post<EnumerationQuery>");
                string enumerateEndpoint = ExtractBetween(routes, "app.Post<EnumerationQuery>(\"/api/v1/missions/enumerate\"", "app.Post<Mission>");

                AssertContains("EnumerateSummariesAsync", listEndpoint, "GET /api/v1/missions should use the lightweight mission summary projection.");
                AssertContains("EnumerateSummariesAsync", enumerateEndpoint, "POST /api/v1/missions/enumerate should use the lightweight mission summary projection.");
                AssertContains("m.AgentOutput = null", listEndpoint, "GET /api/v1/missions should strip persisted agent output.");
                AssertContains("m.AgentOutput = null", enumerateEndpoint, "POST /api/v1/missions/enumerate should strip persisted agent output.");
            });

            await RunTest("MissionDetailRoute StripsPersistedAgentOutput", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "MissionRoutes.cs");
                string detailEndpoint = ExtractBetween(routes, "app.Get(\"/api/v1/missions/{id}\"", "app.Put<Mission>");

                AssertContains("mission.AgentOutput = null", detailEndpoint, "Mission detail responses should not return persisted agent output.");
            });

            await RunTest("McpMissionSurfaces UseLightweightMissionPayloads", () =>
            {
                string enumerateTools = ReadRepositoryFile("src", "Armada.Server", "Mcp", "Tools", "McpEnumerateTools.cs");
                string missionCase = ExtractBetween(enumerateTools, "case \"missions\":", "case \"voyages\":");
                AssertContains("EnumerateSummariesAsync", missionCase, "MCP mission enumeration should not hydrate heavy mission payload columns.");
                AssertDoesNotContain("Missions.EnumerateAsync(query)", missionCase, "MCP mission enumeration must not use full mission enumeration.");

                string missionTools = ReadRepositoryFile("src", "Armada.Server", "Mcp", "Tools", "McpMissionTools.cs");
                AssertContains("ReadSummaryAsync(missionId)", missionTools, "MCP mission status should use summary reads.");
                AssertContains("SanitizeMissionForStatus", missionTools, "MCP mission mutation responses should strip heavy mission payloads.");
            });

            await RunTest("PlanningSessionRuntimeOutput IsBounded", () =>
            {
                string coordinator = ReadRepositoryFile("src", "Armada.Server", "PlanningSessionCoordinator.cs");

                AssertContains("_PlanningOutputCapChars", coordinator, "Planning session output should have a bounded live buffer.");
                AssertContains("AppendPlanningOutputBounded(output, line)", coordinator, "Planning runtime output handlers should use the bounded append helper.");
                AssertDoesNotContain("output.Append(line);", coordinator, "Planning runtime output handlers must not append unbounded runtime output directly.");
            });

            await RunTest("RemoteControlAndWebSocket MissionListsUseSummaryProjection", () =>
            {
                string remoteManagement = ReadRepositoryFile("src", "Armada.Server", "RemoteControlManagementService.cs");
                string remoteQuery = ReadRepositoryFile("src", "Armada.Server", "RemoteControlQueryService.cs");
                string websocket = ReadRepositoryFile("src", "Armada.Server", "WebSocket", "WebSocketCommandHandler.cs");

                AssertContains("EnumerateSummariesAsync(query", remoteManagement, "Remote-control mission list should use summary projection.");
                AssertContains("ReadSummaryAsync(missionId", remoteQuery, "Remote-control mission detail/log should use summary reads where full payloads are unnecessary.");
                AssertContains("EnumerateSummariesAsync(new EnumerationQuery", remoteQuery, "Remote-control voyage/captain/recent mission payloads should use summary projection.");
                AssertContains("EnumerateSummariesAsync(missionQuery)", websocket, "WebSocket list_missions should use summary projection.");
                AssertContains("ReadSummaryAsync(getMissionId)", websocket, "WebSocket get_mission should use summary reads.");
            });

            await RunTest("SqliteMissionSummaries DoNotSelectHeavyMissionColumns", () =>
            {
                string methods = ReadRepositoryFile("src", "Armada.Core", "Database", "Sqlite", "Implementations", "MissionMethods.cs");

                AssertContains("ReadSummaryAsync", methods, "SQLite mission summary reads should avoid full mission hydration.");
                AssertContains("NULL AS description", methods, "SQLite mission summary projection should not hydrate description.");
                AssertContains("NULL AS diff_snapshot", methods, "SQLite mission summary projection should not hydrate diff snapshots.");
                AssertContains("NULL AS agent_output", methods, "SQLite mission summary projection should not hydrate agent output.");
                AssertContains("id = @mission_id", methods, "SQLite summary enumeration should support missionId filtering.");
                AssertContains("CountByVoyageStatusAsync", methods, "Voyage progress should use grouped counts instead of full mission rows.");
            });
        }

        private void AssertContains(string expected, string actual, string message)
        {
            AssertTrue(actual.Contains(expected, StringComparison.Ordinal), message + " Expected text: " + expected);
        }

        private void AssertDoesNotContain(string unexpected, string actual, string message)
        {
            AssertFalse(actual.Contains(unexpected, StringComparison.Ordinal), message + " Unexpected text: " + unexpected);
        }

        private static string ExtractBetween(string contents, string startToken, string endToken)
        {
            int start = contents.IndexOf(startToken, StringComparison.Ordinal);
            if (start < 0)
                throw new InvalidOperationException("Start token not found: " + startToken);

            int end = contents.IndexOf(endToken, start + startToken.Length, StringComparison.Ordinal);
            if (end < 0)
                throw new InvalidOperationException("End token not found: " + endToken);

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
