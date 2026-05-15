namespace Armada.Helm.Commands
{
    using System.ComponentModel;
    using Spectre.Console;
    using Spectre.Console.Cli;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Helm.Rendering;

    [Description("List backlog items")]
    public sealed class BacklogListCommand : BaseCommand<BacklogListCommand.Settings>
    {
        public sealed class Settings : BaseSettings
        {
            [CommandOption("--search|-s")]
            public string? Search { get; set; }

            [CommandOption("--status")]
            public string? Status { get; set; }

            [CommandOption("--kind")]
            public string? Kind { get; set; }

            [CommandOption("--priority")]
            public string? Priority { get; set; }

            [CommandOption("--backlog-state")]
            public string? BacklogState { get; set; }

            [CommandOption("--effort")]
            public string? Effort { get; set; }

            [CommandOption("--owner")]
            public string? Owner { get; set; }

            [CommandOption("--target-version")]
            public string? TargetVersion { get; set; }

            [CommandOption("--fleet")]
            public string? FleetId { get; set; }

            [CommandOption("--vessel")]
            public string? VesselId { get; set; }
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
        {
            await EnsureServerAsync().ConfigureAwait(false);
            ObjectiveQuery query = new ObjectiveQuery
            {
                Search = String.IsNullOrWhiteSpace(settings.Search) ? null : settings.Search.Trim(),
                Owner = String.IsNullOrWhiteSpace(settings.Owner) ? null : settings.Owner.Trim(),
                TargetVersion = String.IsNullOrWhiteSpace(settings.TargetVersion) ? null : settings.TargetVersion.Trim(),
                FleetId = String.IsNullOrWhiteSpace(settings.FleetId) ? null : settings.FleetId.Trim(),
                VesselId = String.IsNullOrWhiteSpace(settings.VesselId) ? null : settings.VesselId.Trim(),
                Status = BacklogCommandSupport.ParseEnumOrNull<ObjectiveStatusEnum>(settings.Status, "--status"),
                Kind = BacklogCommandSupport.ParseEnumOrNull<ObjectiveKindEnum>(settings.Kind, "--kind"),
                Priority = BacklogCommandSupport.ParseEnumOrNull<ObjectivePriorityEnum>(settings.Priority, "--priority"),
                BacklogState = BacklogCommandSupport.ParseEnumOrNull<ObjectiveBacklogStateEnum>(settings.BacklogState, "--backlog-state"),
                Effort = BacklogCommandSupport.ParseEnumOrNull<ObjectiveEffortEnum>(settings.Effort, "--effort"),
                PageNumber = settings.Page ?? 1,
                PageSize = settings.PageSize ?? 100
            };

            EnumerationResult<Objective>? result = await GetApiClient().ListBacklogAsync(query, cancellationToken).ConfigureAwait(false);
            if (result == null || result.Objects == null || result.Objects.Count < 1)
            {
                AnsiConsole.MarkupLine("[gold1]No backlog items found.[/] Use [green]armada backlog create --title \"...\"[/] to create one.");
                return 0;
            }

            if (IsJsonMode(settings))
            {
                WriteJson(result);
                return 0;
            }

            TableRenderer.RenderPaginationHeader(result.PageNumber, result.TotalPages, result.TotalRecords, result.TotalMs);
            Table table = TableRenderer.CreateTable("Backlog", null);
            table.AddColumn("Id");
            table.AddColumn("Rank");
            table.AddColumn("Title");
            table.AddColumn("Priority");
            table.AddColumn("Backlog");
            table.AddColumn("Status");
            table.AddColumn("Owner");
            table.AddColumn("Updated");

            foreach (Objective objective in result.Objects)
            {
                table.AddRow(
                    Markup.Escape(objective.Id),
                    objective.Rank.ToString(),
                    Markup.Escape(objective.Title),
                    "[" + BacklogCommandSupport.PriorityColor(objective.Priority) + "]" + objective.Priority + "[/]",
                    "[" + BacklogCommandSupport.BacklogStateColor(objective.BacklogState) + "]" + objective.BacklogState + "[/]",
                    "[" + BacklogCommandSupport.StatusColor(objective.Status) + "]" + objective.Status + "[/]",
                    Markup.Escape(objective.Owner ?? "-"),
                    objective.LastUpdateUtc.ToString("yyyy-MM-dd HH:mm"));
            }

            AnsiConsole.Write(table);
            return 0;
        }
    }
}
