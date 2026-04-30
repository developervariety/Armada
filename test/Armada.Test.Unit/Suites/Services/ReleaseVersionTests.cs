namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using System.IO;
    using System.Text.RegularExpressions;
    using Armada.Core;
    using Armada.Test.Common;

    public class ReleaseVersionTests : TestSuite
    {
        private static readonly string[] _StaleReleaseVersions = { "0.6.0", "0.5.0", "0.4.0", "0.3.0" };

        public override string Name => "Release Version";

        protected override async Task RunTestsAsync()
        {
            await RunTest("ProductVersion And Shared Build Props Match V070", () =>
            {
                string propsContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Directory.Build.props"));
                MatchCollection versionMatches = Regex.Matches(propsContents, @"<Version>\s*([^<]+)\s*</Version>");

                AssertTrue(versionMatches.Count == 1, "Directory.Build.props should contain exactly one Version element");
                Match versionMatch = versionMatches[0];
                AssertEqual("0.7.0", Constants.ProductVersion);
                AssertEqual(Constants.ProductVersion, versionMatch.Groups[1].Value.Trim());
            });

            await RunTest("Helm Program Uses ProductVersion Constant", () =>
            {
                string programContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Armada.Helm", "Program.cs"));

                AssertContains("\"v\" + Constants.ProductVersion", programContents, "Helm banner/help version should come from Constants.ProductVersion");
                AssertContains("AnsiConsole.MarkupLine(\"[dim]Multi-Agent Orchestration System  \" + _VersionLabel + \"[/]\");", programContents, "Helm subtitle should render the shared version label");
                AssertContains("config.SetApplicationVersion(Constants.ProductVersion);", programContents, "Helm CLI version should come from Constants.ProductVersion");
                AssertFalse(programContents.Contains("0.3.0"), "Helm entry point should not contain the stale 0.3.0 literal");
                AssertFalse(programContents.Contains("\"0.7.0\""), "Helm entry point should not contain a hard-coded release version literal");
                AssertFalse(programContents.Contains("\"v0.7.0\""), "Helm entry point should compose the prefixed version instead of hard-coding it");
                AssertFalse(programContents.Contains("SetApplicationVersion(\"0.7.0\")"), "Helm CLI version should not be hard-coded");
            });

            await RunTest("Source MCP Helpers Use Net10 Framework", () =>
            {
                string mcpConfigHelperContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Armada.Helm", "Commands", "McpConfigHelper.cs"));
                string installMcpBatContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "windows", "install-mcp.bat"));
                string installMcpShContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "common", "install-mcp.sh"));
                string removeMcpBatContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "windows", "remove-mcp.bat"));
                string removeMcpShContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "common", "remove-mcp.sh"));
                string resolveFrameworkBatContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "windows", "resolve-framework.bat"));

                AssertContains("private const string SourceMcpFramework = \"net10.0\";", mcpConfigHelperContents, "McpConfigHelper should pin source MCP installs to net10.0");
                AssertFalse(mcpConfigHelperContents.Contains("\"net8.0\""), "McpConfigHelper should not pin source MCP installs to net8.0");
                AssertContains("-f net10.0 -- mcp install --yes", installMcpShContents, "install-mcp.sh should use net10.0");
                AssertContains("-f net10.0 -- mcp remove --yes", removeMcpShContents, "remove-mcp.sh should use net10.0");
                AssertContains("set \"FRAMEWORK=net10.0\"", resolveFrameworkBatContents, "Windows framework resolver should default to net10.0");
                AssertContains("call \"%SCRIPT_DIR%\\resolve-framework.bat\" %*", installMcpBatContents, "install-mcp.bat should resolve the Windows framework override");
                AssertContains("-f %ARMADA_TARGET_FRAMEWORK% -- mcp install --yes", installMcpBatContents, "install-mcp.bat should honor the resolved framework override");
                AssertContains("call \"%SCRIPT_DIR%\\resolve-framework.bat\" %*", removeMcpBatContents, "remove-mcp.bat should resolve the Windows framework override");
                AssertContains("-f %ARMADA_TARGET_FRAMEWORK% -- mcp remove --yes", removeMcpBatContents, "remove-mcp.bat should honor the resolved framework override");
            });

            await RunTest("Windows Install Scripts Allow Explicit Framework Overrides", () =>
            {
                string installBatContents = ReadRepositoryFile("scripts", "windows", "install.bat");
                string reinstallBatContents = ReadRepositoryFile("scripts", "windows", "reinstall.bat");
                string removeBatContents = ReadRepositoryFile("scripts", "windows", "remove.bat");
                string updateBatContents = ReadRepositoryFile("scripts", "windows", "update.bat");
                string publishServerBatContents = ReadRepositoryFile("scripts", "windows", "publish-server.bat");
                string installWindowsTaskBatContents = ReadRepositoryFile("scripts", "windows", "install-windows-task.bat");
                string updateWindowsTaskBatContents = ReadRepositoryFile("scripts", "windows", "update-windows-task.bat");
                string removeWindowsTaskBatContents = ReadRepositoryFile("scripts", "windows", "remove-windows-task.bat");
                string serverStartCommandContents = ReadRepositoryFile("src", "Armada.Helm", "Commands", "ServerStartCommand.cs");

                AssertContains("call \"%SCRIPT_DIR%\\resolve-framework.bat\" %*", installBatContents, "install.bat should resolve framework overrides");
                AssertContains("dotnet build \"%REPO_ROOT%\\src\\Armada.sln\" -f %ARMADA_TARGET_FRAMEWORK%", installBatContents, "install.bat should build with the resolved framework");
                AssertContains("dotnet pack \"%REPO_ROOT%\\src\\Armada.Helm\\Armada.Helm.csproj\" -p:TargetFrameworks=%ARMADA_TARGET_FRAMEWORK%", installBatContents, "install.bat should pack with the resolved framework");
                AssertContains("call \"%SCRIPT_DIR%\\resolve-framework.bat\" %*", reinstallBatContents, "reinstall.bat should resolve framework overrides");
                AssertContains("call \"%SCRIPT_DIR%\\install.bat\" --framework %ARMADA_TARGET_FRAMEWORK%", reinstallBatContents, "reinstall.bat should forward the resolved framework explicitly");
                AssertContains("call \"%SCRIPT_DIR%\\resolve-framework.bat\" %*", removeBatContents, "remove.bat should accept framework overrides for parity");
                AssertContains("set \"HELM_DLL=%REPO_ROOT%\\src\\Armada.Helm\\bin\\Debug\\%ARMADA_TARGET_FRAMEWORK%\\Armada.Helm.dll\"", updateBatContents, "update.bat should probe the resolved Helm build output");
                AssertContains("call \"%SCRIPT_DIR%\\reinstall.bat\" --framework %ARMADA_TARGET_FRAMEWORK%", updateBatContents, "update.bat should forward the resolved framework to reinstall explicitly");
                AssertContains("dotnet run --project \"%REPO_ROOT%\\src\\Armada.Helm\" -f %ARMADA_TARGET_FRAMEWORK% -- %*", updateBatContents, "update.bat should fall back to the resolved framework");
                AssertContains("dotnet publish \"%REPO_ROOT%\\src\\Armada.Server\" -c Release -f %ARMADA_TARGET_FRAMEWORK%", publishServerBatContents, "publish-server.bat should publish with the resolved framework");
                AssertContains("call \"%SCRIPT_DIR%\\publish-server.bat\" --framework %ARMADA_TARGET_FRAMEWORK%", installWindowsTaskBatContents, "install-windows-task.bat should forward the resolved framework to publish-server explicitly");
                AssertContains("call \"%SCRIPT_DIR%\\install-windows-task.bat\" --framework %ARMADA_TARGET_FRAMEWORK%", updateWindowsTaskBatContents, "update-windows-task.bat should forward the resolved framework explicitly");
                AssertContains("call \"%SCRIPT_DIR%\\resolve-framework.bat\" %*", removeWindowsTaskBatContents, "remove-windows-task.bat should accept framework overrides for parity");
                AssertContains("string targetFramework = GetCliTargetFramework();", serverStartCommandContents, "ServerStartCommand should derive the build framework from the running Helm target");
                AssertContains("buildInfo.ArgumentList.Add(targetFramework);", serverStartCommandContents, "ServerStartCommand should build Armada.Server with the derived framework");
                AssertContains("return $\"net{parsed.Version.Major}.{parsed.Version.Minor}\";", serverStartCommandContents, "ServerStartCommand should map the runtime TFM to a dotnet framework moniker");
            });

            await RunTest("Windows Framework Resolver Accepts Positional And Named Overrides", () =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    return;
                }

                string repoRoot = FindRepositoryRoot();
                string resolveFrameworkPath = Path.Combine(repoRoot, "scripts", "windows", "resolve-framework.bat");

                string positionalResult = RunCommandAndCaptureOutput(
                    "cmd.exe",
                    "/c call \"" + resolveFrameworkPath + "\" net8.0 >nul && echo %ARMADA_TARGET_FRAMEWORK%",
                    repoRoot);
                AssertEqual("net8.0", positionalResult.Trim(), "resolve-framework.bat should accept positional framework values");

                string namedResult = RunCommandAndCaptureOutput(
                    "cmd.exe",
                    "/c call \"" + resolveFrameworkPath + "\" --framework net8.0 >nul && echo %ARMADA_TARGET_FRAMEWORK%",
                    repoRoot);
                AssertEqual("net8.0", namedResult.Trim(), "resolve-framework.bat should accept named framework values");
            });

            await RunTest("Status Health Route Uses ProductVersion Constant", () =>
            {
                string statusRoutesContents = ReadRepositoryFile("src", "Armada.Server", "Routes", "StatusRoutes.cs");
                Match healthRouteMatch = Regex.Match(
                    statusRoutesContents,
                    @"app(?:\.Rest)?\.Get\(""/api/v1/status/health"",[\s\S]*?\.WithDescription\(""Returns health status\. Does not require authentication\.""\)\);");

                AssertTrue(healthRouteMatch.Success, "StatusRoutes should include the REST health route");

                string healthRouteBlock = healthRouteMatch.Value;
                AssertContains("Version = ArmadaConstants.ProductVersion,", healthRouteBlock, "Health route should return ArmadaConstants.ProductVersion");
                AssertFalse(
                    healthRouteBlock.Contains("Version = \"" + Constants.ProductVersion + "\"", StringComparison.Ordinal),
                    "Health route should not hard-code the canonical release version");

                AssertNoStaleVersionSurfaces(
                    healthRouteBlock,
                    "StatusRoutes health route",
                    staleVersion => "Version = \"" + staleVersion + "\"");
            });

            await RunTest("Versioned Docs And Examples Match ProductVersion", () =>
            {
                string restApiContents = ReadRepositoryFile("docs", "REST_API.md");
                string mcpApiContents = ReadRepositoryFile("docs", "MCP_API.md");
                string proxyApiContents = ReadRepositoryFile("docs", "PROXY_API.md");
                string postmanContents = ReadRepositoryFile("Armada.postman_collection.json");
                string restHealthSample = ExtractRestHealthResponseSample(restApiContents);
                string restJsonExamples = ExtractMarkdownJsonExamples(restApiContents);
                string mcpJsonExamples = ExtractMarkdownJsonExamples(mcpApiContents);
                string proxyJsonExamples = ExtractMarkdownJsonExamples(proxyApiContents);
                string postmanResponseBodies = ExtractPostmanResponseBodies(postmanContents);
                string postmanHealthResponseBody = ExtractPostmanHealthyResponseBody(postmanContents);

                AssertEqual(1, Regex.Matches(restApiContents, @"#### GET /api/v1/status/health").Count, "REST API should document the health endpoint exactly once");
                AssertEqual(1, Regex.Matches(postmanContents, @"""raw"":\s*""\{\{baseUrl\}\}/api/v1/status/health""").Count, "Postman collection should include the health request exactly once");
                AssertEqual(1, Regex.Matches(postmanContents, @"""name"":\s*""Healthy""").Count, "Postman collection should include the healthy response example exactly once");
                AssertContains("**Version:** " + Constants.ProductVersion, restApiContents, "REST API header should use the shared release version");
                AssertContains("**Version:** " + Constants.ProductVersion, mcpApiContents, "MCP API header should use the shared release version");
                AssertContains("**Version:** " + Constants.ProductVersion, proxyApiContents, "Proxy API header should use the shared release version");
                AssertContains("Version: " + Constants.ProductVersion, postmanContents, "Postman collection description should use the shared release version");
                AssertContains("\"Version\": \"" + Constants.ProductVersion + "\"", restHealthSample, "REST API health example should use the shared release version");
                AssertContains("\"Version\": \"" + Constants.ProductVersion + "\"", postmanHealthResponseBody, "Postman health response body should use the shared release version");

                AssertNoStaleVersionSurfaces(restJsonExamples, "REST API JSON examples", staleVersion => staleVersion);
                AssertNoStaleVersionSurfaces(mcpJsonExamples, "MCP API JSON examples", staleVersion => staleVersion);
                AssertNoStaleVersionSurfaces(proxyJsonExamples, "Proxy API JSON examples", staleVersion => staleVersion);
                AssertNoStaleVersionSurfaces(postmanResponseBodies, "Postman response bodies", staleVersion => staleVersion);
                AssertNoStaleVersionSurfaces(restHealthSample, "REST API health example", staleVersion => "\"Version\": \"" + staleVersion + "\"");
                AssertNoStaleVersionSurfaces(postmanHealthResponseBody, "Postman health response body", staleVersion => "\"Version\": \"" + staleVersion + "\"");
            });

            await RunTest("Release Surface Extractors Fail Closed When Health Samples Drift", () =>
            {
                string restApiContents = ReadRepositoryFile("docs", "REST_API.md");
                string postmanContents = ReadRepositoryFile("Armada.postman_collection.json");

                AssertThrows<Exception>(
                    () => ExtractRestHealthResponseSample(
                        restApiContents.Replace("#### GET /api/v1/status/health", "#### GET /api/v1/status", StringComparison.Ordinal)),
                    "REST API extractor should fail when the health section heading is missing");
                AssertThrows<Exception>(
                    () => ExtractPostmanHealthyResponseBody(
                        postmanContents.Replace("\"name\": \"Healthy\"", "\"name\": \"HealthyExample\"", StringComparison.Ordinal)),
                    "Postman extractor should fail when the healthy response example is missing");
                AssertThrows<Exception>(
                    () => AssertNoStaleVersionSurfaces("Version: 0.4.0", "synthetic release surface", staleVersion => staleVersion),
                    "Stale release helper should fail when a prior version literal is present");
            });
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src")) &&
                    Directory.Exists(Path.Combine(current.FullName, "test")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
        }

        private void AssertNoStaleVersionSurfaces(string contents, string surfaceName, Func<string, string> staleSurfaceFactory)
        {
            foreach (string staleVersion in _StaleReleaseVersions)
            {
                string staleSurface = staleSurfaceFactory(staleVersion);
                AssertFalse(
                    contents.Contains(staleSurface, StringComparison.Ordinal),
                    surfaceName + " should not contain stale release literal " + staleSurface);
            }
        }

        private static string ReadRepositoryFile(params string[] relativePath)
        {
            return File.ReadAllText(Path.Combine(FindRepositoryRoot(), Path.Combine(relativePath)));
        }

        private static string RunCommandAndCaptureOutput(string fileName, string arguments, string workingDirectory)
        {
            using Process process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new Exception("Command failed: " + fileName + " " + arguments + Environment.NewLine + stdout + stderr);
            }

            return stdout;
        }

        private static string ExtractRestHealthResponseSample(string restApiContents)
        {
            Match match = Regex.Match(
                restApiContents,
                @"#### GET /api/v1/status/health[\s\S]*?```json\s*(?<body>\{[\s\S]*?\})\s*```");

            if (!match.Success)
            {
                throw new Exception("Could not locate the REST API health response sample.");
            }

            return match.Groups["body"].Value;
        }

        private static string ExtractMarkdownJsonExamples(string markdownContents)
        {
            MatchCollection matches = Regex.Matches(
                markdownContents,
                @"```json\s*(?<body>[\s\S]*?)\s*```");

            if (matches.Count == 0)
            {
                throw new Exception("Could not locate any markdown JSON examples.");
            }

            System.Text.StringBuilder combined = new System.Text.StringBuilder();
            foreach (Match match in matches)
            {
                if (combined.Length > 0)
                {
                    combined.Append('\n');
                }

                combined.Append(match.Groups["body"].Value);
            }

            return combined.ToString();
        }

        private static string ExtractPostmanResponseBodies(string postmanContents)
        {
            MatchCollection matches = Regex.Matches(
                postmanContents,
                @"""body"":\s*""(?<body>(?:\\.|[^""\\])*)""");

            if (matches.Count == 0)
            {
                throw new Exception("Could not locate any Postman response bodies.");
            }

            System.Text.StringBuilder combined = new System.Text.StringBuilder();
            foreach (Match match in matches)
            {
                if (combined.Length > 0)
                {
                    combined.Append('\n');
                }

                combined.Append(Regex.Unescape(match.Groups["body"].Value));
            }

            return combined.ToString();
        }

        private static string ExtractPostmanHealthyResponseBody(string postmanContents)
        {
            Match match = Regex.Match(
                postmanContents,
                @"""name"":\s*""Healthy""[\s\S]*?""body"":\s*""(?<body>(?:\\.|[^""\\])*)""");

            if (!match.Success)
            {
                throw new Exception("Could not locate the Postman health response body.");
            }

            return Regex.Unescape(match.Groups["body"].Value);
        }
    }
}
