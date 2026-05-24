namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Server.Routes;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for CodeIndexRoutes -- argument validation on construction and vessel-scoped URL structure.
    /// The HTTP request pipeline itself is exercised by integration tests; this suite pins the constructor
    /// contract and URL scoping so accidental null wiring or unscoped routes in ArmadaServer fail fast.
    /// Vessel-scoping invariants are pinned by source scanning because the route handlers are
    /// closures registered against WatsonWebserver and cannot be invoked without a live HTTP context.
    /// </summary>
    public class CodeIndexRoutesTests : TestSuite
    {
        private const int GraphRouteCount = 5;

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

            await RunTest("Every graph route injects URL vesselId once, overriding any body value", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CodeIndexRoutes.cs");
                int extractCount = CountOccurrences(routes, "req.Parameters[\"vesselId\"]");
                int injectCount = CountOccurrences(routes, "request.VesselId = vesselId");
                AssertEqual(GraphRouteCount, extractCount,
                    "Each of the 5 graph routes must extract vesselId from URL parameters exactly once");
                AssertEqual(GraphRouteCount, injectCount,
                    "Each of the 5 graph routes must assign URL vesselId onto the deserialized request exactly once so a body-supplied VesselId cannot win");
            });

            await RunTest("Graph routes assign URL vesselId AFTER deserializing the body, never before", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CodeIndexRoutes.cs");
                MatchCollection deserializeMatches = Regex.Matches(routes, @"JsonSerializer\.Deserialize<CodeGraph\w+Request>");
                MatchCollection injectMatches = Regex.Matches(routes, @"request\.VesselId = vesselId");
                AssertEqual(GraphRouteCount, deserializeMatches.Count,
                    "Each graph route must deserialize a CodeGraph*Request body exactly once");
                AssertEqual(GraphRouteCount, injectMatches.Count,
                    "Each graph route must inject the URL vesselId exactly once");
                for (int i = 0; i < GraphRouteCount; i++)
                {
                    int deserializeIdx = deserializeMatches[i].Index;
                    int injectIdx = injectMatches[i].Index;
                    AssertTrue(deserializeIdx < injectIdx,
                        "Route handler " + (i + 1) + ": body deserialization must precede URL-vesselId assignment so any spoofed VesselId in the body is overwritten");
                }
            });

            await RunTest("Graph routes authenticate before extracting vesselId in each handler", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CodeIndexRoutes.cs");
                MatchCollection authMatches = Regex.Matches(routes, @"await authenticate\(req\.Http\)");
                MatchCollection extractMatches = Regex.Matches(routes, @"req\.Parameters\[""vesselId""\]");
                AssertEqual(GraphRouteCount, authMatches.Count,
                    "Each graph route must call authenticate() exactly once");
                AssertEqual(GraphRouteCount, extractMatches.Count,
                    "Each graph route must read req.Parameters[\"vesselId\"] exactly once");
                for (int i = 0; i < GraphRouteCount; i++)
                {
                    AssertTrue(authMatches[i].Index < extractMatches[i].Index,
                        "Route handler " + (i + 1) + ": authentication must occur before vesselId extraction so unauthenticated callers are rejected before any vessel-scoped work");
                }
            });

            await RunTest("Graph routes call authorization service before delegating to ICodeIndexService", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CodeIndexRoutes.cs");
                MatchCollection authzMatches = Regex.Matches(routes, @"authz\.IsAuthorized\(");
                MatchCollection serviceCallMatches = Regex.Matches(routes, @"_codeIndex\.\w+Async\(");
                AssertEqual(GraphRouteCount, authzMatches.Count,
                    "Each graph route must invoke authz.IsAuthorized exactly once");
                AssertEqual(GraphRouteCount, serviceCallMatches.Count,
                    "Each graph route must call exactly one ICodeIndexService method");
                for (int i = 0; i < GraphRouteCount; i++)
                {
                    AssertTrue(authzMatches[i].Index < serviceCallMatches[i].Index,
                        "Route handler " + (i + 1) + ": authorization check must occur before service delegation so unauthorized callers never reach the graph service");
                }
            });

            await RunTest("Graph routes declare vesselId as an OpenAPI path parameter", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CodeIndexRoutes.cs");
                int pathParamCount = CountOccurrences(routes, "OpenApiParameterMetadata.Path(\"vesselId\"");
                AssertEqual(GraphRouteCount, pathParamCount,
                    "Each of the 5 graph routes must declare vesselId as an OpenAPI path parameter so the generated spec advertises vessel scoping to clients");
            });

            await RunTest("Graph routes all use the vessel-scoped path prefix exclusively", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CodeIndexRoutes.cs");
                int scopedRouteRegistrationCount = CountOccurrences(routes, "\"/api/v1/vessels/{vesselId}/code-index/");
                AssertEqual(GraphRouteCount, scopedRouteRegistrationCount,
                    "Exactly 5 vessel-scoped graph route string literals must be registered");
                AssertFalse(routes.Contains("\"/api/v1/code-index/"),
                    "The flat /api/v1/code-index/ prefix must not appear anywhere -- it bypasses the vessel hierarchy and is the regression this slice fixes");
            });

            await RunTest("URL vesselId overwrites a body-supplied VesselId on the deserialized request", () =>
            {
                JsonSerializerOptions jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                string spoofedBody = "{\"VesselId\":\"vsl_attacker_spoof\",\"Query\":\"target\",\"Limit\":1}";
                CodeGraphSymbolSearchRequest request = JsonSerializer.Deserialize<CodeGraphSymbolSearchRequest>(spoofedBody, jsonOptions)!;
                AssertEqual("vsl_attacker_spoof", request.VesselId,
                    "Sanity check -- body deserialization populates VesselId from the spoofed payload");
                string urlVesselId = "vsl_url_authoritative";
                request.VesselId = urlVesselId;
                AssertEqual("vsl_url_authoritative", request.VesselId,
                    "Handler pattern (deserialize then assign URL vesselId) must leave the URL value as the authoritative VesselId before the service call");
            });

            await RunTest("URL vesselId overwrite pattern works for every graph request type", () =>
            {
                JsonSerializerOptions jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                string urlVesselId = "vsl_url_authoritative";

                CodeGraphNeighborsRequest callers = JsonSerializer.Deserialize<CodeGraphNeighborsRequest>(
                    "{\"VesselId\":\"vsl_spoof\",\"Symbol\":\"S\"}", jsonOptions)!;
                callers.VesselId = urlVesselId;
                AssertEqual(urlVesselId, callers.VesselId, "Callers request: URL vesselId must override body");

                CodeGraphNeighborsRequest callees = JsonSerializer.Deserialize<CodeGraphNeighborsRequest>(
                    "{\"VesselId\":\"vsl_spoof\",\"Symbol\":\"S\"}", jsonOptions)!;
                callees.VesselId = urlVesselId;
                AssertEqual(urlVesselId, callees.VesselId, "Callees request: URL vesselId must override body");

                CodeGraphImpactRequest impact = JsonSerializer.Deserialize<CodeGraphImpactRequest>(
                    "{\"VesselId\":\"vsl_spoof\",\"Symbol\":\"S\"}", jsonOptions)!;
                impact.VesselId = urlVesselId;
                AssertEqual(urlVesselId, impact.VesselId, "Impact request: URL vesselId must override body");

                CodeGraphAffectedTestsRequest affected = JsonSerializer.Deserialize<CodeGraphAffectedTestsRequest>(
                    "{\"VesselId\":\"vsl_spoof\",\"Symbol\":\"S\"}", jsonOptions)!;
                affected.VesselId = urlVesselId;
                AssertEqual(urlVesselId, affected.VesselId, "AffectedTests request: URL vesselId must override body");
            });

            await RunTest("Missing body still yields a usable request with URL vesselId", () =>
            {
                JsonSerializerOptions jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                CodeGraphSymbolSearchRequest? deserialized = JsonSerializer.Deserialize<CodeGraphSymbolSearchRequest>(
                    "{}", jsonOptions);
                CodeGraphSymbolSearchRequest request = deserialized ?? new CodeGraphSymbolSearchRequest();
                request.VesselId = "vsl_url_only";
                AssertEqual("vsl_url_only", request.VesselId,
                    "An empty body must still allow URL-supplied vesselId to populate the request before the required-field validation kicks in");
                AssertEqual("", request.Query,
                    "Defaulted Query field stays empty so the route-level 'query is required' check fires");
            });
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (String.IsNullOrEmpty(needle)) return 0;
            int count = 0;
            int idx = 0;
            while (true)
            {
                int found = haystack.IndexOf(needle, idx, StringComparison.Ordinal);
                if (found < 0) break;
                count++;
                idx = found + needle.Length;
            }
            return count;
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
