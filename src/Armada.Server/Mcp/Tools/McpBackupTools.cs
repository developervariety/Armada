namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers MCP tools for backup and restore operations.
    /// </summary>
    public static class McpBackupTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Registers backup and restore MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="backups">Shared provider-aware backup and restore service.</param>
        public static void Register(RegisterToolDelegate register, DatabaseBackupService backups)
        {
            register(
                "armada_backup",
                "Create a verified provider-native backup of the configured Armada database (SQLite, PostgreSQL, MySQL or SQL Server) " +
                "and archive it with settings and a manifest whose schema version and record counts come from that provider. " +
                "Fails with a named reason instead of reporting success when the native backup or its isolated restore check fails.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        outputPath = new { type = "string", description = "Output path for the ZIP file. Default: <dataDirectory>/backups/armada-backup-{timestamp}.zip" }
                    }
                },
                async (args) =>
                {
                    BackupArgs backupArgs = args != null
                        ? JsonSerializer.Deserialize<BackupArgs>(args.Value, _JsonOptions) ?? new BackupArgs()
                        : new BackupArgs();

                    DatabaseBackupResult result = await backups.BackupAsync(backupArgs.OutputPath).ConfigureAwait(false);
                    return result;
                });

            register(
                "armada_restore",
                "Restore a SQLite Armada database and settings from a ZIP backup file after a verified safety backup. " +
                "Refused with restore_unsupported_for_provider_<type> on PostgreSQL, MySQL and SQL Server, and with backup_provider_mismatch " +
                "for an archive from another provider. Server restart recommended after restore.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Path to the ZIP backup file to restore from" }
                    },
                    required = new[] { "filePath" }
                },
                async (args) =>
                {
                    RestoreArgs restoreArgs = args != null
                        ? JsonSerializer.Deserialize<RestoreArgs>(args.Value, _JsonOptions) ?? new RestoreArgs()
                        : new RestoreArgs();

                    if (String.IsNullOrEmpty(restoreArgs.FilePath))
                        throw new ArgumentException("filePath is required");

                    DatabaseRestoreResult result = await backups.RestoreAsync(restoreArgs.FilePath).ConfigureAwait(false);
                    return result;
                });
        }
    }
}
