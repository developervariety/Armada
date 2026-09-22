namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end REST coverage for release route authentication, driven through the real REST API
    /// exposed by the shared in-process server obtained through <see cref="E2EServerFixture"/>. Release
    /// CRUD and derivation need a real local Git repository and are owned by the automated runner's
    /// release suite.
    /// </summary>
    public sealed class ReleaseSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the end-to-end Releases suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_without_auth_returns_401", "Releases_CreateWithoutAuthReturns401", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient unauthClient = fx.UnauthClient;

                HttpResponseMessage response = await unauthClient.PostAsync("/api/v1/releases",
                    JsonHelper.ToJsonContent(new
                    {
                        Title = "Unauthorized Release"
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Releases",
                cases: cases);
        }

        #endregion

        #region Private-Members

        private const string SuiteId = "E2E.Release";

        #endregion

        #region Private-Methods

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
