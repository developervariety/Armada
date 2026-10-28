namespace Armada.Server.Routes
{
    using System.Text.Json;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Server;
    using Armada.Core;
    using ArmadaConstants = Armada.Core.Constants;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// REST API routes for authentication management.
    /// </summary>
    public class AuthRoutes
    {
        private readonly ISessionTokenService _sessionTokenService;
        private readonly IAuthenticationService _authenticationService;
        private readonly DatabaseDriver _database;
        private readonly ArmadaSettings _settings;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly DefaultCredentialService? _defaults;
        private readonly LoginRateLimiter _rateLimiter;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="sessionTokenService">Session token service.</param>
        /// <param name="authenticationService">Authentication service.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        /// <param name="defaults">Default credential service; when supplied, a password change retires the seeded bearer token.</param>
        /// <param name="rateLimiter">Credential-login rate limiter; null builds one from <c>settings.LoginRateLimit</c>.</param>
        public AuthRoutes(
            ISessionTokenService sessionTokenService,
            IAuthenticationService authenticationService,
            DatabaseDriver database,
            ArmadaSettings settings,
            JsonSerializerOptions jsonOptions,
            DefaultCredentialService? defaults = null,
            LoginRateLimiter? rateLimiter = null)
        {
            _sessionTokenService = sessionTokenService;
            _authenticationService = authenticationService;
            _database = database;
            _settings = settings;
            _jsonOptions = jsonOptions;
            _defaults = defaults;
            _rateLimiter = rateLimiter ?? new LoginRateLimiter(settings.LoginRateLimit);
        }

        /// <summary>
        /// Register routes with the application.
        /// </summary>
        /// <param name="app">Webserver.</param>
        /// <param name="authenticate">Authentication middleware.</param>
        /// <param name="authz">Authorization service.</param>
        public void Register(
            Webserver app,
            Func<WatsonWebserver.Core.HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            // Authentication
            app.Post("/api/v1/authenticate", async (ApiRequest req) =>
            {
                string body = req.Http.Request.DataAsString;
                AuthenticateRequest? authReq = null;
                if (!string.IsNullOrEmpty(body))
                    authReq = JsonSerializer.Deserialize<AuthenticateRequest>(body, _jsonOptions);

                // Try header-based auth first
                AuthContext headerCtx = await authenticate(req.Http).ConfigureAwait(false);
                if (!String.IsNullOrWhiteSpace(headerCtx.AskThreadId) || !String.IsNullOrWhiteSpace(headerCtx.AskTurnId))
                {
                    req.Http.Response.StatusCode = 403;
                    return (object)new AuthenticateResult { Success = false };
                }
                if (headerCtx.IsAuthenticated)
                {
                    // A mission credential cannot be exchanged for an unrestricted user session.
                    if (!String.IsNullOrWhiteSpace(headerCtx.MissionId) || !String.IsNullOrWhiteSpace(headerCtx.AskThreadId) || !String.IsNullOrWhiteSpace(headerCtx.AskTurnId))
                    {
                        req.Http.Response.StatusCode = 403;
                        return (object)new AuthenticateResult { Success = false };
                    }
                    AuthenticateResult result = _sessionTokenService.CreateToken(headerCtx.TenantId!, headerCtx.UserId!);
                    UserMaster? headerUser = await _database.Users.ReadByIdAsync(headerCtx.UserId!).ConfigureAwait(false);
                    result.PasswordChangeRequired = headerUser != null && headerUser.UsesDefaultPassword();
                    return (object)result;
                }

                // Try email/password
                if (authReq != null && !string.IsNullOrEmpty(authReq.TenantId) && !string.IsNullOrEmpty(authReq.Email) && !string.IsNullOrEmpty(authReq.Password))
                {
                    // Only guessable credentials are throttled: a locked account or client address is refused even with
                    // the right password, so a lockout cannot be bypassed by continuing to guess.
                    string? address = ClientAddress(req.Http);
                    TimeSpan? retryAfter = _rateLimiter.CheckPasswordLogin(authReq.TenantId, authReq.Email, address);
                    if (retryAfter != null)
                    {
                        req.Http.Response.StatusCode = 429;
                        req.Http.Response.Headers.Add("Retry-After", LoginRateLimiter.ToRetryAfterSeconds(retryAfter.Value));
                        return (object)new AuthenticateResult { Success = false };
                    }

                    AuthContext credCtx = await _authenticationService.AuthenticateWithCredentialsAsync(authReq.TenantId, authReq.Email, authReq.Password).ConfigureAwait(false);
                    if (credCtx.IsAuthenticated)
                    {
                        _rateLimiter.RecordPasswordSuccess(authReq.TenantId, authReq.Email);
                        AuthenticateResult result = _sessionTokenService.CreateToken(credCtx.TenantId!, credCtx.UserId!);
                        UserMaster? credUser = await _database.Users.ReadByIdAsync(credCtx.UserId!).ConfigureAwait(false);
                        result.PasswordChangeRequired = credUser != null && credUser.UsesDefaultPassword();
                        return (object)result;
                    }

                    _rateLimiter.RecordPasswordFailure(authReq.TenantId, authReq.Email, address);
                }

                req.Http.Response.StatusCode = 401;
                return (object)new AuthenticateResult { Success = false };
            },
            api => api.WithTag("Authentication").WithSummary("Authenticate and get session token"));

            // WhoAmI
            app.Get("/api/v1/whoami", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                TenantMetadata? tenant = await _database.Tenants.ReadAsync(ctx.TenantId!).ConfigureAwait(false);
                UserMaster? user = await _database.Users.ReadByIdAsync(ctx.UserId!).ConfigureAwait(false);

                return (object)new WhoAmIResult
                {
                    Tenant = tenant,
                    User = user != null ? UserMaster.Redact(user) : null,
                    PasswordChangeRequired = user != null && user.UsesDefaultPassword()
                };
            },
            api => api.WithTag("Authentication").WithSummary("Get current identity"));

            // Self-service password change: the caller proves the current password
            app.Put("/api/v1/account/password", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    return RouteAuthRefusal.Refuse(req, ctx);
                }

                PasswordChangeRequest? change = null;
                try { change = JsonSerializer.Deserialize<PasswordChangeRequest>(req.Http.Request.DataAsString, _jsonOptions); }
                catch (JsonException) { }
                if (change == null || String.IsNullOrEmpty(change.CurrentPassword) || String.IsNullOrEmpty(change.NewPassword))
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new { Error = "CurrentPassword and NewPassword are required" };
                }
                if (change.NewPassword.Length < PasswordChangeRequest.MinimumLength
                    || String.Equals(change.NewPassword, ArmadaConstants.DefaultUserPassword, StringComparison.Ordinal))
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new { Error = "NewPassword must be at least " + PasswordChangeRequest.MinimumLength + " characters and must not be the default password" };
                }
                if (String.Equals(change.NewPassword, change.CurrentPassword, StringComparison.Ordinal))
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new { Error = "NewPassword must differ from CurrentPassword" };
                }

                UserMaster? user = await _database.Users.ReadByIdAsync(ctx.UserId!).ConfigureAwait(false);
                if (user == null || String.Equals(user.Id, ArmadaConstants.SystemUserId, StringComparison.Ordinal))
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new { Error = "This identity has no password to change" };
                }
                if (!user.VerifyPassword(change.CurrentPassword))
                {
                    req.Http.Response.StatusCode = 403;
                    return (object)new { Error = "CurrentPassword is incorrect" };
                }

                user.PasswordSha256 = UserMaster.ComputePasswordHash(change.NewPassword);
                user.LastUpdateUtc = DateTime.UtcNow;
                user = await _database.Users.UpdateAsync(user).ConfigureAwait(false);
                if (_defaults != null) await _defaults.RetireDefaultBearerTokenAsync().ConfigureAwait(false);

                return (object)new WhoAmIResult
                {
                    Tenant = await _database.Tenants.ReadAsync(user.TenantId).ConfigureAwait(false),
                    User = UserMaster.Redact(user),
                    PasswordChangeRequired = user.UsesDefaultPassword()
                };
            },
            api => api
                .WithTag("Authentication")
                .WithSummary("Change the caller's password")
                .WithDescription("Changes the authenticated user's password after verifying the current one. Changing the seeded admin's default password also deactivates the seeded \"default\" bearer token.")
                .WithRequestBody(OpenApiJson.BodyFor<PasswordChangeRequest>("Current and new password", true)));

            // Tenant Lookup
            app.Post("/api/v1/tenants/lookup", async (ApiRequest req) =>
            {
                string body = req.Http.Request.DataAsString;
                TenantLookupRequest? lookupReq = JsonSerializer.Deserialize<TenantLookupRequest>(body, _jsonOptions);
                if (lookupReq == null || string.IsNullOrEmpty(lookupReq.Email))
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new { Error = "Email is required" };
                }

                List<UserMaster> users = await _database.Users.ReadByEmailAnyTenantAsync(lookupReq.Email.ToLowerInvariant()).ConfigureAwait(false);
                TenantLookupResult result = new TenantLookupResult();
                foreach (UserMaster u in users)
                {
                    if (u.TenantId == ArmadaConstants.SystemTenantId) continue;
                    TenantMetadata? t = await _database.Tenants.ReadAsync(u.TenantId).ConfigureAwait(false);
                    if (t != null && t.Active)
                        result.Tenants.Add(new TenantListEntry(t.Id, t.Name));
                }
                return (object)result;
            },
            api => api.WithTag("Authentication").WithSummary("Look up tenants by email"));

            // Onboarding
            app.Post("/api/v1/onboarding", async (ApiRequest req) =>
            {
                if (!_settings.AllowSelfRegistration)
                {
                    req.Http.Response.StatusCode = 403;
                    return (object)new OnboardingResult { Success = false, ErrorMessage = "Self-registration is disabled" };
                }

                string body = req.Http.Request.DataAsString;
                OnboardingRequest? onbReq = JsonSerializer.Deserialize<OnboardingRequest>(body, _jsonOptions);
                if (onbReq == null || string.IsNullOrEmpty(onbReq.TenantId) || string.IsNullOrEmpty(onbReq.Email) || string.IsNullOrEmpty(onbReq.Password))
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new OnboardingResult { Success = false, ErrorMessage = "TenantId, Email, and Password are required" };
                }

                TenantMetadata? tenant = await _database.Tenants.ReadAsync(onbReq.TenantId).ConfigureAwait(false);
                if (tenant == null || !tenant.Active)
                {
                    req.Http.Response.StatusCode = 400;
                    return (object)new OnboardingResult { Success = false, ErrorMessage = "Tenant not found or inactive" };
                }

                UserMaster? existing = await _database.Users.ReadByEmailAsync(onbReq.TenantId, onbReq.Email).ConfigureAwait(false);
                if (existing != null)
                {
                    req.Http.Response.StatusCode = 409;
                    return (object)new OnboardingResult { Success = false, ErrorMessage = "Email already exists in this tenant" };
                }

                UserMaster newUser = new UserMaster(onbReq.TenantId, onbReq.Email, onbReq.Password);
                newUser.FirstName = onbReq.FirstName;
                newUser.LastName = onbReq.LastName;
                newUser.IsAdmin = false;
                newUser.IsTenantAdmin = false;
                await _database.Users.CreateAsync(newUser).ConfigureAwait(false);

                Credential newCred = new Credential(onbReq.TenantId, newUser.Id);
                await _database.Credentials.CreateAsync(newCred).ConfigureAwait(false);

                return (object)new OnboardingResult
                {
                    Success = true,
                    Tenant = tenant,
                    User = UserMaster.Redact(newUser),
                    Credential = newCred
                };
            },
            api => api.WithTag("Authentication").WithSummary("Self-register a new user"));
        }

        /// <summary>
        /// Client address of a request as seen by the listener; forwarding headers are not trusted.
        /// </summary>
        /// <param name="ctx">HTTP context.</param>
        /// <returns>Address, or null.</returns>
        public static string? ClientAddress(HttpContextBase ctx)
        {
            string? address = ctx?.Request?.Source?.IpAddress?.ToString();
            return String.IsNullOrEmpty(address) ? null : address;
        }
    }
}
