namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// Detects and retires the well-known seeded credentials: the seeded admin account with the default password and
    /// the "default" bearer token. Used by the startup bind guard and by password changes.
    /// </summary>
    public class DefaultCredentialService
    {
        #region Public-Members

        /// <summary>
        /// Environment variable that, while the seeded admin still has the default password, replaces that password
        /// before the server binds (used by container and other unattended installs).
        /// </summary>
        public const string InitialAdminPasswordEnvironmentVariable = "ARMADA_INITIAL_ADMIN_PASSWORD";

        #endregion

        #region Private-Members

        private readonly string _Header = "[DefaultCredentialService] ";
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        public DefaultCredentialService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a hostname binds only to the loopback interface.
        /// </summary>
        /// <param name="hostname">Listener hostname.</param>
        /// <returns>True for localhost, 127.0.0.0/8, and ::1. Wildcard and empty names are not loopback.</returns>
        public static bool IsLoopbackHostname(string? hostname)
        {
            if (String.IsNullOrWhiteSpace(hostname)) return false;
            string host = hostname.Trim().Trim('[', ']');
            if (String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (IPAddress.TryParse(host, out IPAddress? address)) return IPAddress.IsLoopback(address);
            return false;
        }

        /// <summary>
        /// Describe which default credentials are still in use. An empty list means none are.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Human-readable descriptions.</returns>
        public async Task<List<string>> GetDefaultsInUseAsync(CancellationToken token = default)
        {
            List<string> found = new List<string>();
            Dictionary<string, UserMaster> candidates = new Dictionary<string, UserMaster>(StringComparer.Ordinal);
            foreach (UserMaster user in await _Database.Users.ReadByEmailAnyTenantAsync(Constants.DefaultUserEmail, token).ConfigureAwait(false))
                candidates[user.Id] = user;
            UserMaster? firstBoot = await _Database.Users.ReadByIdAsync(Constants.DefaultUserId, token).ConfigureAwait(false);
            if (firstBoot != null) candidates[firstBoot.Id] = firstBoot;

            foreach (UserMaster user in candidates.Values)
            {
                if (user.Active && user.UsesDefaultPassword())
                    found.Add("user " + user.Email + " in tenant " + user.TenantId + " still uses the default password");
            }

            Credential? defaultCredential = await _Database.Credentials.ReadByIdAsync(Constants.DefaultCredentialId, token).ConfigureAwait(false);
            if (defaultCredential != null && defaultCredential.Active && AuthenticationService.IsSeededDefaultToken(defaultCredential))
            {
                UserMaster? owner = await _Database.Users.ReadByIdAsync(defaultCredential.UserId, token).ConfigureAwait(false);
                if (owner != null && owner.Active && owner.UsesDefaultPassword())
                    found.Add("the seeded bearer token \"" + Constants.DefaultBearerToken + "\" is active");
            }

            return found;
        }

        /// <summary>
        /// Apply the startup policy for seeded default credentials before the listener binds. Applies the initial
        /// admin password when one is supplied, retires the seeded bearer token when its owner no longer uses the default
        /// password, then refuses a non-loopback listener while any default credential remains, unless the explicit
        /// override is set. The override and a loopback listener log a warning instead of refusing.
        /// </summary>
        /// <param name="hostname">Listener hostname.</param>
        /// <param name="allowDefaultCredentialsOnNetwork">Whether the operator accepted the risk of a non-loopback listener with default credentials.</param>
        /// <param name="initialAdminPassword">Initial admin password, or null/empty to leave the seeded password alone.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The default credentials that remain in use. Empty means none.</returns>
        /// <exception cref="DefaultCredentialPolicyException">The listener is non-loopback with default credentials and no override, or the initial password is not acceptable.</exception>
        public async Task<List<string>> EnforceStartupPolicyAsync(
            string? hostname,
            bool allowDefaultCredentialsOnNetwork,
            string? initialAdminPassword,
            CancellationToken token = default)
        {
            await ApplyInitialAdminPasswordAsync(initialAdminPassword, token).ConfigureAwait(false);
            await RetireDefaultBearerTokenAsync(token).ConfigureAwait(false);

            List<string> inUse = await GetDefaultsInUseAsync(token).ConfigureAwait(false);
            if (inUse.Count == 0) return inUse;

            string summary = String.Join("; ", inUse);
            if (IsLoopbackHostname(hostname))
            {
                _Logging.Warn(_Header + "default credentials are in use (" + summary + "); change the admin password before exposing this Admiral beyond localhost");
                return inUse;
            }

            if (allowDefaultCredentialsOnNetwork)
            {
                _Logging.Warn(_Header + "WARNING: listening on non-loopback hostname '" + hostname + "' with default credentials in use ("
                    + summary + ") because AllowDefaultCredentialsOnNetwork is set. Anyone who can reach this port can sign in with the published default credentials.");
                return inUse;
            }

            string message =
                "Refusing to listen on non-loopback hostname '" + hostname + "' while default credentials are in use (" + summary + "). "
                + "Set the " + InitialAdminPasswordEnvironmentVariable + " environment variable (it applies while the seeded admin still has the default password), "
                + "or start on localhost and change the admin password (PUT /api/v1/account/password), "
                + "or set AllowDefaultCredentialsOnNetwork to true in settings to accept the risk.";
            _Logging.Warn(_Header + message);
            throw new DefaultCredentialPolicyException(message);
        }

        /// <summary>
        /// Apply an initial admin password to the seeded default admin while it still has the default password, and
        /// retire the seeded bearer token. Does nothing when the password was already changed.
        /// </summary>
        /// <param name="password">New password.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the password was applied.</returns>
        /// <exception cref="DefaultCredentialPolicyException">The password is shorter than the minimum or equals the default password.</exception>
        public async Task<bool> ApplyInitialAdminPasswordAsync(string? password, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(password)) return false;
            if (password.Length < PasswordChangeRequest.MinimumLength || String.Equals(password, Constants.DefaultUserPassword, StringComparison.Ordinal))
                throw new DefaultCredentialPolicyException(InitialAdminPasswordEnvironmentVariable + " must be at least " + PasswordChangeRequest.MinimumLength + " characters and not the default password.");

            UserMaster? user = await _Database.Users.ReadByIdAsync(Constants.DefaultUserId, token).ConfigureAwait(false);
            if (user == null || !user.UsesDefaultPassword()) return false;

            user.PasswordSha256 = UserMaster.ComputePasswordHash(password);
            user.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Users.UpdateAsync(user, token).ConfigureAwait(false);
            await RetireDefaultBearerTokenAsync(token).ConfigureAwait(false);
            _Logging.Info(_Header + "applied the initial admin password from " + InitialAdminPasswordEnvironmentVariable + " and retired the seeded bearer token");
            return true;
        }

        /// <summary>
        /// Deactivate the seeded "default" bearer token when its owner no longer uses the default password.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the token was deactivated by this call.</returns>
        public async Task<bool> RetireDefaultBearerTokenAsync(CancellationToken token = default)
        {
            Credential? credential = await _Database.Credentials.ReadByIdAsync(Constants.DefaultCredentialId, token).ConfigureAwait(false);
            if (credential == null || !credential.Active || !AuthenticationService.IsSeededDefaultToken(credential)) return false;

            UserMaster? owner = await _Database.Users.ReadByIdAsync(credential.UserId, token).ConfigureAwait(false);
            if (owner != null && owner.UsesDefaultPassword()) return false;

            credential.Active = false;
            credential.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Credentials.UpdateAsync(credential, token).ConfigureAwait(false);
            _Logging.Info(_Header + "deactivated the seeded \"" + Constants.DefaultBearerToken + "\" bearer token because the default admin password has been changed");
            return true;
        }

        #endregion
    }
}
