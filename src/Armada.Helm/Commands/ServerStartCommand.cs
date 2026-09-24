namespace Armada.Helm.Commands
{
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Net.Http;
    using System.Runtime.InteropServices;
    using System.Runtime.Versioning;
    using System.Threading;
    using Spectre.Console;
    using Spectre.Console.Cli;
    using Armada.Core;
    using Armada.Core.Services;

    /// <summary>
    /// Start the Admiral server.
    /// </summary>
    [Description("Start the Admiral server")]
    public class ServerStartCommand : BaseCommand<ServerStartSettings>
    {
        private const int _BuildStepOutputLimitBytes = 256 * 1024;
        private static readonly TimeSpan _BuildStepTimeout = TimeSpan.FromMinutes(15);

        /// <inheritdoc />
        protected override async Task<int> ExecuteAsync(CommandContext context, ServerStartSettings settings, CancellationToken cancellationToken)
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
            string? serverExe = await FindServerExeAsync(cancellationToken).ConfigureAwait(false);
            if (serverExe == null)
            {
                AnsiConsole.MarkupLine("[red]Admiral server executable not found.[/]");
                AnsiConsole.MarkupLine("[dim]Looked for Armada.Server next to the CLI and in common source locations.[/]");
                return 1;
            }

            // Build and deploy the React dashboard if source is available
            await BuildAndDeployDashboardAsync(serverExe, cancellationToken).ConfigureAwait(false);

            // Launch the server executable
            ProcessStartInfo startInfo;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = serverExe,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Minimized
                };
            }
            else
            {
                startInfo = BuildUnixServerStartInfo(serverExe);
            }

            // Disposing the handle releases it without stopping the server, which outlives this command.
            using (Process process = new Process { StartInfo = startInfo })
            {
                bool started = process.Start();
                if (!started)
                {
                    AnsiConsole.MarkupLine("[red]Failed to start server process.[/]");
                    return 1;
                }

                string baseUrl = GetBaseUrl();
                AnsiConsole.MarkupLine($"[green]Admiral server starting...[/] (PID: {process.Id})");
                AnsiConsole.MarkupLine($"[dim]  REST API:   {baseUrl}[/]");
                AnsiConsole.MarkupLine($"[dim]  WebSocket:  ws://localhost:{Constants.DefaultAdmiralPort}/ws[/]");
                AnsiConsole.MarkupLine($"[dim]  Dashboard:  {baseUrl}/dashboard[/]");
                AnsiConsole.MarkupLine($"[dim]  MCP:        http://localhost:{Constants.DefaultMcpPort}[/]");

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
        }

        /// <summary>
        /// Start information for the Admiral on Unix. The Admiral writes its own log file and outlives this CLI, so
        /// nothing here reads its console: its standard streams go to /dev/null rather than to pipes, which would
        /// stop the server at the first full pipe while it starts and be left without a reader once the CLI exits.
        /// The shell replaces itself with the server, so the started process is the server itself.
        /// </summary>
        /// <param name="serverExe">Path of the Admiral executable.</param>
        /// <returns>The start information.</returns>
        internal static ProcessStartInfo BuildUnixServerStartInfo(string serverExe)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("exec \"$0\" </dev/null >/dev/null 2>&1");
            startInfo.ArgumentList.Add(serverExe);
            return startInfo;
        }

        /// <summary>
        /// Find the Admiral server executable.
        /// 1. Next to the CLI executable (installed/published scenario)
        /// 2. Dev: build from source project and return built exe path
        /// </summary>
        private async Task<string?> FindServerExeAsync(CancellationToken token)
        {
            // Platform-aware executable name
            string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "Armada.Server.exe"
                : "Armada.Server";

            // 1. Installed: Armada.Server[.exe] next to the CLI
            string? cliDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(cliDir))
            {
                string installed = Path.Combine(cliDir, exeName);
                if (File.Exists(installed)) return installed;
            }

            // 2. Dev: find and build the source project
            string? projectDir = FindServerProject();
            if (projectDir == null) return null;

            string targetFramework = GetCliTargetFramework();
            string builtExe = Path.GetFullPath(Path.Combine(projectDir, "bin", "Debug", targetFramework, exeName));

            // If the exe exists and is locked by a recently-stopped server, wait for it to be released
            if (File.Exists(builtExe))
            {
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        using FileStream probe = new FileStream(builtExe, FileMode.Open, FileAccess.Write, FileShare.None);
                        break; // File is unlocked
                    }
                    catch (IOException)
                    {
                        if (attempt == 0)
                            AnsiConsole.MarkupLine("[dim]Waiting for previous server process to release...[/]");
                        await Task.Delay(1000, token).ConfigureAwait(false);
                    }
                }
            }

            AnsiConsole.MarkupLine($"[dim]Building server from {Markup.Escape(projectDir)}...[/]");

            // Build the server project. Retry once without -q if the first attempt fails
            // (incremental build can fail with "Question build" on first run after code changes).
            for (int buildAttempt = 0; buildAttempt < 2; buildAttempt++)
            {
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
                buildInfo.ArgumentList.Add(targetFramework);
                buildInfo.ArgumentList.Add("-p:TargetFramework=" + targetFramework);
                buildInfo.ArgumentList.Add("-p:TargetFrameworks=" + targetFramework);
                if (buildAttempt == 0)
                    buildInfo.ArgumentList.Add("-q");

                BoundedProcessResult build = await RunBuildStepAsync(buildInfo, token).ConfigureAwait(false);

                if (build.ExitCode == 0)
                    break;

                if (buildAttempt == 0)
                {
                    AnsiConsole.MarkupLine("[dim]Retrying build...[/]");
                    continue;
                }

                AnsiConsole.MarkupLine("[red]Server build failed.[/]");
                WriteStepFailure(build);
                return null;
            }

            return File.Exists(builtExe) ? builtExe : null;
        }

        /// <summary>
        /// Find the Armada.Server source project directory.
        /// Searches relative to CWD, walks up the directory tree, and checks
        /// next to the CLI assembly to handle global-tool and dev scenarios.
        /// </summary>
        private string? FindServerProject()
        {
            // 1. Relative candidates from CWD (covers common dev layouts)
            string[] relativeCandidates = new[]
            {
                "src/Armada.Server",
                "Armada.Server",
                Path.Combine("..", "Armada.Server"),
                Path.Combine("..", "src", "Armada.Server")
            };

            foreach (string candidate in relativeCandidates)
            {
                string csproj = Path.Combine(candidate, "Armada.Server.csproj");
                if (File.Exists(csproj)) return Path.GetFullPath(candidate);
            }

            // 2. Walk up from CWD looking for Armada.Server.csproj
            try
            {
                DirectoryInfo? dir = new DirectoryInfo(Directory.GetCurrentDirectory());
                while (dir != null)
                {
                    string serverDir = Path.Combine(dir.FullName, "src", "Armada.Server");
                    if (File.Exists(Path.Combine(serverDir, "Armada.Server.csproj")))
                        return serverDir;

                    serverDir = Path.Combine(dir.FullName, "Armada.Server");
                    if (File.Exists(Path.Combine(serverDir, "Armada.Server.csproj")))
                        return serverDir;

                    dir = dir.Parent;
                }
            }
            catch { }

            // 3. Check relative to the CLI assembly location (global tool scenario)
            try
            {
                string? asmDir = Path.GetDirectoryName(typeof(ServerStartCommand).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir))
                {
                    DirectoryInfo? dir = new DirectoryInfo(asmDir);
                    while (dir != null)
                    {
                        string serverDir = Path.Combine(dir.FullName, "src", "Armada.Server");
                        if (File.Exists(Path.Combine(serverDir, "Armada.Server.csproj")))
                            return serverDir;
                        dir = dir.Parent;
                    }
                }
            }
            catch { }

            return null;
        }

        private static string GetCliTargetFramework()
        {
            try
            {
                string? frameworkName = typeof(ServerStartCommand).Assembly
                    .GetCustomAttributes(typeof(TargetFrameworkAttribute), inherit: false)
                    .OfType<TargetFrameworkAttribute>()
                    .FirstOrDefault()?
                    .FrameworkName;

                if (!String.IsNullOrWhiteSpace(frameworkName))
                {
                    FrameworkName parsed = new FrameworkName(frameworkName);
                    if (String.Equals(parsed.Identifier, ".NETCoreApp", StringComparison.OrdinalIgnoreCase))
                        return $"net{parsed.Version.Major}.{parsed.Version.Minor}";
                }
            }
            catch
            {
            }

            return "net10.0";
        }

        /// <summary>
        /// Build the React dashboard and deploy it to the data directory.
        /// </summary>
        private async Task BuildAndDeployDashboardAsync(string serverExe, CancellationToken token)
        {
            // Find the dashboard source relative to the server project
            string? dashboardDir = FindDashboardProject(serverExe);
            if (dashboardDir == null) return;

            string distDir = Path.Combine(dashboardDir, "dist");
            string targetDir = Path.Combine(Constants.DefaultDataDirectory, "dashboard");
            bool hasExistingDist = Directory.Exists(distDir)
                && File.Exists(Path.Combine(distDir, "index.html"));
            bool hasExistingDeployedDashboard = Directory.Exists(targetDir)
                && File.Exists(Path.Combine(targetDir, "index.html"));

            // Check if dashboard needs rebuilding
            bool needsBuild = !hasExistingDist;

            if (!needsBuild)
            {
                // Check if source is newer than dist
                try
                {
                    DateTime srcMtime = Directory.GetFiles(Path.Combine(dashboardDir, "src"), "*", SearchOption.AllDirectories)
                        .Select(f => File.GetLastWriteTimeUtc(f))
                        .DefaultIfEmpty(DateTime.MinValue)
                        .Max();
                    DateTime distMtime = File.GetLastWriteTimeUtc(Path.Combine(distDir, "index.html"));
                    if (srcMtime > distMtime) needsBuild = true;
                }
                catch
                {
                    needsBuild = true;
                }
            }

            if (needsBuild)
            {
                AnsiConsole.MarkupLine("[dim]Building dashboard...[/]");

                string toolExtension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".cmd" : String.Empty;
                string localBinDir = Path.Combine(dashboardDir, "node_modules", ".bin");
                string tscPath = Path.Combine(localBinDir, "tsc" + toolExtension);
                string vitePath = Path.Combine(localBinDir, "vite" + toolExtension);

                if (!File.Exists(tscPath) || !File.Exists(vitePath))
                {
                    if (hasExistingDist)
                        AnsiConsole.MarkupLine("[gold1]Dashboard build skipped because local dashboard tools are unavailable. Continuing with the existing React dashboard bundle.[/]");
                    else if (hasExistingDeployedDashboard)
                        AnsiConsole.MarkupLine("[gold1]Dashboard build skipped because local dashboard tools are unavailable. Keeping the existing deployed React dashboard.[/]");
                    else
                        AnsiConsole.MarkupLine("[gold1]Dashboard build skipped because local dashboard tools are unavailable. React dashboard unavailable; /dashboard serves nothing until a dashboard build exists.[/]");
                    return;
                }

                ProcessStartInfo tscInfo = new ProcessStartInfo
                {
                    FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd" : tscPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = dashboardDir
                };
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    tscInfo.ArgumentList.Add("/c");
                    tscInfo.ArgumentList.Add(tscPath);
                }

                BoundedProcessResult tsc = await RunBuildStepAsync(tscInfo, token).ConfigureAwait(false);

                if (tsc.ExitCode != 0)
                {
                    if (hasExistingDist)
                        AnsiConsole.MarkupLine("[gold1]Dashboard build failed. Continuing with the existing React dashboard bundle.[/]");
                    else if (hasExistingDeployedDashboard)
                        AnsiConsole.MarkupLine("[gold1]Dashboard build failed. Keeping the existing deployed React dashboard.[/]");
                    else
                        AnsiConsole.MarkupLine("[gold1]Dashboard build failed. React dashboard unavailable; /dashboard serves nothing until a dashboard build exists.[/]");

                    WriteStepFailure(tsc);

                    return;
                }

                ProcessStartInfo viteInfo = new ProcessStartInfo
                {
                    FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd" : vitePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = dashboardDir
                };
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    viteInfo.ArgumentList.Add("/c");
                    viteInfo.ArgumentList.Add(vitePath);
                }
                viteInfo.ArgumentList.Add("build");

                BoundedProcessResult vite = await RunBuildStepAsync(viteInfo, token).ConfigureAwait(false);

                if (vite.ExitCode != 0)
                {
                    if (hasExistingDist)
                        AnsiConsole.MarkupLine("[gold1]Dashboard build failed. Continuing with the existing React dashboard bundle.[/]");
                    else if (hasExistingDeployedDashboard)
                        AnsiConsole.MarkupLine("[gold1]Dashboard build failed. Keeping the existing deployed React dashboard.[/]");
                    else
                        AnsiConsole.MarkupLine("[gold1]Dashboard build failed. React dashboard unavailable; /dashboard serves nothing until a dashboard build exists.[/]");

                    WriteStepFailure(vite);

                    return;
                }
            }

            if (!Directory.Exists(distDir) || !File.Exists(Path.Combine(distDir, "index.html")))
            {
                return;
            }

            // Deploy: copy dist/ to {dataDir}/dashboard/
            try
            {
                string stagingDir = targetDir + ".staging";
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, recursive: true);

                CopyDirectory(distDir, stagingDir);
                if (Directory.Exists(targetDir))
                    Directory.Delete(targetDir, recursive: true);

                Directory.Move(stagingDir, targetDir);
                AnsiConsole.MarkupLine("[dim]Dashboard deployed to " + Markup.Escape(targetDir) + "[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[gold1]Dashboard deploy failed. Keeping any previously deployed React dashboard if present: {Markup.Escape(ex.Message)}[/]");
            }
        }

        /// <summary>
        /// Find the Armada.Dashboard source directory.
        /// </summary>
        private string? FindDashboardProject(string serverExe)
        {
            // From the server project dir: ../Armada.Dashboard
            string? serverProjectDir = FindServerProject();
            if (serverProjectDir != null)
            {
                string candidate = Path.GetFullPath(Path.Combine(serverProjectDir, "..", "Armada.Dashboard"));
                if (File.Exists(Path.Combine(candidate, "package.json")))
                    return candidate;
            }

            // From the server exe: walk up looking for src/Armada.Dashboard
            string? exeDir = Path.GetDirectoryName(serverExe);
            if (exeDir != null)
            {
                DirectoryInfo? dir = new DirectoryInfo(exeDir);
                while (dir != null)
                {
                    string candidate = Path.Combine(dir.FullName, "src", "Armada.Dashboard");
                    if (File.Exists(Path.Combine(candidate, "package.json")))
                        return candidate;
                    dir = dir.Parent;
                }
            }

            return null;
        }

        /// <summary>
        /// Run one build step (the server build, tsc, vite) through the bounded runner: both output streams are read
        /// at once, so a step that writes more than a pipe buffer to either stream cannot block, each stream keeps its
        /// beginning and its end within a budget, and a step that never finishes is killed with its tree.
        /// </summary>
        internal static Task<BoundedProcessResult> RunBuildStepAsync(ProcessStartInfo startInfo, CancellationToken token)
        {
            BoundedProcessRequest request = new BoundedProcessRequest(startInfo, _BuildStepTimeout)
            {
                OutputLimitBytes = _BuildStepOutputLimitBytes
            };
            return BoundedProcessRunner.RunAsync(request, token);
        }

        private static void WriteStepFailure(BoundedProcessResult result)
        {
            string anomalies = BoundedProcessRunner.DescribeAnomalies(result);
            if (!string.IsNullOrEmpty(anomalies))
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(anomalies)}[/]");
            if (!string.IsNullOrEmpty(result.StandardError))
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(result.StandardError.Trim())}[/]");
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            foreach (string subDir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
                CopyDirectory(subDir, destSubDir);
            }
        }
    }
}
