namespace Armada.Helm.Commands
{
    using System;
    using System.Net.Http;
    using System.ComponentModel;
    using System.Threading;
    using System.Threading.Tasks;
    using Spectre.Console;
    using Spectre.Console.Cli;

    /// <summary>
    /// Restart the Admiral server: stop it if it is running, wait for it to exit, then start it again.
    /// Inherits <see cref="ServerStartCommand"/> for the start half so restart and start stay in lockstep;
    /// a change to how the server starts cannot apply to one and not the other.
    /// </summary>
    [Description("Restart the Admiral server")]
    public class ServerRestartCommand : ServerStartCommand
    {
        #region Private-Members

        private const int _StopPollAttempts = 15;
        private static readonly TimeSpan _StopPollInterval = TimeSpan.FromSeconds(1);

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        protected override async Task<int> ExecuteAsync(CommandContext context, ServerStartSettings settings, CancellationToken cancellationToken)
        {
            await StopRunningServerAsync(cancellationToken).ConfigureAwait(false);
            return await base.ExecuteAsync(context, settings, cancellationToken).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Ask a running Admiral to stop, then wait until it stops answering health checks. Returning
        /// before the process has actually exited would start the replacement against a held port and a
        /// locked executable, so the wait is part of the stop rather than an optimization.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        private async Task StopRunningServerAsync(CancellationToken cancellationToken)
        {
            try
            {
                using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    await client.PostAsync(GetBaseUrl() + "/api/v1/server/stop", null, cancellationToken).ConfigureAwait(false);
                    AnsiConsole.MarkupLine("[green]Stopping Admiral server...[/]");
                }
            }
            catch (HttpRequestException)
            {
                AnsiConsole.MarkupLine("[gold1]Admiral server was not running; starting a fresh instance.[/]");
                return;
            }

            for (int i = 0; i < _StopPollAttempts; i++)
            {
                await Task.Delay(_StopPollInterval, cancellationToken).ConfigureAwait(false);

                try
                {
                    using (HttpClient pollClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
                    {
                        await pollClient.GetAsync(GetBaseUrl() + "/api/v1/status/health", cancellationToken).ConfigureAwait(false);
                        // Still answering, so the process is alive. Keep waiting.
                    }
                }
                catch
                {
                    AnsiConsole.MarkupLine("[green]Admiral server stopped.[/]");
                    return;
                }
            }

            AnsiConsole.MarkupLine("[gold1]Server did not stop within the timeout; attempting to start anyway.[/]");
        }

        #endregion
    }
}
