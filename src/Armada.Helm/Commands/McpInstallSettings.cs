namespace Armada.Helm.Commands
{
    using System.ComponentModel;
    using System.Text.Json;
    using System.Threading;
    using Spectre.Console;
    using Spectre.Console.Cli;
    using Armada.Core.Settings;

    /// <summary>
    /// Settings for MCP install command.
    /// </summary>
    public class McpInstallSettings : BaseSettings
    {
        /// <summary>
        /// Only show the configuration, don't write it.
        /// </summary>
        [Description("Only display the configuration, don't write it")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; set; } = false;
    }
}
