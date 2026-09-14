namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Armada.Core;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Checks the published Postman collection against the routes the Admiral and the proxy
    /// actually register, instead of against a hand-maintained route list.
    /// </summary>
    public sealed class PostmanCollectionSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.PostmanCollection";
        private const string AdmiralBase = "{{baseUrl}}";
        private const string ProxyBase = "{{proxyBaseUrl}}";
        private static readonly Regex _Variable = new Regex(@"\{\{[^{}]+\}\}", RegexOptions.Compiled);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the Postman collection contract suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                CaseAsync("every_request_matches_a_served_route", "Every Postman request targets a served route and method", TestTags.Positive, async () =>
                {
                    ServedRouteTable admiral = await ServedRouteTable.ReadAdmiralAsync(this).ConfigureAwait(false);
                    ServedRouteTable proxy = await ServedRouteTable.ReadProxyAsync().ConfigureAwait(false);
                    List<CollectionRequest> requests = ReadCollection();
                    AssertTrue(requests.Count > 0, "The collection must contain requests.");

                    List<string> failures = new List<string>();
                    foreach (CollectionRequest request in requests)
                    {
                        ServedRouteTable? table = SelectTable(request, admiral, proxy, out string path);
                        if (table == null)
                        {
                            failures.Add(request.Describe() + ": URL must start with " + AdmiralBase + " or " + ProxyBase);
                        }
                        else if (!table.Serves(request.Method, path))
                        {
                            failures.Add(request.Describe() + ": no served " + request.Method + " route matches " + path);
                        }
                    }

                    AssertTrue(failures.Count == 0, failures.Count + " collection request(s) do not match a served route:\n" + String.Join("\n", failures));
                }),
                CaseAsync("every_served_api_route_has_a_request", "Every served API route has a Postman request", TestTags.Positive, async () =>
                {
                    ServedRouteTable admiral = await ServedRouteTable.ReadAdmiralAsync(this).ConfigureAwait(false);
                    ServedRouteTable proxy = await ServedRouteTable.ReadProxyAsync().ConfigureAwait(false);
                    List<CollectionRequest> requests = ReadCollection();

                    List<string> missing = new List<string>();
                    AddMissing(admiral, "/api/", requests, missing, request =>
                    {
                        if (request.Url.StartsWith(AdmiralBase, StringComparison.OrdinalIgnoreCase)) return StripQuery(request.Url.Substring(AdmiralBase.Length));
                        return null;
                    });
                    AddMissing(proxy, "/proxy-api/", requests, missing, request =>
                    {
                        if (!request.Url.StartsWith(ProxyBase, StringComparison.OrdinalIgnoreCase)) return null;
                        string path = StripQuery(request.Url.Substring(ProxyBase.Length));
                        return path.StartsWith("/proxy-api/", StringComparison.OrdinalIgnoreCase) ? path : null;
                    });

                    AssertTrue(missing.Count == 0, missing.Count + " served route(s) have no collection request:\n" + String.Join("\n", missing));
                }),
                CaseAsync("request_bodies_are_json_and_proxy_relays_carry_a_session", "Postman bodies parse and proxy relays send the proxy session", TestTags.Positive, async () =>
                {
                    List<string> failures = new List<string>();
                    HashSet<string> defined = ReadDefinedVariables();
                    foreach (CollectionRequest request in ReadCollection())
                    {
                        foreach (string variable in request.Variables.Where(variable => !defined.Contains(variable)))
                        {
                            failures.Add(request.Describe() + ": uses undefined collection variable {{" + variable + "}}");
                        }

                        if (!String.IsNullOrEmpty(request.RawBody) && request.DeclaresJson)
                        {
                            try
                            {
                                using (JsonDocument.Parse(_Variable.Replace(request.RawBody, "0")))
                                {
                                }
                            }
                            catch (JsonException ex)
                            {
                                failures.Add(request.Describe() + ": body is not JSON (" + ex.Message + ")");
                            }
                        }

                        if (request.Url.StartsWith(ProxyBase + "/api/", StringComparison.OrdinalIgnoreCase)
                            && !request.Headers.Contains(Constants.ProxySessionTokenHeader))
                        {
                            failures.Add(request.Describe() + ": the proxy relays /api/ only with the " + Constants.ProxySessionTokenHeader + " session header");
                        }
                    }

                    AssertTrue(failures.Count == 0, String.Join("\n", failures));
                    await Task.CompletedTask.ConfigureAwait(false);
                }),
                CaseAsync("anonymous_admiral_requests_are_not_refused_for_authentication", "Postman no-auth Admiral requests are served without credentials", TestTags.Positive, async () =>
                {
                    E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                    List<CollectionRequest> anonymous = ReadCollection()
                        .Where(request => request.NoAuth && request.Url.StartsWith(AdmiralBase, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    AssertTrue(anonymous.Count > 0, "The collection must mark its anonymous Admiral requests.");

                    List<string> failures = new List<string>();
                    foreach (CollectionRequest request in anonymous)
                    {
                        string path = StripQuery(request.Url.Substring(AdmiralBase.Length));
                        using (HttpRequestMessage message = new HttpRequestMessage(new HttpMethod(request.Method), _Variable.Replace(path, "unknown")))
                        {
                            if (!String.IsNullOrEmpty(request.RawBody))
                            {
                                message.Content = new StringContent(_Variable.Replace(request.RawBody, "0"), Encoding.UTF8, "application/json");
                            }

                            HttpResponseMessage response = await fx.UnauthClient.SendAsync(message).ConfigureAwait(false);
                            if (response.StatusCode == HttpStatusCode.Unauthorized)
                            {
                                failures.Add(request.Describe() + ": marked no-auth but the Admiral requires credentials");
                            }
                        }
                    }

                    AssertTrue(failures.Count == 0, String.Join("\n", failures));
                })
            };

            return new TestSuiteDescriptor(SuiteId, "Postman collection route contract", cases);
        }

        #endregion

        #region Private-Methods

        private static void AddMissing(ServedRouteTable table, string prefix, List<CollectionRequest> requests, List<string> missing, Func<CollectionRequest, string?> pathFor)
        {
            IEnumerable<ServedRoute> routes = table.Routes
                .Where(route => route.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(route => route.Path, StringComparer.Ordinal)
                .ThenBy(route => route.Method, StringComparer.Ordinal);

            foreach (ServedRoute route in routes)
            {
                bool covered = requests.Any(request =>
                {
                    string? path = pathFor(request);
                    return path != null && route.Matches(request.Method, path);
                });

                if (!covered) missing.Add(route.ToString());
            }
        }

        private static ServedRouteTable? SelectTable(CollectionRequest request, ServedRouteTable admiral, ServedRouteTable proxy, out string path)
        {
            path = String.Empty;
            if (request.Url.StartsWith(AdmiralBase, StringComparison.OrdinalIgnoreCase))
            {
                path = StripQuery(request.Url.Substring(AdmiralBase.Length));
                return admiral;
            }

            if (request.Url.StartsWith(ProxyBase, StringComparison.OrdinalIgnoreCase))
            {
                path = StripQuery(request.Url.Substring(ProxyBase.Length));
                // The proxy relays every /api/ request to the selected Admiral unchanged.
                return path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ? admiral : proxy;
            }

            return null;
        }

        private static string StripQuery(string url)
        {
            int query = url.IndexOf('?');
            return query < 0 ? url : url.Substring(0, query);
        }

        private static List<CollectionRequest> ReadCollection()
        {
            List<CollectionRequest> requests = new List<CollectionRequest>();
            AddRequests(LoadCollection().Item, String.Empty, requests);
            return requests;
        }

        private static HashSet<string> ReadDefinedVariables()
        {
            List<PostmanVariable> variables = LoadCollection().Variable ?? new List<PostmanVariable>();
            return new HashSet<string>(variables.Select(variable => variable.Key), StringComparer.Ordinal);
        }

        private static PostmanCollection LoadCollection()
        {
            string path = Path.Combine(FindRepositoryRoot(), "Armada.postman_collection.json");
            PostmanCollection? collection = JsonSerializer.Deserialize<PostmanCollection>(File.ReadAllText(path));
            if (collection == null) throw new InvalidOperationException("The Postman collection could not be read.");
            return collection;
        }

        private static void AddRequests(List<PostmanItem>? items, string folder, List<CollectionRequest> requests)
        {
            if (items == null) return;
            foreach (PostmanItem item in items)
            {
                string name = folder.Length == 0 ? item.Name : folder + "/" + item.Name;
                if (item.Request == null)
                {
                    AddRequests(item.Item, name, requests);
                    continue;
                }

                if (item.Request.Url == null || String.IsNullOrWhiteSpace(item.Request.Url.Raw))
                    throw new InvalidOperationException("Postman request '" + name + "' has no raw URL.");

                List<PostmanHeader> headers = item.Request.Header ?? new List<PostmanHeader>();
                CollectionRequest request = new CollectionRequest();
                request.Name = name;
                request.Method = item.Request.Method.ToUpperInvariant();
                request.Url = item.Request.Url.Raw.Trim();
                request.RawBody = item.Request.Body?.Raw;
                request.NoAuth = String.Equals(item.Request.Auth?.Type, "noauth", StringComparison.OrdinalIgnoreCase);
                request.Headers = new HashSet<string>(headers.Select(header => header.Key), StringComparer.OrdinalIgnoreCase);
                request.DeclaresJson = headers.Any(header =>
                    String.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)
                    && header.Value.Contains("json", StringComparison.OrdinalIgnoreCase));
                string referenced = request.Url + "\n" + (request.RawBody ?? String.Empty) + "\n" + String.Join("\n", headers.Select(header => header.Value));
                foreach (Match match in _Variable.Matches(referenced))
                {
                    request.Variables.Add(match.Value.Substring(2, match.Value.Length - 4));
                }

                requests.Add(request);
            }
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Armada.postman_collection.json"))
                    && Directory.Exists(Path.Combine(current.FullName, "src")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the repository root holding Armada.postman_collection.json.");
        }

        private static TestCaseDescriptor CaseAsync(string id, string name, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(SuiteId, id, name, _ => body(), new List<string> { tag, TestTags.EndToEnd });
        }

        #endregion

        #region Private-Classes

        private sealed class CollectionRequest
        {
            public string Name { get; set; } = String.Empty;

            public string Method { get; set; } = String.Empty;

            public string Url { get; set; } = String.Empty;

            public string? RawBody { get; set; }

            public bool NoAuth { get; set; }

            public bool DeclaresJson { get; set; }

            public HashSet<string> Headers { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public HashSet<string> Variables { get; } = new HashSet<string>(StringComparer.Ordinal);

            public string Describe()
            {
                return "'" + Name + "' " + Method + " " + Url;
            }
        }

        private sealed class PostmanCollection
        {
            [JsonPropertyName("item")]
            public List<PostmanItem>? Item { get; set; }

            [JsonPropertyName("variable")]
            public List<PostmanVariable>? Variable { get; set; }
        }

        private sealed class PostmanVariable
        {
            [JsonPropertyName("key")]
            public string Key { get; set; } = String.Empty;
        }

        private sealed class PostmanItem
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = String.Empty;

            [JsonPropertyName("item")]
            public List<PostmanItem>? Item { get; set; }

            [JsonPropertyName("request")]
            public PostmanRequest? Request { get; set; }
        }

        private sealed class PostmanRequest
        {
            [JsonPropertyName("method")]
            public string Method { get; set; } = String.Empty;

            [JsonPropertyName("header")]
            public List<PostmanHeader>? Header { get; set; }

            [JsonPropertyName("body")]
            public PostmanBody? Body { get; set; }

            [JsonPropertyName("url")]
            public PostmanUrl? Url { get; set; }

            [JsonPropertyName("auth")]
            public PostmanAuth? Auth { get; set; }
        }

        private sealed class PostmanHeader
        {
            [JsonPropertyName("key")]
            public string Key { get; set; } = String.Empty;

            [JsonPropertyName("value")]
            public string Value { get; set; } = String.Empty;
        }

        private sealed class PostmanBody
        {
            [JsonPropertyName("raw")]
            public string? Raw { get; set; }
        }

        private sealed class PostmanUrl
        {
            [JsonPropertyName("raw")]
            public string Raw { get; set; } = String.Empty;
        }

        private sealed class PostmanAuth
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }
        }

        #endregion
    }
}
