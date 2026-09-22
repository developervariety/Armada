namespace Armada.Helm.Commands
{
    using System.ComponentModel;
    using Spectre.Console;
    using Spectre.Console.Cli;
    using Armada.Core;
    using Armada.Core.Settings;
    using Armada.Helm.Infrastructure;

    /// <summary>
    /// Destructively reset Armada back to zero: delete database, logs, docks, and bare repos.
    /// Preserves settings file.
    /// </summary>
    [Description("Destructively reset all Armada data (database, logs, docks, repos)")]
    public class ResetCommand : BaseCommand<ResetSettings>
    {
        #region Public-Methods

        /// <inheritdoc />
        protected override async Task<int> ExecuteAsync(CommandContext context, ResetSettings settings, CancellationToken cancellationToken)
        {
            ArmadaSettings armadaSettings = GetSettings();

            if (!settings.Force)
            {
                AnsiConsole.MarkupLine("[bold red]WARNING: This will permanently delete:[/]");
                AnsiConsole.MarkupLine($"  - Database:  [dim]{Markup.Escape(armadaSettings.DatabasePath)}[/]");
                AnsiConsole.MarkupLine($"  - Logs:      [dim]{Markup.Escape(armadaSettings.LogDirectory)}[/]");
                AnsiConsole.MarkupLine($"  - Docks:     [dim]{Markup.Escape(armadaSettings.DocksDirectory)}[/]");
                AnsiConsole.MarkupLine($"  - Bare repos:[dim] {Markup.Escape(armadaSettings.ReposDirectory)}[/]");
                AnsiConsole.MarkupLine("");
                AnsiConsole.MarkupLine("[dim]Settings file will be preserved.[/]");
                AnsiConsole.MarkupLine("");

                if (!AnsiConsole.Confirm("[bold red]Are you sure you want to reset?[/]", defaultValue: false))
                {
                    AnsiConsole.MarkupLine("[dim]Reset cancelled.[/]");
                    return 0;
                }
            }

            return await ResetDataAsync(armadaSettings, CreateAdmiralShutdown(), cancellationToken).ConfigureAwait(false);
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Stop the Admiral, prove it has exited, then delete the database, logs, docks and bare repositories.
        /// Nothing is deleted while the Admiral still answers: a refused stop request or a server that keeps
        /// answering ends the reset with a non-zero exit code and every file in place.
        /// </summary>
        /// <param name="armadaSettings">Settings naming the data to delete.</param>
        /// <param name="shutdown">Stop logic for the Admiral that owns the data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Process exit code.</returns>
        internal static async Task<int> ResetDataAsync(ArmadaSettings armadaSettings, AdmiralShutdown shutdown, CancellationToken cancellationToken)
        {
            AdmiralStopResult stop = await shutdown.StopAsync(cancellationToken).ConfigureAwait(false);
            if (!stop.IsDown)
            {
                AnsiConsole.MarkupLine("[bold red]Reset refused:[/] " + Markup.Escape(stop.Message));
                AnsiConsole.MarkupLine("[dim]Nothing was deleted. Stop the Admiral server, then run reset again.[/]");
                return 1;
            }

            if (stop.Outcome == AdmiralStopOutcomeEnum.Stopped)
                AnsiConsole.MarkupLine("[dim]" + Markup.Escape(stop.Message) + "[/]");

            int errors = 0;

            // Delete database
            if (File.Exists(armadaSettings.DatabasePath))
            {
                try
                {
                    File.Delete(armadaSettings.DatabasePath);
                    AnsiConsole.MarkupLine("[green]Deleted[/] database");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Failed[/] to delete database: {Markup.Escape(ex.Message)}");
                    errors++;
                }
            }

            // Delete logs directory
            if (Directory.Exists(armadaSettings.LogDirectory))
            {
                try
                {
                    Directory.Delete(armadaSettings.LogDirectory, recursive: true);
                    AnsiConsole.MarkupLine("[green]Deleted[/] logs");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Failed[/] to delete logs: {Markup.Escape(ex.Message)}");
                    errors++;
                }
            }

            // Delete docks directory (worktrees)
            if (Directory.Exists(armadaSettings.DocksDirectory))
            {
                try
                {
                    ClearReadOnlyAttributes(armadaSettings.DocksDirectory);
                    Directory.Delete(armadaSettings.DocksDirectory, recursive: true);
                    AnsiConsole.MarkupLine("[green]Deleted[/] docks");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Failed[/] to delete docks: {Markup.Escape(ex.Message)}");
                    errors++;
                }
            }

            // Delete bare repos directory
            if (Directory.Exists(armadaSettings.ReposDirectory))
            {
                try
                {
                    ClearReadOnlyAttributes(armadaSettings.ReposDirectory);
                    Directory.Delete(armadaSettings.ReposDirectory, recursive: true);
                    AnsiConsole.MarkupLine("[green]Deleted[/] bare repos");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Failed[/] to delete bare repos: {Markup.Escape(ex.Message)}");
                    errors++;
                }
            }

            // Re-create directories
            armadaSettings.InitializeDirectories();

            AnsiConsole.MarkupLine("");
            if (errors == 0)
            {
                AnsiConsole.MarkupLine("[bold green]Reset complete.[/] Armada is back to zero.");
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold yellow]Reset completed with {errors} error(s).[/] Some files may still exist.");
                AnsiConsole.MarkupLine("[dim]Close any process that holds Armada files open and try again.[/]");
            }

            return errors > 0 ? 1 : 0;
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Recursively clear read-only attributes on all files in a directory.
        /// Git pack files and index files are often marked read-only on Windows.
        /// </summary>
        private static void ClearReadOnlyAttributes(string directory)
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    FileAttributes attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                    }
                }
                catch { }
            }
        }

        #endregion
    }
}
