namespace Armada.Server.Routes
{
    using System;
    using System.Threading.Tasks;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using Armada.Server;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// REST API routes for generic OAuth2 / OIDC single sign-on (Authentik and
    /// other providers). Implements the redirect-based Authorization-Code flow:
    /// config discovery, authorize redirect, and callback. On success the
    /// callback mints an Armada session token and hands it to the dashboard via
    /// a URL fragment.
    /// </summary>
    public class OAuthRoutes
    {
        #region Private-Members

        private const string CallbackPath = "/api/v1/auth/oauth/callback";
        private readonly IOAuth2Service _oauthService;
        private readonly ArmadaSettings _settings;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="oauthService">OAuth2 service.</param>
        /// <param name="settings">Application settings.</param>
        public OAuthRoutes(IOAuth2Service oauthService, ArmadaSettings settings)
        {
            _oauthService = oauthService ?? throw new ArgumentNullException(nameof(oauthService));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register routes with the application.
        /// </summary>
        /// <param name="app">Webserver.</param>
        /// <param name="authenticate">Authentication middleware (unused; these routes are public).</param>
        /// <param name="authz">Authorization service (unused; these routes are public).</param>
        public void Register(
            Webserver app,
            Func<WatsonWebserver.Core.HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            // Public config so the dashboard can show/hide the SSO button
            app.Get("/api/v1/auth/oauth/config", (ApiRequest req) =>
            {
                return Task.FromResult<object>(_oauthService.GetPublicConfig());
            },
            api => api.WithTag("Authentication").WithSummary("OAuth2 single sign-on public configuration"));

            // Begin login: redirect to the provider's authorization endpoint
            app.Get("/api/v1/auth/oauth/authorize", (ApiRequest req) =>
            {
                if (!_oauthService.IsEnabled)
                    return Task.FromResult(RedirectToDashboard(req, "#oauth_error=sso_disabled"));

                string redirectUri = ResolveRedirectUri(req);
                string authorizeUrl = _oauthService.BuildAuthorizationUrl(redirectUri);
                return Task.FromResult(Redirect(req, authorizeUrl));
            },
            api => api.WithTag("Authentication").WithSummary("Begin OAuth2 single sign-on"));

            // Provider callback: exchange the code and hand a session token to the dashboard
            app.Get(CallbackPath, async (ApiRequest req) =>
            {
                string? code = req.Query.GetValueOrDefault("code");
                string? state = req.Query.GetValueOrDefault("state");
                string? providerError = req.Query.GetValueOrDefault("error");

                if (!string.IsNullOrEmpty(providerError))
                    return RedirectToDashboard(req, "#oauth_error=" + Uri.EscapeDataString(providerError));

                string redirectUri = ResolveRedirectUri(req);
                OAuthLoginResult result = await _oauthService.CompleteLoginAsync(code, state, redirectUri).ConfigureAwait(false);

                if (!result.Success || string.IsNullOrEmpty(result.Token))
                    return RedirectToDashboard(req, "#oauth_error=" + Uri.EscapeDataString(result.ErrorMessage ?? "login_failed"));

                return RedirectToDashboard(req, "#oauth_token=" + Uri.EscapeDataString(result.Token));
            },
            api => api.WithTag("Authentication").WithSummary("OAuth2 single sign-on callback"));
        }

        #endregion

        #region Private-Methods

        private object Redirect(ApiRequest req, string location)
        {
            // The callback's Location fragment carries a live session token, so the
            // token must not also be echoed into the response body (which the
            // request-history pipeline can capture). Return an empty body.
            req.Http.Response.StatusCode = 302;
            req.Http.Response.Headers.Add("Location", location);
            return new { };
        }

        private object RedirectToDashboard(ApiRequest req, string fragment)
        {
            return Redirect(req, "/dashboard" + fragment);
        }

        private string ResolveRedirectUri(ApiRequest req)
        {
            if (!string.IsNullOrWhiteSpace(_settings.OAuth2.RedirectUri))
                return _settings.OAuth2.RedirectUri!;

            string? scheme = req.Http.Request.Headers.Get("X-Forwarded-Proto");
            if (string.IsNullOrWhiteSpace(scheme))
                scheme = _settings.Rest.Ssl ? "https" : "http";

            string? host = req.Http.Request.Headers.Get("X-Forwarded-Host");
            if (string.IsNullOrWhiteSpace(host))
                host = req.Http.Request.Headers.Get("Host");
            if (string.IsNullOrWhiteSpace(host))
                host = _settings.Rest.Hostname + ":" + _settings.AdmiralPort;

            return scheme + "://" + host + CallbackPath;
        }

        #endregion
    }
}
