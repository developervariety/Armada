namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end descriptors for authentication, API key handling, and CORS headers, ported 1:1
    /// from the retired automated AuthenticationTests suite. Cases run against the shared e2e
    /// server fixture using its authenticated and unauthenticated clients, base URL, and API key.
    /// </summary>
    public sealed class AuthenticationSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.Authentication";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Authentication suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            foreach (AuthRefusalRoute route in AuthRefusalRoutes.NoApiKey)
            {
                cases.Add(CaseAsync("no_api_key_" + ToCaseId(route.Scenario) + "_is_refused_with_401", "NoApiKey_" + route.Scenario + "_IsRefusedWith401", TestTags.Negative, async () =>
                {
                    E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                    await AssertRefusedAsync(fx, fx.UnauthClient, route).ConfigureAwait(false);
                }));
            }

            foreach (AuthRefusalRoute route in AuthRefusalRoutes.WrongApiKeyRoutes)
            {
                cases.Add(CaseAsync("wrong_api_key_" + ToCaseId(route.Scenario) + "_is_refused_with_401", "WrongApiKey_" + route.Scenario + "_IsRefusedWith401", TestTags.Negative, async () =>
                {
                    E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                    using (HttpClient wrongKeyClient = CreateClientWithKey(fx.BaseUrl, "X-Api-Key", AuthRefusalRoutes.WrongApiKey))
                        await AssertRefusedAsync(fx, wrongKeyClient, route).ConfigureAwait(false);
                }));
            }

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Authentication",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task AssertRefusedAsync(E2EServerFixture fx, HttpClient caller, AuthRefusalRoute route)
        {
            string? failure = await AuthRefusalRoutes.DescribeRefusalFailureAsync(caller, fx.AuthClient, route).ConfigureAwait(false);
            AssertTrue(failure == null, failure);
        }

        private static HttpClient CreateClientWithKey(string baseUrl, string headerName, string key)
        {
            HttpClient client = new HttpClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Add(headerName, key);
            return client;
        }

        private static string ToCaseId(string scenario)
        {
            System.Text.StringBuilder id = new System.Text.StringBuilder();
            foreach (char c in scenario)
            {
                if (Char.IsUpper(c) && id.Length > 0) id.Append('_');
                id.Append(Char.ToLowerInvariant(c));
            }
            return id.ToString();
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

        #endregion
    }
}
