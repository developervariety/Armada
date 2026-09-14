namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Reads the applied schema version with a short-lived driver. The driver is not initialized, so no
    /// migration runs.
    /// </summary>
    public sealed class SelfDeployDatabaseSchemaVersionReader : ISelfDeploySchemaVersionReader
    {
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        public SelfDeployDatabaseSchemaVersionReader(DatabaseSettings settings, LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <inheritdoc />
        public async Task<int> ReadAsync(CancellationToken token = default)
        {
            using (DatabaseDriver database = DatabaseDriverFactory.Create(_Settings, _Logging))
            {
                return await database.GetSchemaVersionAsync(token).ConfigureAwait(false);
            }
        }
    }
}
