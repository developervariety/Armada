namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Generic OAuth2 / OIDC single sign-on service. Implements the
    /// Authorization-Code flow (with PKCE) by hand because Armada's REST layer
    /// is Watson, not ASP.NET Core. On success it mints a normal Armada session
    /// token so the rest of the auth stack is unchanged.
    /// </summary>
    public class OAuth2Service : IOAuth2Service
    {
        #region Public-Members

        /// <inheritdoc />
        public bool IsEnabled => _Settings.OAuth2.IsConfigured();

        #endregion

        #region Private-Members

        private readonly string _Header = "[OAuth2Service] ";
        private readonly DatabaseDriver _Database;
        private readonly ISessionTokenService _SessionTokenService;
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly OAuthStateStore _StateStore;
        private readonly HttpClient _HttpClient;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="sessionTokenService">Session token service.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="httpClient">Optional HTTP client (a shared instance is created when null).</param>
        /// <param name="stateStore">Optional state store (a default is created when null).</param>
        public OAuth2Service(
            DatabaseDriver database,
            ISessionTokenService sessionTokenService,
            ArmadaSettings settings,
            LoggingModule logging,
            HttpClient? httpClient = null,
            OAuthStateStore? stateStore = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _SessionTokenService = sessionTokenService ?? throw new ArgumentNullException(nameof(sessionTokenService));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _HttpClient = httpClient ?? new HttpClient();
            _StateStore = stateStore ?? new OAuthStateStore();
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public OAuthConfigResult GetPublicConfig()
        {
            return new OAuthConfigResult
            {
                Enabled = IsEnabled,
                DisplayName = _Settings.OAuth2.DisplayName
            };
        }

        /// <inheritdoc />
        public string BuildAuthorizationUrl(string redirectUri)
        {
            if (string.IsNullOrEmpty(redirectUri)) throw new ArgumentNullException(nameof(redirectUri));
            if (!IsEnabled) throw new InvalidOperationException("OAuth2 is not configured");

            OAuth2Settings cfg = _Settings.OAuth2;

            string codeVerifier = cfg.UsePkce ? PkceHelper.GenerateCodeVerifier() : string.Empty;
            string state = _StateStore.Issue(codeVerifier);

            Dictionary<string, string> query = new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = cfg.ClientId!,
                ["redirect_uri"] = redirectUri,
                ["scope"] = cfg.Scopes,
                ["state"] = state
            };

            if (cfg.UsePkce)
            {
                query["code_challenge"] = PkceHelper.ComputeCodeChallenge(codeVerifier);
                query["code_challenge_method"] = "S256";
            }

            return AppendQuery(cfg.AuthorizationEndpoint!, query);
        }

        /// <inheritdoc />
        public async Task<OAuthLoginResult> CompleteLoginAsync(string? code, string? state, string redirectUri, CancellationToken token = default)
        {
            if (!IsEnabled) return OAuthLoginResult.Failed("sso_disabled");
            if (string.IsNullOrEmpty(code)) return OAuthLoginResult.Failed("missing_code");

            OAuthFlowState? flow = _StateStore.Consume(state);
            if (flow == null) return OAuthLoginResult.Failed("invalid_state");

            OAuth2Settings cfg = _Settings.OAuth2;

            try
            {
                OAuthTokenResponse? tokenResponse = await ExchangeCodeAsync(code, redirectUri, flow.CodeVerifier, cfg, token).ConfigureAwait(false);
                if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
                {
                    string detail = tokenResponse?.Error ?? "no_access_token";
                    _Logging.Warn(_Header + "token exchange failed: " + detail);
                    return OAuthLoginResult.Failed("token_exchange_failed");
                }

                OAuthUserInfo? userInfo = await FetchUserInfoAsync(tokenResponse.AccessToken, cfg, token).ConfigureAwait(false);
                if (userInfo == null) return OAuthLoginResult.Failed("userinfo_failed");

                string? email = userInfo.GetClaim(cfg.EmailClaim);
                if (string.IsNullOrWhiteSpace(email)) email = userInfo.Email;
                if (string.IsNullOrWhiteSpace(email)) return OAuthLoginResult.Failed("no_email_claim");

                // Require a verified email before trusting it for identity mapping.
                // Otherwise a provider account with an unverified, attacker-chosen
                // email could be matched to (and take over) an existing Armada user.
                if (cfg.RequireVerifiedEmail && userInfo.EmailVerified != true)
                {
                    _Logging.Warn(_Header + "rejecting SSO login: email not verified by provider");
                    return OAuthLoginResult.Failed("email_not_verified");
                }

                string? displayName = userInfo.GetClaim(cfg.NameClaim) ?? userInfo.Name;

                UserMaster? user = await ResolveOrProvisionUserAsync(email, displayName, token).ConfigureAwait(false);
                if (user == null) return OAuthLoginResult.Failed("user_not_permitted");

                AuthenticateResult session = _SessionTokenService.CreateToken(user.TenantId, user.Id);
                return new OAuthLoginResult
                {
                    Success = true,
                    Token = session.Token,
                    ExpiresUtc = session.ExpiresUtc
                };
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "login failed: " + ex.Message);
                return OAuthLoginResult.Failed("login_error");
            }
        }

        /// <inheritdoc />
        public async Task<UserMaster?> ResolveOrProvisionUserAsync(string email, string? displayName, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;
            email = email.ToLowerInvariant();

            OAuth2Settings cfg = _Settings.OAuth2;
            string tenantId = cfg.DefaultTenantId;

            TenantMetadata? tenant = await _Database.Tenants.ReadAsync(tenantId, token).ConfigureAwait(false);
            if (tenant == null || !tenant.Active)
            {
                _Logging.Warn(_Header + "SSO tenant '" + tenantId + "' not found or inactive");
                return null;
            }

            UserMaster? existing = await _Database.Users.ReadByEmailAsync(tenantId, email, token).ConfigureAwait(false);
            if (existing != null)
            {
                if (!existing.Active) return null;
                return existing;
            }

            if (!cfg.AllowAutoProvision)
            {
                _Logging.Warn(_Header + "SSO user '" + email + "' not found and auto-provision disabled");
                return null;
            }

            UserMaster newUser = new UserMaster(tenantId, email, Guid.NewGuid().ToString("N"));
            SplitDisplayName(displayName, newUser);
            newUser.IsAdmin = false;
            newUser.IsTenantAdmin = false;
            await _Database.Users.CreateAsync(newUser, token).ConfigureAwait(false);

            Credential newCred = new Credential(tenantId, newUser.Id);
            await _Database.Credentials.CreateAsync(newCred, token).ConfigureAwait(false);

            _Logging.Info(_Header + "auto-provisioned SSO user '" + email + "' in tenant '" + tenantId + "'");
            return newUser;
        }

        #endregion

        #region Private-Methods

        private async Task<OAuthTokenResponse?> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, OAuth2Settings cfg, CancellationToken token)
        {
            Dictionary<string, string> form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = cfg.ClientId!,
                ["client_secret"] = cfg.ClientSecret!
            };
            if (cfg.UsePkce && !string.IsNullOrEmpty(codeVerifier))
                form["code_verifier"] = codeVerifier;

            using (FormUrlEncodedContent content = new FormUrlEncodedContent(form))
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, cfg.TokenEndpoint))
            {
                request.Content = content;
                request.Headers.Add("Accept", "application/json");
                using (HttpResponseMessage response = await _HttpClient.SendAsync(request, token).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(body)) return null;
                    return JsonSerializer.Deserialize<OAuthTokenResponse>(body);
                }
            }
        }

        private async Task<OAuthUserInfo?> FetchUserInfoAsync(string accessToken, OAuth2Settings cfg, CancellationToken token)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, cfg.UserInfoEndpoint))
            {
                request.Headers.Add("Accept", "application/json");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                using (HttpResponseMessage response = await _HttpClient.SendAsync(request, token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return null;
                    string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(body)) return null;
                    return JsonSerializer.Deserialize<OAuthUserInfo>(body);
                }
            }
        }

        private static void SplitDisplayName(string? displayName, UserMaster user)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return;
            string trimmed = displayName.Trim();
            int space = trimmed.IndexOf(' ');
            if (space <= 0)
            {
                user.FirstName = trimmed;
                return;
            }
            user.FirstName = trimmed.Substring(0, space);
            user.LastName = trimmed.Substring(space + 1).Trim();
        }

        private static string AppendQuery(string baseUrl, Dictionary<string, string> query)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, string> kvp in query)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kvp.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(kvp.Value));
            }

            string separator = baseUrl.Contains('?') ? "&" : "?";
            return baseUrl + separator + sb.ToString();
        }

        #endregion
    }
}
