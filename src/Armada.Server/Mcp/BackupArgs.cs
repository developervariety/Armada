namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP tool arguments for the backup command.
    /// </summary>
    public class BackupArgs
    {
        /// <summary>
        /// Optional output path for the ZIP backup file. Defaults to ~/.armada/backups/armada-backup-{timestamp}.zip.
        /// </summary>
        public string? OutputPath { get; set; }
    }
}
