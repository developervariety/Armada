namespace Armada.Helm.Commands
{
    using System.ComponentModel;
    using Spectre.Console.Cli;

    /// <summary>
    /// Settings for the ask command.
    /// </summary>
    public class AskSettings : BaseSettings
    {
        /// <summary>
        /// The natural-language question.
        /// </summary>
        [CommandArgument(0, "<message>")]
        [Description("What you want to ask about fleet state")]
        public string Message { get; set; } = string.Empty;
    }
}
