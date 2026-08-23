using System.ComponentModel;
using Armada.Helm.Rendering;

namespace Armada.Helm.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Spectre.Console;
    using Spectre.Console.Cli;
    using Armada.Core.Models;

    /// <summary>
    /// Show the shared coordination board: recent notes, active claims, and who is present.
    /// </summary>
    [Description("Show the coordination board: recent notes, active claims, active sessions")]
    public class BoardCommand : BaseCommand<BoardSettings>
    {
        /// <inheritdoc />
        protected override async Task<int> ExecuteAsync(CommandContext context, BoardSettings settings, CancellationToken cancellationToken)
        {
            List<CoordinationMessage>? messages = await GetAsync<List<CoordinationMessage>>("/api/v1/coordination/rooms/fleet/messages?limit=" + Math.Clamp(settings.Limit, 1, 200)).ConfigureAwait(false);
            List<CoordinationClaim>? claims = await GetAsync<List<CoordinationClaim>>("/api/v1/coordination/claims").ConfigureAwait(false);
            List<CoordinationParticipant>? participants = await GetAsync<List<CoordinationParticipant>>("/api/v1/coordination/rooms/fleet/participants?activeWithinMinutes=15").ConfigureAwait(false);

            if (IsJsonMode(settings))
            {
                WriteJson(new { messages, claims, participants });
                return 0;
            }

            if (messages == null)
            {
                AnsiConsole.MarkupLine("[red]Unable to retrieve the board.[/] Is the Admiral running?");
                return 1;
            }

            if (participants != null && participants.Count > 0)
            {
                Table presence = TableRenderer.CreateTable($"Active Now ({participants.Count})", null);
                presence.AddColumn("Participant");
                presence.AddColumn("Last Seen");
                foreach (CoordinationParticipant p in participants)
                    presence.AddRow(Markup.Escape(p.DisplayName), p.LastSeenUtc.ToString("u"));
                AnsiConsole.Write(presence);
                AnsiConsole.WriteLine();
            }

            if (claims != null && claims.Count > 0)
            {
                Table claimTable = TableRenderer.CreateTable($"Reservations ({claims.Count})", null);
                claimTable.AddColumn("Holder");
                claimTable.AddColumn("Subject");
                claimTable.AddColumn("Expires");
                claimTable.AddColumn("Note");
                foreach (CoordinationClaim c in claims)
                    claimTable.AddRow(
                        Markup.Escape(c.DisplayName),
                        Markup.Escape(c.SubjectType.ToString().ToLowerInvariant() + " " + c.SubjectId),
                        c.ExpiresUtc.ToString("u"),
                        Markup.Escape(c.Note ?? ""));
                AnsiConsole.Write(claimTable);
                AnsiConsole.WriteLine();
            }

            Table notes = TableRenderer.CreateTable($"Recent Notes ({messages.Count})", null);
            notes.AddColumn("Author");
            notes.AddColumn("Note");
            notes.AddColumn("When");
            foreach (CoordinationMessage m in messages)
                notes.AddRow(m.AuthorName, m.Content, m.CreatedUtc.ToString("u"));
            AnsiConsole.Write(notes);
            return 0;
        }
    }
}
