namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// REST-level integration tests for the OAuth2 single sign-on endpoints. The
    /// test server does not configure a provider, so SSO is expected to report
    /// disabled and the authorize endpoint should short-circuit safely.
    /// </summary>
    public class OAuthApiTests : TestSuite
    {
        #region Public-Members

        /// <summary>
        /// Name of this test suite.
        /// </summary>
        public override string Name => "OAuth API Tests";

        #endregion

        #region Private-Members

        private HttpClient _UnauthClient;
        private string _BaseUrl;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a new OAuthApiTests suite.
        /// </summary>
        /// <param name="unauthClient">Unauthenticated HTTP client.</param>
        /// <param name="baseUrl">Server base URL.</param>
        public OAuthApiTests(HttpClient unauthClient, string baseUrl)
        {
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
            _BaseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        }

        #endregion

        #region Protected-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("OAuthConfig_WithoutAuth_ReturnsDisabled", async () =>
            {
                HttpResponseMessage response = await _UnauthClient.GetAsync("/api/v1/auth/oauth/config").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                OAuthConfigResult result = await JsonHelper.DeserializeAsync<OAuthConfigResult>(response).ConfigureAwait(false);
                AssertFalse(result.Enabled, "SSO should be disabled on the test server");
            }).ConfigureAwait(false);

            await RunTest("OAuthAuthorize_WhenDisabled_RedirectsWithError", async () =>
            {
                HttpClientHandler handler = new HttpClientHandler();
                handler.AllowAutoRedirect = false;
                using (HttpClient noFollow = new HttpClient(handler))
                {
                    noFollow.BaseAddress = new Uri(_BaseUrl);
                    HttpResponseMessage response = await noFollow.GetAsync("/api/v1/auth/oauth/authorize").ConfigureAwait(false);

                    AssertEqual(HttpStatusCode.Found, response.StatusCode);
                    AssertNotNull(response.Headers.Location, "Expected a redirect Location");
                    AssertContains("oauth_error=sso_disabled", response.Headers.Location!.ToString());
                }
            }).ConfigureAwait(false);
        }

        #endregion
    }
}
