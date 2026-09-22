namespace Armada.Helm.Commands
{
    using System;
    using System.ComponentModel;
    using System.Threading;
    using System.Threading.Tasks;
    using Spectre.Console;
    using Spectre.Console.Cli;
    using Armada.Helm.Infrastructure;

    /// <summary>
    /// Restart the Admiral server: stop it if it is running, wait for it to exit, then start it again.
    /// Inherits <see cref="ServerStartCommand"/> for the start half so restart and start stay in lockstep;
    /// a change to how the server starts cannot apply to one and not the other.
    /// </summary>
    [Description("Restart the Admiral server")]
    public class ServerRestartCommand : ServerStartCommand
    {
        #region Public-Methods

        /// <inheritdoc />
        protected override async Task<int> ExecuteAsync(CommandContext context, ServerStartSettings settings, CancellationToken cancellationToken)
        {
            if (!await StopRunningServerAsync(CreateAdmiralShutdown(), cancellationToken).ConfigureAwait(false)) return 1;
            return await base.ExecuteAsync(context, settings, cancellationToken).ConfigureAwait(false);
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Stop a running Admiral and wait until it stops answering. Starting the replacement before the process
        /// has exited would run it against a held port and a locked executable, so a refused stop or a server that
        /// keeps answering ends the restart instead of starting a second instance.
        /// </summary>
        /// <param name="shutdown">Stop logic for the target Admiral.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True when nothing answers and the start may proceed.</returns>
        internal static async Task<bool> StopRunningServerAsync(AdmiralShutdown shutdown, CancellationToken cancellationToken)
        {
            AdmiralStopResult result = await shutdown.StopAsync(cancellationToken).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case AdmiralStopOutcomeEnum.NotRunning:
                    AnsiConsole.MarkupLine("[gold1]Admiral server was not running; starting a fresh instance.[/]");
                    return true;
                case AdmiralStopOutcomeEnum.Stopped:
                    AnsiConsole.MarkupLine("[green]" + Markup.Escape(result.Message) + "[/]");
                    return true;
                default:
                    AnsiConsole.MarkupLine("[red]" + Markup.Escape(result.Message) + "[/] Restart cancelled; the running server was left in place.");
                    return false;
            }
        }

        #endregion
    }
}
