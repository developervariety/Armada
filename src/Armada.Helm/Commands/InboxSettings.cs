namespace Armada.Helm.Commands
{
    using System.ComponentModel;
    using Spectre.Console.Cli;

    /// <summary>
    /// Settings for the inbox command.
    /// </summary>
    public class InboxSettings : BaseSettings
    {
        /// <summary>
        /// Show only critical items.
        /// </summary>
        [CommandOption("--critical")]
        [Description("Show only critical items")]
        public bool CriticalOnly { get; set; } = false;
    }
}
