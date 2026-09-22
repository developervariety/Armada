namespace Armada.Helm.Commands
{
    using System.ComponentModel;
    using System.Threading;
    using Spectre.Console;
    using Spectre.Console.Cli;
    using Armada.Helm.Infrastructure;

    /// <summary>
    /// Stop the Admiral server.
    /// </summary>
    [Description("Stop the Admiral server")]
    public class ServerStopCommand : BaseCommand<ServerStopSettings>
    {
        #region Public-Methods

        /// <inheritdoc />
        protected override async Task<int> ExecuteAsync(CommandContext context, ServerStopSettings settings, CancellationToken cancellationToken)
        {
            return await StopAsync(CreateAdmiralShutdown(), cancellationToken).ConfigureAwait(false);
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Stop the Admiral and wait for it to exit, so its executable is unlocked for a following build. Exit code
        /// 0 means the server stopped; any other outcome, including a server that was not running, is non-zero.
        /// </summary>
        /// <param name="shutdown">Stop logic for the target Admiral.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Process exit code.</returns>
        internal static async Task<int> StopAsync(AdmiralShutdown shutdown, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("[dim]Stopping Admiral server...[/]");
            AdmiralStopResult result = await shutdown.StopAsync(cancellationToken).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case AdmiralStopOutcomeEnum.Stopped:
                    AnsiConsole.MarkupLine("[green]" + Markup.Escape(result.Message) + "[/]");
                    return 0;
                case AdmiralStopOutcomeEnum.NotRunning:
                    AnsiConsole.MarkupLine("[gold1]" + Markup.Escape(result.Message) + "[/]");
                    return 1;
                default:
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(result.Message) + "[/]");
                    return 1;
            }
        }

        #endregion
    }
}
