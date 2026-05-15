namespace Armada.Helm.Commands
{
    using System.ComponentModel;
    using Spectre.Console;
    using Spectre.Console.Cli;
    using Armada.Core.Models;

    [Description("Show backlog item details")]
    public sealed class BacklogShowCommand : BaseCommand<BacklogShowCommand.Settings>
    {
        public sealed class Settings : BaseSettings
        {
            [CommandArgument(0, "<backlog>")]
            public string Backlog { get; set; } = String.Empty;
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
        {
            await EnsureServerAsync().ConfigureAwait(false);
            Objective? objective = await BacklogCommandSupport.ResolveBacklogItemAsync(GetApiClient(), settings.Backlog, cancellationToken).ConfigureAwait(false);
            if (objective == null)
            {
                AnsiConsole.MarkupLine("[red]Backlog item not found:[/] " + Markup.Escape(settings.Backlog));
                return 1;
            }

            if (IsJsonMode(settings))
            {
                WriteJson(objective);
                return 0;
            }

            BacklogCommandSupport.RenderBacklogDetails(objective);
            return 0;
        }
    }
}
