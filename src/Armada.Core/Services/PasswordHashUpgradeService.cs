namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>Upgrades legacy password rows without replacing unrelated user fields.</summary>
    public static class PasswordHashUpgradeService
    {
        #region Private-Members

        private const string _Header = "[PasswordHashUpgradeService] ";

        #endregion

        #region Public-Methods

        /// <summary>Upgrade legacy SHA-256 values before the server accepts requests.</summary>
        public static async Task<int> UpgradeLegacyHashesAsync(DatabaseDriver database, LoggingModule logging, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (logging == null) throw new ArgumentNullException(nameof(logging));
            int upgraded = 0;
            EnumerationQuery query = new EnumerationQuery { PageSize = 1000 };
            while (true)
            {
                EnumerationResult<UserMaster> page = await database.Users.EnumerateAsync(query, token).ConfigureAwait(false);
                foreach (UserMaster user in page.Objects)
                {
                    token.ThrowIfCancellationRequested();
                    if (PasswordHasher.IsAdaptiveHash(user.PasswordSha256)) continue;
                    try
                    {
                        string replacement = PasswordHasher.HashSha256Hex(user.PasswordSha256);
                        if (await database.Users.UpgradePasswordAsync(user.TenantId, user.Id, user.PasswordSha256, replacement, token).ConfigureAwait(false)) upgraded++;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        logging.Warn(_Header + "password upgrade failed for user " + user.Id + ": " + ex.Message);
                    }
                }
                if (query.PageNumber >= page.TotalPages) break;
                query.PageNumber++;
            }
            logging.Info(_Header + "upgraded legacy passwords: " + upgraded);
            return upgraded;
        }

        #endregion
    }
}
