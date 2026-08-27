namespace Armada.Server.Mcp
{
    using System.Collections.Concurrent;
    using System.Net;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Http;

    /// <summary>
    /// Small, in-memory OAuth authorization server for the Grok MCP connection proof.
    /// It implements authorization code with PKCE and rotating refresh tokens. It is
    /// intentionally not a production identity provider: restart removes every grant.
    /// </summary>
    internal sealed class GrokMcpOAuthBroker
    {
        private const string _SCOPE = "armada:read";
        private const int _MAX_CLIENTS = 100;
        private static readonly TimeSpan _AuthorizationCodeLifetime = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan _AccessTokenLifetime = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan _RefreshTokenLifetime = TimeSpan.FromHours(8);

        private readonly string _PublicBaseUrl;
        private readonly string _Resource;
        private readonly string _OwnerSecret;
        private readonly ConcurrentDictionary<string, ClientRegistration> _Clients = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, AuthorizationCode> _Codes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TokenGrant> _AccessTokens = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TokenGrant> _RefreshTokens = new(StringComparer.Ordinal);

        public GrokMcpOAuthBroker(string publicBaseUrl, string ownerSecret)
        {
            if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out Uri? publicUri)
                || publicUri.Scheme != Uri.UriSchemeHttps
                || !String.IsNullOrEmpty(publicUri.Query)
                || !String.IsNullOrEmpty(publicUri.Fragment))
                throw new ArgumentException("The MCP OAuth public base URL must be an HTTPS origin.", nameof(publicBaseUrl));
            if (String.IsNullOrWhiteSpace(ownerSecret))
                throw new ArgumentNullException(nameof(ownerSecret));

            _PublicBaseUrl = publicBaseUrl.TrimEnd('/');
            _Resource = _PublicBaseUrl + "/mcp";
            _OwnerSecret = ownerSecret;
        }

        public string Challenge => "Bearer resource_metadata=\""
            + _PublicBaseUrl + "/.well-known/oauth-protected-resource/mcp\", scope=\"" + _SCOPE + "\"";

        public bool IsPublicPath(PathString path)
        {
            return path.StartsWithSegments("/.well-known/oauth-protected-resource")
                || path.Equals("/.well-known/oauth-authorization-server")
                || path.Equals("/oauth/register")
                || path.Equals("/oauth/authorize")
                || path.Equals("/oauth/token");
        }

        public bool IsAccessToken(string token)
        {
            if (String.IsNullOrEmpty(token)) return false;
            string key = Hash(token);
            if (!_AccessTokens.TryGetValue(key, out TokenGrant? grant)) return false;
            if (grant.ExpiresUtc > DateTimeOffset.UtcNow) return true;
            _AccessTokens.TryRemove(key, out _);
            return false;
        }

        public void MapEndpoints(WebApplication application)
        {
            Func<HttpContext, Task<IResult>> registerClient = RegisterClientAsync;
            Func<HttpContext, IResult> authorize = AuthorizeAsync;
            Func<HttpContext, Task<IResult>> approve = ApproveAsync;
            Func<HttpContext, Task<IResult>> token = TokenAsync;
            application.MapGet("/.well-known/oauth-protected-resource", ProtectedResourceMetadata);
            application.MapGet("/.well-known/oauth-protected-resource/mcp", ProtectedResourceMetadata);
            application.MapGet("/.well-known/oauth-authorization-server", AuthorizationServerMetadata);
            application.MapPost("/oauth/register", registerClient);
            application.MapGet("/oauth/authorize", authorize);
            application.MapPost("/oauth/authorize", approve);
            application.MapPost("/oauth/token", token);
        }

        private IResult ProtectedResourceMetadata()
        {
            return NoStoreJson(new
            {
                resource = _Resource,
                authorization_servers = new[] { _PublicBaseUrl },
                bearer_methods_supported = new[] { "header" },
                scopes_supported = new[] { _SCOPE },
                resource_name = "Armada Grok lead proof"
            });
        }

        private IResult AuthorizationServerMetadata()
        {
            return NoStoreJson(new
            {
                issuer = _PublicBaseUrl,
                authorization_endpoint = _PublicBaseUrl + "/oauth/authorize",
                token_endpoint = _PublicBaseUrl + "/oauth/token",
                registration_endpoint = _PublicBaseUrl + "/oauth/register",
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                code_challenge_methods_supported = new[] { "S256" },
                token_endpoint_auth_methods_supported = new[] { "none" },
                scopes_supported = new[] { _SCOPE }
            });
        }

        private async Task<IResult> RegisterClientAsync(HttpContext context)
        {
            if (_Clients.Count >= _MAX_CLIENTS)
                return OAuthError("temporarily_unavailable", "The POC client limit was reached.", 503);

            DynamicClientRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<DynamicClientRequest>().ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return OAuthError("invalid_client_metadata", "The registration document is not valid JSON.");
            }

            if (request?.RedirectUris == null || request.RedirectUris.Length == 0
                || request.RedirectUris.Length > 10
                || request.RedirectUris.Any(uri => !IsSafeRedirectUri(uri)))
                return OAuthError("invalid_redirect_uri", "A valid HTTPS or loopback redirect URI is required.");
            if (request.TokenEndpointAuthMethod != null
                && !String.Equals(request.TokenEndpointAuthMethod, "none", StringComparison.Ordinal))
                return OAuthError("invalid_client_metadata", "Only public PKCE clients are supported.");

            string clientId = RandomValue(32);
            ClientRegistration registration = new ClientRegistration(
                request.RedirectUris.Distinct(StringComparer.Ordinal).ToArray(),
                String.IsNullOrWhiteSpace(request.ClientName) ? "MCP client" : request.ClientName.Trim());
            _Clients[clientId] = registration;
            return Results.Json(new
            {
                client_id = clientId,
                client_id_issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                client_name = registration.Name,
                redirect_uris = registration.RedirectUris,
                token_endpoint_auth_method = "none",
                grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" }
            }, statusCode: StatusCodes.Status201Created);
        }

        private IResult AuthorizeAsync(HttpContext context)
        {
            AuthorizationRequest? request = ReadAuthorizationRequest(context.Request.Query);
            string? error = ValidateAuthorizationRequest(request);
            if (error != null) return Results.BadRequest(error);

            string csrf = RandomValue(32);
            context.Response.Cookies.Append("armada_oauth_csrf", csrf, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                MaxAge = _AuthorizationCodeLifetime,
                IsEssential = true
            });
            SetBrowserSecurityHeaders(context.Response);
            ClientRegistration client = _Clients[request!.ClientId]!;
            string html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Authorize Armada</title>"
                + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"></head><body>"
                + "<main><h1>Authorize Armada read-only proof</h1><p>Client: " + H(client.Name) + "</p>"
                + "<p>This grant can read the restricted Armada proof tools. It cannot change Armada state.</p>"
                + "<form method=\"post\" action=\"/oauth/authorize\">"
                + Hidden("client_id", request.ClientId) + Hidden("redirect_uri", request.RedirectUri)
                + Hidden("state", request.State) + Hidden("scope", request.Scope)
                + Hidden("resource", request.Resource) + Hidden("code_challenge", request.CodeChallenge)
                + Hidden("code_challenge_method", request.CodeChallengeMethod) + Hidden("csrf", csrf)
                + "<label>POC owner secret <input type=\"password\" name=\"owner_secret\" required autocomplete=\"current-password\"></label>"
                + "<button type=\"submit\" name=\"decision\" value=\"approve\">Approve</button> "
                + "<button type=\"submit\" name=\"decision\" value=\"deny\">Deny</button></form></main></body></html>";
            return Results.Content(html, "text/html", Encoding.UTF8);
        }

        private async Task<IResult> ApproveAsync(HttpContext context)
        {
            IFormCollection form = await context.Request.ReadFormAsync().ConfigureAwait(false);
            AuthorizationRequest request = new AuthorizationRequest(
                form["client_id"].ToString(), form["redirect_uri"].ToString(), form["state"].ToString(),
                form["scope"].ToString(), form["resource"].ToString(), form["code_challenge"].ToString(),
                form["code_challenge_method"].ToString());
            string? error = ValidateAuthorizationRequest(request);
            if (error != null) return Results.BadRequest(error);
            if (!ConstantTimeEquals(context.Request.Cookies["armada_oauth_csrf"], form["csrf"].ToString()))
                return Results.BadRequest("The authorization request expired or failed CSRF validation.");

            if (!String.Equals(form["decision"].ToString(), "approve", StringComparison.Ordinal))
                return AuthorizationRedirect(request, "access_denied", null);
            if (!ConstantTimeEquals(_OwnerSecret, form["owner_secret"].ToString()))
            {
                SetBrowserSecurityHeaders(context.Response);
                return Results.Content("Authorization failed. Return to Grok and try the connection again.", "text/plain", Encoding.UTF8, 403);
            }

            string code = RandomValue(32);
            _Codes[Hash(code)] = new AuthorizationCode(
                request.ClientId, request.RedirectUri, request.CodeChallenge, request.Resource,
                DateTimeOffset.UtcNow.Add(_AuthorizationCodeLifetime));
            return AuthorizationRedirect(request, null, code);
        }

        private async Task<IResult> TokenAsync(HttpContext context)
        {
            IFormCollection form = await context.Request.ReadFormAsync().ConfigureAwait(false);
            string grantType = form["grant_type"].ToString();
            string clientId = form["client_id"].ToString();
            if (!_Clients.ContainsKey(clientId)) return OAuthError("invalid_client", "The client is not registered.", 401);

            if (String.Equals(grantType, "authorization_code", StringComparison.Ordinal))
            {
                string codeKey = Hash(form["code"].ToString());
                if (!_Codes.TryRemove(codeKey, out AuthorizationCode? code)
                    || code.ExpiresUtc <= DateTimeOffset.UtcNow
                    || !String.Equals(code.ClientId, clientId, StringComparison.Ordinal)
                    || !String.Equals(code.RedirectUri, form["redirect_uri"].ToString(), StringComparison.Ordinal)
                    || !String.Equals(code.Resource, ReadResource(form), StringComparison.Ordinal)
                    || !VerifyPkce(form["code_verifier"].ToString(), code.CodeChallenge))
                    return OAuthError("invalid_grant", "The authorization code is invalid or expired.");
                return IssueTokens(clientId, code.Resource);
            }

            if (String.Equals(grantType, "refresh_token", StringComparison.Ordinal))
            {
                string refreshKey = Hash(form["refresh_token"].ToString());
                if (!_RefreshTokens.TryRemove(refreshKey, out TokenGrant? grant)
                    || grant.ExpiresUtc <= DateTimeOffset.UtcNow
                    || !String.Equals(grant.ClientId, clientId, StringComparison.Ordinal)
                    || !String.Equals(grant.Resource, ReadResource(form), StringComparison.Ordinal))
                    return OAuthError("invalid_grant", "The refresh token is invalid or expired.");
                return IssueTokens(clientId, grant.Resource);
            }

            return OAuthError("unsupported_grant_type", "Only authorization_code and refresh_token are supported.");
        }

        private IResult IssueTokens(string clientId, string resource)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string accessToken = RandomValue(32);
            string refreshToken = RandomValue(48);
            _AccessTokens[Hash(accessToken)] = new TokenGrant(clientId, resource, now.Add(_AccessTokenLifetime));
            _RefreshTokens[Hash(refreshToken)] = new TokenGrant(clientId, resource, now.Add(_RefreshTokenLifetime));
            return Results.Json(new
            {
                access_token = accessToken,
                token_type = "Bearer",
                expires_in = (int)_AccessTokenLifetime.TotalSeconds,
                refresh_token = refreshToken,
                scope = _SCOPE
            });
        }

        private AuthorizationRequest? ReadAuthorizationRequest(IQueryCollection query)
        {
            if (!String.Equals(query["response_type"].ToString(), "code", StringComparison.Ordinal)) return null;
            return new AuthorizationRequest(
                query["client_id"].ToString(), query["redirect_uri"].ToString(), query["state"].ToString(),
                query["scope"].ToString(), query["resource"].ToString(), query["code_challenge"].ToString(),
                query["code_challenge_method"].ToString());
        }

        private string? ValidateAuthorizationRequest(AuthorizationRequest? request)
        {
            if (request == null || !_Clients.TryGetValue(request.ClientId, out ClientRegistration? client))
                return "The OAuth client is not registered.";
            if (!client.RedirectUris.Contains(request.RedirectUri, StringComparer.Ordinal))
                return "The OAuth redirect URI is not registered.";
            if (!String.Equals(request.CodeChallengeMethod, "S256", StringComparison.Ordinal)
                || request.CodeChallenge.Length < 43 || request.CodeChallenge.Length > 128)
                return "PKCE with S256 is required.";
            if (!String.IsNullOrEmpty(request.Resource) && !String.Equals(request.Resource, _Resource, StringComparison.Ordinal))
                return "The OAuth resource is not valid.";
            if (!String.IsNullOrEmpty(request.Scope)
                && !request.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(_SCOPE, StringComparer.Ordinal))
                return "The requested OAuth scope is not valid.";
            request.Resource = _Resource;
            request.Scope = _SCOPE;
            return null;
        }

        private IResult AuthorizationRedirect(AuthorizationRequest request, string? error, string? code)
        {
            string separator = request.RedirectUri.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            string location = request.RedirectUri + separator;
            if (error != null) location += "error=" + Uri.EscapeDataString(error);
            else location += "code=" + Uri.EscapeDataString(code!);
            if (!String.IsNullOrEmpty(request.State)) location += "&state=" + Uri.EscapeDataString(request.State);
            return Results.Redirect(location);
        }

        private string ReadResource(IFormCollection form)
        {
            string resource = form["resource"].ToString();
            return String.IsNullOrEmpty(resource) ? _Resource : resource;
        }

        private static bool IsSafeRedirectUri(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || !String.IsNullOrEmpty(uri.Fragment)) return false;
            return uri.Scheme == Uri.UriSchemeHttps
                || (uri.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(uri.Host, out IPAddress? address)
                    && IPAddress.IsLoopback(address))
                || (uri.Scheme == Uri.UriSchemeHttp && String.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        }

        private static bool VerifyPkce(string verifier, string expectedChallenge)
        {
            if (verifier.Length < 43 || verifier.Length > 128) return false;
            string actual = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return ConstantTimeEquals(actual, expectedChallenge);
        }

        private static bool ConstantTimeEquals(string? expected, string? supplied)
        {
            if (expected == null || supplied == null) return false;
            byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
            byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
            return expectedBytes.Length == suppliedBytes.Length
                && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }

        private static string RandomValue(int bytes)
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string Hash(string value)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }

        private static IResult NoStoreJson(object value)
        {
            return Results.Json(value);
        }

        private static IResult OAuthError(string error, string description, int statusCode = 400)
        {
            return Results.Json(new { error, error_description = description }, statusCode: statusCode);
        }

        private static void SetBrowserSecurityHeaders(HttpResponse response)
        {
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Content-Security-Policy"] = "default-src 'none'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["X-Content-Type-Options"] = "nosniff";
        }

        private static string H(string value) => WebUtility.HtmlEncode(value);
        private static string Hidden(string name, string value) => "<input type=\"hidden\" name=\"" + H(name)
            + "\" value=\"" + H(value) + "\">";

        private sealed record ClientRegistration(string[] RedirectUris, string Name);
        private sealed record AuthorizationCode(
            string ClientId, string RedirectUri, string CodeChallenge, string Resource, DateTimeOffset ExpiresUtc);
        private sealed record TokenGrant(string ClientId, string Resource, DateTimeOffset ExpiresUtc);

        private sealed class AuthorizationRequest
        {
            public string ClientId { get; }
            public string RedirectUri { get; }
            public string State { get; }
            public string Scope { get; set; }
            public string Resource { get; set; }
            public string CodeChallenge { get; }
            public string CodeChallengeMethod { get; }

            public AuthorizationRequest(string clientId, string redirectUri, string state, string scope,
                string resource, string codeChallenge, string codeChallengeMethod)
            {
                ClientId = clientId;
                RedirectUri = redirectUri;
                State = state;
                Scope = scope;
                Resource = resource;
                CodeChallenge = codeChallenge;
                CodeChallengeMethod = codeChallengeMethod;
            }
        }

        private sealed class DynamicClientRequest
        {
            [JsonPropertyName("redirect_uris")]
            public string[]? RedirectUris { get; set; }

            [JsonPropertyName("client_name")]
            public string? ClientName { get; set; }

            [JsonPropertyName("token_endpoint_auth_method")]
            public string? TokenEndpointAuthMethod { get; set; }
        }
    }
}
