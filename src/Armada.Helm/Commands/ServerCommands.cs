namespace Armada.Helm.Commands
{
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Net.Http;
    using System.Threading;
    using Spectre.Console;
    using Spectre.Console.Cli;
    using Armada.Core;

    #region Settings

    /// <summary>
    /// Settings for server start command.
    /// </summary>
    public class ServerStartSettings : BaseSettings
    {
    }

    /// <summary>
    /// Settings for server status command.
    /// </summary>
    public class ServerStatusSettings : BaseSettings
    {
    }

    /// <summary>
    /// Settings for server stop command.
    /// </summary>
    public class ServerStopSettings : BaseSettings
    {
    }

    #endregion

    #region Commands

    /// <summary>
    /// Start the Admiral server.
    /// </summary>
    [Description("Start the Admiral server")]
    public class ServerStartCommand : BaseCommand<ServerStartSettings>
    {
        /// <inheritdoc />
        public override async Task<int> ExecuteAsync(CommandContext context, ServerStartSettings settings, CancellationToken cancellationToken)
        {
            Program.WriteBanner();
            AnsiConsole.MarkupLine("[bold dodgerblue1]Admiral Server[/]");
            AnsiConsole.WriteLine();

            // Check if already running (bypass EnsureServerAsync — just probe directly)
            try
            {
                using HttpClient probeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                HttpResponseMessage probeResp = await probeClient.GetAsync(GetBaseUrl() + "/api/v1/status/health", cancellationToken).ConfigureAwait(false);
                if (probeResp.IsSuccessStatusCode)
                {
                    AnsiConsole.MarkupLine("[gold1]Admiral server is already running![/]");
                    return 0;
                }
            }
            catch
            {
                // Not running — proceed to start
            }

            // Find the server executable
            string? serverExe = FindServerExe();
            if (serverExe == null)
            {
                AnsiConsole.MarkupLine("[red]Admiral server executable not found.[/]");
                AnsiConsole.MarkupLine("[dim]Looked for Armada.Server.exe next to the CLI and in common source locations.[/]");
                return 1;
            }

            // Launch the server executable in its own window
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized
            };

            Process process = new Process { StartInfo = startInfo };
            bool started = process.Start();
            if (!started)
            {
                AnsiConsole.MarkupLine("[red]Failed to start server process.[/]");
                return 1;
            }

            string baseUrl = GetBaseUrl();
            AnsiConsole.MarkupLine($"[green]Admiral server starting...[/] (PID: {process.Id})");
            AnsiConsole.MarkupLine($"[dim]  REST API:   {baseUrl}[/]");
            AnsiConsole.MarkupLine($"[dim]  Dashboard:  {baseUrl}/dashboard[/]");
            AnsiConsole.MarkupLine($"[dim]  MCP:        http://localhost:{Constants.DefaultMcpPort}[/]");
            AnsiConsole.MarkupLine($"[dim]  WebSocket:  ws://localhost:{Constants.DefaultWebSocketPort}[/]");

            // Poll until the server is ready
            bool ready = false;
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

                if (process.HasExited)
                {
                    AnsiConsole.MarkupLine($"[red]Server process exited with code {process.ExitCode}.[/]");
                    break;
                }

                try
                {
                    using HttpClient pollClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    HttpResponseMessage pollResp = await pollClient.GetAsync(baseUrl + "/api/v1/status/health", cancellationToken).ConfigureAwait(false);
                    if (pollResp.IsSuccessStatusCode)
                    {
                        ready = true;
                        break;
                    }
                }
                catch { }
            }

            if (ready)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[green]Admiral server is running![/]");
            }
            else if (!process.HasExited)
                AnsiConsole.MarkupLine("[gold1]Server is still starting. Check [green]armada server status[/] in a few seconds.[/]");

            return 0;
        }

        /// <summary>
        /// Find the Admiral server executable.
        /// 1. Next to the CLI executable (installed/published scenario)
        /// 2. Dev: build from source project and return built exe path
        /// </summary>
        private string? FindServerExe()
        {
            // 1. Installed: Armada.Server.exe next to the CLI
            string? cliDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(cliDir))
            {
                string installed = Path.Combine(cliDir, "Armada.Server.exe");
                if (File.Exists(installed)) return installed;
            }

            // 2. Dev: find and build the source project
            string? projectDir = FindServerProject();
            if (projectDir == null) return null;

            AnsiConsole.MarkupLine("[dim]Building server...[/]");

            ProcessStartInfo buildInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            buildInfo.ArgumentList.Add("build");
            buildInfo.ArgumentList.Add(projectDir);
            buildInfo.ArgumentList.Add("--framework");
            buildInfo.ArgumentList.Add("net10.0");
            buildInfo.ArgumentList.Add("-q");

            Process buildProcess = new Process { StartInfo = buildInfo };
            buildProcess.Start();
            buildProcess.StandardOutput.ReadToEnd();
            string buildStderr = buildProcess.StandardError.ReadToEnd();
            buildProcess.WaitForExit();

            if (buildProcess.ExitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]Server build failed.[/]");
                if (!string.IsNullOrEmpty(buildStderr))
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(buildStderr.Trim())}[/]");
                return null;
            }

            string builtExe = Path.GetFullPath(Path.Combine(projectDir, "bin", "Debug", "net10.0", "Armada.Server.exe"));
            return File.Exists(builtExe) ? builtExe : null;
        }

        /// <summary>
        /// Find the Armada.Server source project directory relative to CWD.
        /// </summary>
        private string? FindServerProject()
        {
            string[] candidates = new[]
            {
                "src/Armada.Server",
                "Armada.Server",
                Path.Combine("..", "Armada.Server"),
                Path.Combine("..", "src", "Armada.Server")
            };

            foreach (string candidate in candidates)
            {
                string csproj = Path.Combine(candidate, "Armada.Server.csproj");
                if (File.Exists(csproj)) return candidate;
            }

            return null;
        }
    }

    /// <summary>
    /// Check Admiral server health.
    /// </summary>
    [Description("Check Admiral server status")]
    public class ServerStatusCommand : BaseCommand<ServerStatusSettings>
    {
        /// <inheritdoc />
        public override async Task<int> ExecuteAsync(CommandContext context, ServerStatusSettings settings, CancellationToken cancellationToken)
        {
            try
            {
                using HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                HttpResponseMessage resp = await client.GetAsync(GetBaseUrl() + "/api/v1/status/health", cancellationToken).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    AnsiConsole.MarkupLine("[green]Admiral server is running![/]");
                    AnsiConsole.MarkupLine("[dodgerblue1]Health:[/] healthy");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]Admiral server returned unhealthy status.[/]");
                    return 1;
                }
            }
            catch (HttpRequestException)
            {
                AnsiConsole.MarkupLine("[red]Admiral server is not reachable.[/]");
                AnsiConsole.MarkupLine($"[dim]  Tried: {GetBaseUrl()}[/]");
                return 1;
            }

            return 0;
        }
    }

    /// <summary>
    /// Stop the Admiral server.
    /// </summary>
    [Description("Stop the Admiral server")]
    public class ServerStopCommand : BaseCommand<ServerStopSettings>
    {
        /// <inheritdoc />
        public override async Task<int> ExecuteAsync(CommandContext context, ServerStopSettings settings, CancellationToken cancellationToken)
        {
            try
            {
                using HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                await client.PostAsync(GetBaseUrl() + "/api/v1/server/stop", null, cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine("[green]Admiral server is shutting down.[/]");
            }
            catch (HttpRequestException)
            {
                AnsiConsole.MarkupLine("[gold1]Admiral server is not reachable (may already be stopped).[/]");
                return 1;
            }

            return 0;
        }
    }

    #endregion
}
