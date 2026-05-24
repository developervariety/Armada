namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Server.Routes;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for CodeIndexRoutes -- argument validation on construction and vessel-scoped URL structure.
    /// The HTTP request pipeline itself is exercised by integration tests; this suite pins the constructor
    /// contract and URL scoping so accidental null wiring or unscoped routes in ArmadaServer fail fast.
    /// </summary>
    public class CodeIndexRoutesTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Code Index Routes";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Constructor rejects null code index service", () =>
            {
                JsonSerializerOptions jsonOptions = new JsonSerializerOptions();
                AssertThrows<ArgumentNullException>(() => new CodeIndexRoutes(null!, jsonOptions));
                return Task.CompletedTask;
            });

            await RunTest("Constructor rejects null json options", () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                AssertThrows<ArgumentNullException>(() => new CodeIndexRoutes(service, null!));
                return Task.CompletedTask;
            });

            await RunTest("Constructor accepts non-null arguments", () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                JsonSerializerOptions jsonOptions = new JsonSerializerOptions();
                CodeIndexRoutes routes = new CodeIndexRoutes(service, jsonOptions);
                AssertNotNull(routes);
                return Task.CompletedTask;
            });

            await RunTest("Graph REST routes are vessel-scoped under /api/v1/vessels/{vesselId}/code-index", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CodeIndexRoutes.cs");
                AssertContains("/api/v1/vessels/{vesselId}/code-index/search-symbols", routes,
                    "search-symbols must be vessel-scoped");
                AssertContains("/api/v1/vessels/{vesselId}/code-index/callers", routes,
                    "callers must be vessel-scoped");
                AssertContains("/api/v1/vessels/{vesselId}/code-index/callees", routes,
                    "callees must be vessel-scoped");
                AssertContains("/api/v1/vessels/{vesselId}/code-index/impact", routes,
                    "impact must be vessel-scoped");
                AssertContains("/api/v1/vessels/{vesselId}/code-index/affected-tests", routes,
                    "affected-tests must be vessel-scoped");
                AssertFalse(routes.Contains("\"/api/v1/code-index/"), "Routes must not use the unscoped /api/v1/code-index/ prefix");
                return Task.CompletedTask;
            });

            await RunTest("Graph REST routes extract vesselId from URL parameters", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CodeIndexRoutes.cs");
                AssertContains("req.Parameters[\"vesselId\"]", routes,
                    "Handlers must read vesselId from URL parameters, not the request body");
                AssertContains("request.VesselId = vesselId", routes,
                    "Handlers must inject the URL vesselId into the request before calling the service");
                return Task.CompletedTask;
            });
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
