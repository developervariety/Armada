namespace Armada.Helm.Commands
{
    using System.ComponentModel;
    using System.Threading;
    using Spectre.Console;
    using Spectre.Console.Cli;
    using Armada.Core.Client;
    using Armada.Core.Models;

    /// <summary>
    /// Stop all captains.
    /// </summary>
    [Description("Stop all captains")]
    public class CaptainStopAllCommand : BaseCommand<CaptainStopAllSettings>
    {
        #region Public-Methods

        /// <summary>
        /// Run the server's single stop-all operation and report its counts and every failure it names.
        /// </summary>
        /// <param name="client">API client for the Admiral.</param>
        /// <param name="console">Console to report to.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>0 when every captain and session stopped, 1 when any stop failed or the request failed.</returns>
        public static async Task<int> StopAllAsync(ArmadaApiClient client, IAnsiConsole console, CancellationToken token = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (console == null) throw new ArgumentNullException(nameof(console));

            CaptainStopAllResult? result;
            try
            {
                result = await client.StopAllCaptainsAsync(token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                console.MarkupLine("[red]Stop all failed:[/] " + Markup.Escape(ex.Message));
                return 1;
            }

            if (result == null)
            {
                console.MarkupLine("[red]Stop all failed:[/] the Admiral returned no result.");
                return 1;
            }

            console.MarkupLine($"Captains stopped: [bold]{result.CaptainsStopped}[/], failed: [bold]{result.CaptainsFailed}[/]");
            console.MarkupLine($"Planning sessions stopped: [bold]{result.PlanningSessionsStopped}[/], failed: [bold]{result.PlanningSessionsFailed}[/]");
            console.MarkupLine($"Refinement sessions stopped: [bold]{result.RefinementSessionsStopped}[/], failed: [bold]{result.RefinementSessionsFailed}[/]");
            foreach (CaptainStopFailure failure in result.Failures)
            {
                console.MarkupLine($"  [red]Failed:[/] {Markup.Escape(failure.Kind)} {Markup.Escape(failure.Id)} -- {Markup.Escape(failure.Message)}");
            }

            if (result.Failed > 0)
            {
                console.MarkupLine($"\n[red]{result.Failed} captain(s) or session(s) could not be stopped ({Markup.Escape(result.Status)}).[/]");
                return 1;
            }

            console.MarkupLine($"\n[green]Stopped {result.Stopped} captain(s) and session(s) ({Markup.Escape(result.Status)}).[/]");
            return 0;
        }

        #endregion

        #region Protected-Methods

        /// <inheritdoc />
        protected override async Task<int> ExecuteAsync(CommandContext context, CaptainStopAllSettings settings, CancellationToken cancellationToken)
        {
            if (!AnsiConsole.Confirm("[bold red]RECALL ALL CAPTAINS?[/] This will stop all active agents and planning and refinement sessions.", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[dodgerblue1]Cancelled. Fleet remains operational.[/]");
                return 0;
            }

            await EnsureServerAsync().ConfigureAwait(false);
            return await StopAllAsync(GetApiClient(), AnsiConsole.Console, cancellationToken).ConfigureAwait(false);
        }

        #endregion
    }
}
