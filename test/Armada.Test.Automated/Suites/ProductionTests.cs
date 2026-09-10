namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Net;
    using System.Net.Http;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// REST tests for the verified production summary.
    /// </summary>
    public sealed class ProductionTests : TestSuite
    {
        private readonly HttpClient _AuthClient;
        private readonly HttpClient _UnauthClient;

        /// <inheritdoc />
        public override string Name => "Production Routes";

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ProductionTests(HttpClient authClient, HttpClient unauthClient)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("SummaryRequiresAuthentication", async () =>
            {
                HttpResponseMessage response = await _UnauthClient
                    .GetAsync("/api/v1/production/summary")
                    .ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("SummaryReturnsRequestedWindowAndCoverage", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync(
                    "/api/v1/production/summary?fromUtc=2026-01-01T00%3A00%3A00Z&toUtc=2026-01-08T00%3A00%3A00Z&sourceFamily=ecu&workType=Feature")
                    .ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                ProductionSummaryResult result = await JsonHelper
                    .DeserializeAsync<ProductionSummaryResult>(response)
                    .ConfigureAwait(false);
                AssertEqual(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), result.FromUtc);
                AssertEqual(new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc), result.ToUtc);
                AssertNotNull(result.Groups);
                AssertNotNull(result.Warnings);
            }).ConfigureAwait(false);
        }
    }
}
