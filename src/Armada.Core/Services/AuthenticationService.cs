namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Service for authenticating API requests via bearer tokens, session tokens, or API keys.
    /// </summary>
    public class AuthenticationService : IAuthenticationService
    {
        #region Private-Members

        private readonly string _Header = "[AuthenticationService] ";
        private readonly DatabaseDriver _Database;
        private readonly ISessionTokenService _SessionTokenService;
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="sessionTokenService">Session token service.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="logging">Logging module.</param>
        public AuthenticationService(
            DatabaseDriver database,
            ISessionTokenService sessionTokenService,
            ArmadaSettings settings,
            LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _SessionTokenService = sessionTokenService ?? throw new ArgumentNullException(nameof(sessionTokenService));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<AuthContext> AuthenticateAsync(
            string? authorizationHeader,
            string? sessionTokenHeader,
            string? apiKeyHeader,
            CancellationToken token = default)
        {
            string? bearerCandidate = GetBearerCandidate(authorizationHeader);
            bool hasAskFormat = SessionTokenService.HasAskTokenFormat(bearerCandidate)
                || SessionTokenService.HasAskTokenFormat(sessionTokenHeader);
            if (hasAskFormat)
            {
                AuthContext? askCredential = GetAskTurnCredential(bearerCandidate) ?? GetAskTurnCredential(sessionTokenHeader);
                if (askCredential != null)
                    return new AuthContext { AskThreadId = askCredential.AskThreadId, AskTurnId = askCredential.AskTurnId };
                return new AuthContext();
            }
            // 1. Check Bearer token (canonical auth path)
            if (!string.IsNullOrEmpty(authorizationHeader) && authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string bearerToken = authorizationHeader.Substring(7).Trim();
                if (!string.IsNullOrEmpty(bearerToken))
                {
                    AuthContext? bearerCtx = await AuthenticateByBearerTokenAsync(bearerToken, token).ConfigureAwait(false);
                    if (bearerCtx != null) return bearerCtx;

                    // A session token may also arrive as a bearer credential. A launched chat captain's
                    // runtime MCP configuration presents the caller's session token in the Authorization
                    // header (the CLI runtimes send a bearer credential, not an X-Token header), so accept a
                    // session token from the bearer header with the token owner's own scope, exactly as the
                    // X-Token path does. Persistent bearer credentials keep precedence above.
                    AuthContext? bearerSessionCtx = await AuthenticateBySessionTokenAsync(bearerToken, token).ConfigureAwait(false);
                    if (bearerSessionCtx != null) return bearerSessionCtx;
                }
            }

            // 2. Check session token (X-Token)
            if (!string.IsNullOrEmpty(sessionTokenHeader))
            {
                AuthContext? sessionCtx = await AuthenticateBySessionTokenAsync(sessionTokenHeader, token).ConfigureAwait(false);
                if (sessionCtx != null) return sessionCtx;
            }

            // 3. Check API key (X-Api-Key, deprecated but supported)
            if (!string.IsNullOrEmpty(apiKeyHeader) && !string.IsNullOrEmpty(_Settings.ApiKey))
            {
                if (apiKeyHeader == _Settings.ApiKey)
                {
                    return AuthContext.Authenticated(
                        Constants.DefaultTenantId,
                        Constants.DefaultUserId,
                        true,
                        true,
                        "ApiKey",
                        null,
                        "API Key");
                }
            }

            // 4. Unauthenticated
            return new AuthContext();
        }

        /// <inheritdoc />
        public async Task<AuthContext> AuthenticateMcpAsync(string? authorizationHeader, string? sessionTokenHeader, string? apiKeyHeader, CancellationToken token = default)
        {
            string? candidate = GetSessionCandidate(authorizationHeader, sessionTokenHeader);
            AuthContext? scoped = String.IsNullOrWhiteSpace(candidate) ? null : _SessionTokenService.ValidateToken(candidate);
            if (SessionTokenService.HasAskTokenFormat(candidate)
                && (scoped == null || String.IsNullOrWhiteSpace(scoped.AskThreadId) || String.IsNullOrWhiteSpace(scoped.AskTurnId)))
                return new AuthContext();
            if (scoped != null && (!String.IsNullOrWhiteSpace(scoped.AskThreadId) || !String.IsNullOrWhiteSpace(scoped.AskTurnId)))
            {
                if (String.IsNullOrWhiteSpace(scoped.AskThreadId) || String.IsNullOrWhiteSpace(scoped.AskTurnId)
                    || String.IsNullOrWhiteSpace(scoped.TenantId) || String.IsNullOrWhiteSpace(scoped.UserId)) return new AuthContext();
                try
                {
                    UserMaster? user = await _Database.Users.ReadByIdAsync(scoped.UserId, token).ConfigureAwait(false);
                    if (user == null || !user.Active || user.TenantId != scoped.TenantId) return new AuthContext();
                    TenantMetadata? tenant = await _Database.Tenants.ReadAsync(scoped.TenantId, token).ConfigureAwait(false);
                    if (tenant == null || !tenant.Active) return new AuthContext();
                    AskThread? thread = await _Database.AskThreads.ReadAsync(scoped.TenantId, scoped.UserId, scoped.AskThreadId, token).ConfigureAwait(false);
                    if (thread == null || thread.ActiveTurnId != scoped.AskTurnId) return new AuthContext();
                    AskTurn? turn = await _Database.AskThreads.ReadTurnAsync(scoped.TenantId, scoped.UserId, scoped.AskThreadId, scoped.AskTurnId, token).ConfigureAwait(false);
                    if (turn == null || turn.State != AskTurnStateEnum.Running || turn.ThreadId != thread.Id || turn.RequestId != thread.ActiveTurnId) return new AuthContext();
                    scoped.IsAdmin = user.IsAdmin;
                    scoped.IsTenantAdmin = user.IsAdmin || user.IsTenantAdmin;
                    scoped.PrincipalDisplay = user.Email;
                    return scoped;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "saved-turn MCP authentication failed: " + ex.Message);
                    return new AuthContext();
                }
            }
            return await AuthenticateAsync(authorizationHeader, sessionTokenHeader, apiKeyHeader, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AuthContext> AuthenticateWithCredentialsAsync(
            string tenantId,
            string email,
            string password,
            CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
                return new AuthContext();

            // Email comparison is case-insensitive
            email = email.ToLowerInvariant();

            try
            {
                UserMaster? user = await _Database.Users.ReadByEmailAsync(tenantId, email, token).ConfigureAwait(false);
                if (user == null) return new AuthContext();
                if (!user.Active) return new AuthContext();
                if (!user.VerifyPassword(password)) return new AuthContext();

                // Verify tenant is active
                TenantMetadata? tenant = await _Database.Tenants.ReadAsync(tenantId, token).ConfigureAwait(false);
                if (tenant == null || !tenant.Active) return new AuthContext();

                if (PasswordHasher.NeedsRehash(user.PasswordSha256))
                {
                    string replacement = PasswordHasher.HashPassword(password);
                    await _Database.Users.UpgradePasswordAsync(tenantId, user.Id, user.PasswordSha256, replacement, token).ConfigureAwait(false);
                }

                return AuthContext.Authenticated(
                    tenantId,
                    user.Id,
                    user.IsAdmin,
                    user.IsAdmin || user.IsTenantAdmin,
                    "Credentials",
                    null,
                    user.Email);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "credential authentication failed: " + ex.Message);
                return new AuthContext();
            }
        }

        /// <summary>
        /// Whether a credential is the seeded default credential still carrying the well-known token.
        /// </summary>
        /// <param name="credential">Credential.</param>
        /// <returns>True for the seeded default token.</returns>
        public static bool IsSeededDefaultToken(Credential credential)
        {
            if (credential == null) return false;
            return String.Equals(credential.Id, Constants.DefaultCredentialId, StringComparison.Ordinal)
                && String.Equals(credential.BearerToken, Constants.DefaultBearerToken, StringComparison.Ordinal);
        }

        #endregion

        #region Private-Methods

        private async Task<AuthContext?> AuthenticateByBearerTokenAsync(string bearerToken, CancellationToken token)
        {
            try
            {
                Credential? credential = await _Database.Credentials.ReadByBearerTokenAsync(bearerToken, token).ConfigureAwait(false);
                if (credential == null || !credential.Active) return null;

                UserMaster? user = await _Database.Users.ReadByIdAsync(credential.UserId, token).ConfigureAwait(false);
                if (user == null || !user.Active) return null;

                // The well-known seeded token works only while its owner still has the default password, so it stops
                // working the moment real credentials replace the default.
                if (IsSeededDefaultToken(credential) && !user.UsesDefaultPassword())
                {
                    _Logging.Debug(_Header + "rejected the seeded default bearer token: the default admin password has been changed");
                    return null;
                }

                TenantMetadata? tenant = await _Database.Tenants.ReadAsync(credential.TenantId, token).ConfigureAwait(false);
                if (tenant == null || !tenant.Active) return null;

                return AuthContext.Authenticated(
                    credential.TenantId,
                    credential.UserId,
                    user.IsAdmin,
                    user.IsAdmin || user.IsTenantAdmin,
                    "Bearer",
                    credential.Id,
                    !string.IsNullOrEmpty(credential.Name) ? credential.Name : user.Email);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "bearer token authentication failed: " + ex.Message);
                return null;
            }
        }

        private async Task<AuthContext?> AuthenticateBySessionTokenAsync(string encryptedToken, CancellationToken token)
        {
            try
            {
                AuthContext? ctx = _SessionTokenService.ValidateToken(encryptedToken);
                if (ctx == null) return null;

                // Verify user is still active
                UserMaster? user = await _Database.Users.ReadByIdAsync(ctx.UserId!, token).ConfigureAwait(false);
                if (user == null || !user.Active) return null;

                // Verify tenant is still active
                TenantMetadata? tenant = await _Database.Tenants.ReadAsync(ctx.TenantId!, token).ConfigureAwait(false);
                if (tenant == null || !tenant.Active) return null;

                ctx.IsAdmin = user.IsAdmin;
                ctx.IsTenantAdmin = user.IsAdmin || user.IsTenantAdmin;
                ctx.PrincipalDisplay = user.Email;
                return ctx;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "session token authentication failed: " + ex.Message);
                return null;
            }
        }

        private AuthContext? GetAskTurnCredential(string? candidate)
        {
            AuthContext? context = String.IsNullOrWhiteSpace(candidate) ? null : _SessionTokenService.ValidateToken(candidate);
            return context != null && (!String.IsNullOrWhiteSpace(context.AskThreadId) || !String.IsNullOrWhiteSpace(context.AskTurnId)) ? context : null;
        }

        private static string? GetBearerCandidate(string? authorizationHeader)
        {
            if (String.IsNullOrWhiteSpace(authorizationHeader) || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
            return authorizationHeader.Substring(7).Trim();
        }

        private static string? GetSessionCandidate(string? authorizationHeader, string? sessionTokenHeader)
        {
            if (!String.IsNullOrWhiteSpace(authorizationHeader) && authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return authorizationHeader.Substring(7).Trim();
            return sessionTokenHeader;
        }

        #endregion
    }
}
