namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.IO;
    using System.Reflection;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
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
        private const int GraphRouteCount = 8;
        private const int CodeIndexRouteCount = 11;

        /// <summary>Suite name.</summary>
        public override string Name => "Code Index Routes";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Constructor rejects null code index service", () =>
            {
                JsonSerializerOptions jsonOptions = new JsonSerializerOptions();
                using TestDatabase testDb = TestDatabaseHelper.CreateDatabaseAsync().GetAwaiter().GetResult();
                AssertThrows<ArgumentNullException>(() => new CodeIndexRoutes(null!, testDb.Driver, jsonOptions));
                return Task.CompletedTask;
            });

            await RunTest("Constructor rejects null database", () =>
            {
                JsonSerializerOptions jsonOptions = new JsonSerializerOptions();
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                AssertThrows<ArgumentNullException>(() => new CodeIndexRoutes(service, null!, jsonOptions));
                return Task.CompletedTask;
            });

            await RunTest("Constructor rejects null json options", () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                using TestDatabase testDb = TestDatabaseHelper.CreateDatabaseAsync().GetAwaiter().GetResult();
                AssertThrows<ArgumentNullException>(() => new CodeIndexRoutes(service, testDb.Driver, null!));
                return Task.CompletedTask;
            });

            await RunTest("Constructor accepts non-null arguments", () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                JsonSerializerOptions jsonOptions = new JsonSerializerOptions();
                using TestDatabase testDb = TestDatabaseHelper.CreateDatabaseAsync().GetAwaiter().GetResult();
                CodeIndexRoutes routes = new CodeIndexRoutes(service, testDb.Driver, jsonOptions);
                AssertNotNull(routes);
                return Task.CompletedTask;
            });

            await RunTest("Graph REST routes are vessel-scoped under /api/v1/vessels/{vesselId}/code-index", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CodeIndexRoutes.cs");
                AssertContains("/api/v1/vessels/{vesselId}/code-index/status", routes,
                    "status must be vessel-scoped");
                AssertContains("/api/v1/vessels/{vesselId}/code-index/update", routes,
                    "update must be vessel-scoped");
                AssertContains("/api/v1/vessels/{vesselId}/code-index/search", routes,
                    "search must be vessel-scoped");
                AssertContains("/api/v1/vessels/{vesselId}/code-index/search-symbols", routes,
                    "search-symbols must be vessel-scoped");
                AssertContains("/api/v1/vessels/{vesselId}/code-index/callers", routes,
                    "callers must be vessel-scoped");
                AssertContains("/api/v1/vessels/{vesselId}/code-index/callees", routes,
                    "callees must be vessel-scoped");
                AssertContains("/api/v1/vessels/{vesselId}/code-index/node", routes,
                    "node must be vessel-scoped");
                AssertContains("/api/v1/vessels/{vesselId}/code-index/files", routes,
                    "files must be vessel-scoped");
                AssertContains("/api/v1/vessels/{vesselId}/code-index/explore", routes,
                    "explore must be vessel-scoped");
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
                    "Each graph route must extract vesselId from URL parameters exactly once");
                AssertEqual(GraphRouteCount, injectCount,
                    "Each graph route must assign URL vesselId onto the deserialized request exactly once so a body-supplied VesselId cannot win");
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

            await RunTest("Graph routes authorize vessel access before body parsing in each handler", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CodeIndexRoutes.cs");
                MatchCollection authMatches = Regex.Matches(routes, @"AuthorizeVesselAccessAsync\(req");
                MatchCollection deserializeMatches = Regex.Matches(routes, @"JsonSerializer\.Deserialize<CodeGraph\w+Request>");
                AssertEqual(CodeIndexRouteCount, authMatches.Count,
                    "Each code-index route must call the vessel-access authorization helper exactly once");
                AssertEqual(GraphRouteCount, deserializeMatches.Count,
                    "Each graph route must deserialize a CodeGraph*Request body exactly once");
                int graphAuthOffset = authMatches.Count - deserializeMatches.Count;
                for (int i = 0; i < GraphRouteCount; i++)
                {
                    AssertTrue(authMatches[i + graphAuthOffset].Index < deserializeMatches[i].Index,
                        "Route handler " + (i + 1) + ": vessel ACL must be checked before request body parsing and graph-service work");
                }
            });

            await RunTest("Code-index routes call authorization service before delegating to ICodeIndexService", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CodeIndexRoutes.cs");
                MatchCollection authzMatches = Regex.Matches(routes, @"AuthorizeVesselAccessAsync\(req");
                MatchCollection serviceCallMatches = Regex.Matches(routes, @"_codeIndex\.\w+Async\(");
                AssertEqual(CodeIndexRouteCount, authzMatches.Count,
                    "Each code-index route must invoke the helper that performs authz plus vessel lookup exactly once");
                AssertEqual(CodeIndexRouteCount, serviceCallMatches.Count,
                    "Each code-index route must call exactly one ICodeIndexService method");
                for (int i = 0; i < CodeIndexRouteCount; i++)
                {
                    AssertTrue(authzMatches[i].Index < serviceCallMatches[i].Index,
                        "Route handler " + (i + 1) + ": authorization check must occur before service delegation so unauthorized callers never reach the code-index service");
                }
            });

            await RunTest("Code-index routes declare vesselId as an OpenAPI path parameter", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CodeIndexRoutes.cs");
                int pathParamCount = CountOccurrences(routes, "OpenApiParameterMetadata.Path(\"vesselId\"");
                AssertEqual(CodeIndexRouteCount, pathParamCount,
                    "Each code-index route must declare vesselId as an OpenAPI path parameter so the generated spec advertises vessel scoping to clients");
            });

            await RunTest("Code-index routes all use the vessel-scoped path prefix exclusively", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CodeIndexRoutes.cs");
                int scopedRouteRegistrationCount = CountOccurrences(routes, "\"/api/v1/vessels/{vesselId}/code-index/");
                AssertEqual(CodeIndexRouteCount, scopedRouteRegistrationCount,
                    "Every vessel-scoped code-index route string literal must be registered");
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

                CodeGraphNodeRequest node = JsonSerializer.Deserialize<CodeGraphNodeRequest>(
                    "{\"VesselId\":\"vsl_spoof\",\"Symbol\":\"S\"}", jsonOptions)!;
                node.VesselId = urlVesselId;
                AssertEqual(urlVesselId, node.VesselId, "Node request: URL vesselId must override body");

                CodeGraphFileStructureRequest files = JsonSerializer.Deserialize<CodeGraphFileStructureRequest>(
                    "{\"VesselId\":\"vsl_spoof\",\"PathPrefix\":\"src\"}", jsonOptions)!;
                files.VesselId = urlVesselId;
                AssertEqual(urlVesselId, files.VesselId, "Files request: URL vesselId must override body");

                CodeGraphExploreRequest explore = JsonSerializer.Deserialize<CodeGraphExploreRequest>(
                    "{\"VesselId\":\"vsl_spoof\",\"Query\":\"S\"}", jsonOptions)!;
                explore.VesselId = urlVesselId;
                AssertEqual(urlVesselId, explore.VesselId, "Explore request: URL vesselId must override body");
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

            await RunTest("Graph POST routes are authenticated, not tenant-admin, before route-level vessel ACL", () =>
            {
                PermissionLevel required = AuthorizationConfig.GetPermissionLevel(
                    "POST",
                    "/api/v1/vessels/vsl_target/code-index/search-symbols");
                AssertEqual(PermissionLevel.Authenticated, required,
                    "Graph routes use POST bodies but are read-only; coarse auth must allow regular users through to the route-level vessel owner check");
                return Task.CompletedTask;
            });

            await RunTest("Vessel ACL helper allows admin, tenant admin, and owning user only", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = "tenant-a", Name = "Tenant A" }).ConfigureAwait(false);
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = "tenant-b", Name = "Tenant B" }).ConfigureAwait(false);
                await testDb.Driver.Users.CreateAsync(new UserMaster { Id = "user-a", TenantId = "tenant-a", Email = "user-a@example.invalid" }).ConfigureAwait(false);
                await testDb.Driver.Users.CreateAsync(new UserMaster { Id = "user-b", TenantId = "tenant-b", Email = "user-b@example.invalid" }).ConfigureAwait(false);
                await testDb.Driver.Users.CreateAsync(new UserMaster { Id = "user-other", TenantId = "tenant-a", Email = "user-other@example.invalid" }).ConfigureAwait(false);

                Vessel vesselA = await testDb.Driver.Vessels.CreateAsync(new Vessel
                {
                    TenantId = "tenant-a",
                    UserId = "user-a",
                    Name = "tenant-a-vessel",
                    RepoUrl = "https://example.invalid/a.git"
                }).ConfigureAwait(false);

                Vessel vesselB = await testDb.Driver.Vessels.CreateAsync(new Vessel
                {
                    TenantId = "tenant-b",
                    UserId = "user-b",
                    Name = "tenant-b-vessel",
                    RepoUrl = "https://example.invalid/b.git"
                }).ConfigureAwait(false);

                CodeIndexRoutes routes = new CodeIndexRoutes(
                    new RecordingCodeIndexService(),
                    testDb.Driver,
                    new JsonSerializerOptions());

                AuthContext admin = AuthContext.Authenticated("tenant-x", "admin", true, true, "test");
                AuthContext tenantAAdmin = AuthContext.Authenticated("tenant-a", "tenant-admin-a", false, true, "test");
                AuthContext tenantBAdmin = AuthContext.Authenticated("tenant-b", "tenant-admin-b", false, true, "test");
                AuthContext ownerA = AuthContext.Authenticated("tenant-a", "user-a", false, false, "test");
                AuthContext otherUserA = AuthContext.Authenticated("tenant-a", "user-other", false, false, "test");

                AssertNotNull(await ResolveVesselForContextAsync(routes, admin, vesselA.Id).ConfigureAwait(false));
                AssertNotNull(await ResolveVesselForContextAsync(routes, tenantAAdmin, vesselA.Id).ConfigureAwait(false));
                AssertNull(await ResolveVesselForContextAsync(routes, tenantBAdmin, vesselA.Id).ConfigureAwait(false));
                AssertNotNull(await ResolveVesselForContextAsync(routes, ownerA, vesselA.Id).ConfigureAwait(false));
                AssertNull(await ResolveVesselForContextAsync(routes, otherUserA, vesselA.Id).ConfigureAwait(false));
                AssertNotNull(await ResolveVesselForContextAsync(routes, tenantBAdmin, vesselB.Id).ConfigureAwait(false));
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

        private async Task<Vessel?> ResolveVesselForContextAsync(CodeIndexRoutes routes, AuthContext ctx, string vesselId)
        {
            MethodInfo? method = typeof(CodeIndexRoutes).GetMethod(
                "ReadVesselForContextAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            AssertNotNull(method);

            object? result = method!.Invoke(routes, new object[] { ctx, vesselId });
            AssertNotNull(result);

            return await ((Task<Vessel?>)result!).ConfigureAwait(false);
        }
    }
}
