namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Asks every registered REST route without credentials. A route that is not on the anonymous
    /// allow list must answer 401. The allow list is the health check, login, tenant lookup,
    /// self-registration, and the dashboard shell.
    /// </summary>
    public sealed class AnonymousRouteClosureSuite : IArmadaTestSuite
    {
        private const string SuiteId = "E2E.AnonymousRouteClosure";
        private static readonly object SuiteKey = new object();
        private static readonly Regex _Parameter = new Regex(@"\{[^/{}]+\}", RegexOptions.Compiled);

        /// <summary>
        /// Build the anonymous-route closure suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            return new TestSuiteDescriptor(SuiteId, "Anonymous route closure", new List<TestCaseDescriptor>
            {
                CaseAsync("registered_routes_outside_the_allow_list_reject_anonymous_callers", "Registered routes outside the allow list reject anonymous callers", TestTags.Negative, WalkAsync)
            });
        }

        private static async Task WalkAsync()
        {
            E2EServerFixture admiral = await E2EServerFixture.AcquireAsync(SuiteKey).ConfigureAwait(false);
            E2EServerFixture harbor = await E2EServerFixture.StartIsolatedAsync(settings =>
            {
                settings.Harbor.Enabled = true;
                settings.WebSocketEnabled = true;
            }).ConfigureAwait(false);
            try
            {
                ServedRouteTable admiralRoutes = ServedRouteTable.FromServer(admiral.Server);
                ServedRouteTable harborRoutes = ServedRouteTable.FromServer(harbor.Server);
                Dictionary<string, int> levels = new Dictionary<string, int>(StringComparer.Ordinal);
                List<string> anonymous = new List<string>();
                List<string> failures = new List<string>();
                int routeCount = 0;

                using (HttpClient admiralClient = AnonymousClient(admiral.BaseUrl))
                using (HttpClient harborClient = AnonymousClient(harbor.BaseUrl))
                {
                    HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (ServedRoute route in admiralRoutes.Routes)
                    {
                        if (!seen.Add(route.Method + " " + route.Path)) continue;
                        routeCount++;
                        await CheckAsync(admiralClient, route, levels, anonymous, failures).ConfigureAwait(false);
                    }

                    foreach (ServedRoute route in harborRoutes.Routes)
                    {
                        if (!seen.Add(route.Method + " " + route.Path)) continue;
                        routeCount++;
                        await CheckAsync(harborClient, route, levels, anonymous, failures).ConfigureAwait(false);
                    }
                }

                Console.WriteLine("route census count=" + routeCount
                    + " NoAuthRequired=" + Count(levels, PermissionLevel.NoAuthRequired)
                    + " Authenticated=" + Count(levels, PermissionLevel.Authenticated)
                    + " TenantAdmin=" + Count(levels, PermissionLevel.TenantAdmin)
                    + " AdminOnly=" + Count(levels, PermissionLevel.AdminOnly));
                Console.WriteLine("anonymous routes:");
                foreach (string line in anonymous) Console.WriteLine(line);

                AssertTrue(routeCount > 0, "The server registered no routes.");
                AssertTrue(failures.Count == 0, failures.Count + " registered route(s) answered an anonymous caller without 401:\n" + String.Join("\n", failures));
            }
            finally
            {
                harbor.Stop();
            }
        }

        private static async Task CheckAsync(HttpClient client, ServedRoute route, Dictionary<string, int> levels, List<string> anonymous, List<string> failures)
        {
            string path = ConcretePath(route);
            PermissionLevel level = AuthorizationConfig.GetPermissionLevel(route.Method, path);
            string levelName = level.ToString();
            levels[levelName] = Count(levels, level) + 1;

            if (!path.StartsWith("/", StringComparison.Ordinal) || path.IndexOf('{') >= 0 || path.IndexOf('(') >= 0)
            {
                failures.Add(route.Method + " " + route.Path + ": the test could not build a concrete path");
                return;
            }

            int status;
            try
            {
                using (HttpRequestMessage message = new HttpRequestMessage(new HttpMethod(route.Method), path))
                {
                    if (route.Method == "POST" || route.Method == "PUT" || route.Method == "PATCH")
                        message.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using (HttpResponseMessage response = await client.SendAsync(message).ConfigureAwait(false))
                        status = (int)response.StatusCode;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                failures.Add(route.Method + " " + path + ": anonymous request failed (" + ex.GetType().Name + ")");
                return;
            }

            bool allowed = IsAllowListed(route.Method, path);
            if (allowed)
            {
                anonymous.Add(route.Method + " " + route.Path + " status=" + status + " why=" + AllowListReason(route.Method, path));
                return;
            }

            if (level == PermissionLevel.NoAuthRequired)
                anonymous.Add(route.Method + " " + route.Path + " status=" + status + " why=matrix NoAuthRequired");

            if (status != 401)
                failures.Add(route.Method + " " + path + " returned " + status + " (matrix " + levelName + ")");
        }

        private static bool IsAllowListed(string method, string path)
        {
            string normalizedMethod = method.ToUpperInvariant();
            string normalizedPath = path.Split('?', 2)[0].TrimEnd('/').ToLowerInvariant();
            if (normalizedPath.Length == 0) normalizedPath = "/";
            if (normalizedMethod == "GET" && (normalizedPath == "/" || normalizedPath == "/api/v1/status/health")) return true;
            if (normalizedMethod == "POST" && (normalizedPath == "/api/v1/authenticate"
                || normalizedPath == "/api/v1/tenants/lookup"
                || normalizedPath == "/api/v1/onboarding")) return true;
            return normalizedPath == "/dashboard" || normalizedPath.StartsWith("/dashboard/", StringComparison.Ordinal);
        }

        private static string AllowListReason(string method, string path)
        {
            string normalizedPath = path.Split('?', 2)[0].TrimEnd('/').ToLowerInvariant();
            if (normalizedPath.EndsWith("/status/health", StringComparison.Ordinal)) return "health check for monitors";
            if (normalizedPath.EndsWith("/authenticate", StringComparison.Ordinal)) return "login";
            if (normalizedPath.EndsWith("/tenants/lookup", StringComparison.Ordinal)) return "find a tenant before login";
            if (normalizedPath.EndsWith("/onboarding", StringComparison.Ordinal)) return "self-registration";
            if (normalizedPath == "/" || normalizedPath.StartsWith("/dashboard", StringComparison.Ordinal)) return "dashboard shell";
            return "allow list";
        }

        private static string ConcretePath(ServedRoute route)
        {
            return _Parameter.Replace(route.Path, "sample");
        }

        private static int Count(Dictionary<string, int> levels, PermissionLevel level)
        {
            return levels.TryGetValue(level.ToString(), out int count) ? count : 0;
        }

        private static HttpClient AnonymousClient(string baseUrl)
        {
            HttpClient client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(15)
            };
            return client;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }
    }
}
