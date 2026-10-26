namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors that pin the release surface to <see cref="Constants.ProductVersion"/>:
    /// shared build props, Helm entry point, MCP install scripts, Windows framework overrides,
    /// the status health route, and versioned docs/Postman examples. Positive cases assert the
    /// canonical version is wired through and stale literals are absent; negative cases assert
    /// scripts fail closed and the doc extractors throw when their anchors drift.
    /// </summary>
    public sealed class ReleaseVersionSuite : IArmadaTestSuite
    {
        #region Private-Members

        private static readonly string[] _StaleReleaseVersions = { "0.8.0", "0.7.0", "0.6.0", "0.5.0", "0.4.0", "0.3.0" };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the ReleaseVersion suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("product_version_and_shared_build_props_match_v090", "ProductVersion And Shared Build Props Match V090", TestTags.Positive, () =>
            {
                string repoRoot = FindRepositoryRoot();
                string propsContents = File.ReadAllText(Path.Combine(repoRoot, "src", "Directory.Build.props"));
                string helmProjectContents = File.ReadAllText(Path.Combine(repoRoot, "src", "Armada.Helm", "Armada.Helm.csproj"));
                string dashboardPackageContents = File.ReadAllText(Path.Combine(repoRoot, "src", "Armada.Dashboard", "package.json"));
                string dashboardLockContents = File.ReadAllText(Path.Combine(repoRoot, "src", "Armada.Dashboard", "package-lock.json"));
                MatchCollection versionMatches = Regex.Matches(propsContents, @"<Version>\s*([^<]+)\s*</Version>");

                AssertTrue(versionMatches.Count == 1, "Directory.Build.props should contain exactly one Version element");
                Match versionMatch = versionMatches[0];
                AssertEqual("0.9.0", Constants.ProductVersion);
                AssertEqual(Constants.ProductVersion, versionMatch.Groups[1].Value.Trim());
                AssertContains("<Version>" + Constants.ProductVersion + "</Version>", helmProjectContents, "Helm package version should align with the shared release version");
                AssertContains("\"version\": \"" + Constants.ProductVersion + "\"", dashboardPackageContents, "Dashboard package.json should align with the shared release version");
                AssertContains("\"version\": \"" + Constants.ProductVersion + "\"", dashboardLockContents, "Dashboard package-lock.json should align with the shared release version");
            }));

            cases.Add(Case("helm_program_uses_product_version_constant", "Helm Program Uses ProductVersion Constant", TestTags.Positive, () =>
            {
                string programContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Armada.Helm", "Program.cs"));

                AssertContains("\"v\" + Constants.ProductVersion", programContents, "Helm banner/help version should come from Constants.ProductVersion");
                AssertContains("AnsiConsole.MarkupLine(\"[dim]Multi-Agent Orchestration System  \" + _VersionLabel + \"[/]\");", programContents, "Helm subtitle should render the shared version label");
                AssertContains("config.SetApplicationVersion(Constants.ProductVersion);", programContents, "Helm CLI version should come from Constants.ProductVersion");
                AssertFalse(programContents.Contains("0.3.0"), "Helm entry point should not contain the stale 0.3.0 literal");
                AssertFalse(programContents.Contains("\"0.9.0\""), "Helm entry point should not contain a hard-coded release version literal");
                AssertFalse(programContents.Contains("\"v0.9.0\""), "Helm entry point should compose the prefixed version instead of hard-coding it");
                AssertFalse(programContents.Contains("SetApplicationVersion(\"0.9.0\")"), "Helm CLI version should not be hard-coded");
            }));

            cases.Add(Case("windows_install_scripts_allow_explicit_framework_overrides", "Windows Install Scripts Allow Explicit Framework Overrides", TestTags.Positive, () =>
            {
                string installBatContents = ReadRepositoryFile("scripts", "windows", "install.bat");
                string reinstallBatContents = ReadRepositoryFile("scripts", "windows", "reinstall.bat");
                string removeBatContents = ReadRepositoryFile("scripts", "windows", "remove.bat");
                string updateBatContents = ReadRepositoryFile("scripts", "windows", "update.bat");
                string publishServerBatContents = ReadRepositoryFile("scripts", "windows", "publish-server.bat");
                string deployDashboardBatContents = ReadRepositoryFile("scripts", "windows", "deploy-dashboard.bat");
                string installWindowsTaskBatContents = ReadRepositoryFile("scripts", "windows", "install-windows-task.bat");
                string updateWindowsTaskBatContents = ReadRepositoryFile("scripts", "windows", "update-windows-task.bat");
                string removeWindowsTaskBatContents = ReadRepositoryFile("scripts", "windows", "remove-windows-task.bat");
                string healthcheckServerBatContents = ReadRepositoryFile("scripts", "windows", "healthcheck-server.bat");
                string serverStartCommandContents = ReadRepositoryFile("src", "Armada.Helm", "Commands", "ServerStartCommand.cs");

                AssertContains("call \"%SCRIPT_DIR%\\resolve-framework.bat\" %*", installBatContents, "install.bat should resolve framework overrides");
                AssertContains("dotnet build \"%REPO_ROOT%\\src\\Armada.sln\" %ARMADA_DOTNET_FRAMEWORK_ARGS%", installBatContents, "install.bat should build with the resolved framework");
                AssertContains("dotnet pack \"%REPO_ROOT%\\src\\Armada.Helm\\Armada.Helm.csproj\" %ARMADA_DOTNET_MSBUILD_FRAMEWORK_ARGS%", installBatContents, "install.bat should pack with the resolved framework");
                AssertContains("call \"%SCRIPT_DIR%\\resolve-framework.bat\" %*", reinstallBatContents, "reinstall.bat should resolve framework overrides");
                AssertContains("call \"%SCRIPT_DIR%\\install.bat\" %ARMADA_FORWARD_FRAMEWORK_ARGS%", reinstallBatContents, "reinstall.bat should forward the resolved framework explicitly");
                AssertContains("call \"%SCRIPT_DIR%\\resolve-framework.bat\" %*", removeBatContents, "remove.bat should accept framework overrides for parity");
                AssertContains("set \"HELM_DLL=%REPO_ROOT%\\src\\Armada.Helm\\bin\\Debug\\%ARMADA_TARGET_FRAMEWORK%\\Armada.Helm.dll\"", updateBatContents, "update.bat should probe the resolved Helm build output");
                AssertContains("call \"%SCRIPT_DIR%\\reinstall.bat\" %ARMADA_FORWARD_FRAMEWORK_ARGS%", updateBatContents, "update.bat should forward the resolved framework to reinstall explicitly");
                AssertContains("dotnet run --project \"%REPO_ROOT%\\src\\Armada.Helm\" %ARMADA_DOTNET_FRAMEWORK_ARGS% -- %*", updateBatContents, "update.bat should fall back to the resolved framework");
                AssertTrue(
                    updateBatContents.IndexOf("if exist \"%HELM_DLL%\"", StringComparison.Ordinal) < updateBatContents.IndexOf("where armada", StringComparison.Ordinal),
                    "update.bat should prefer repo-targeted Helm execution before the global tool");
                AssertContains("dotnet publish \"%REPO_ROOT%\\src\\Armada.Server\" -c Release %ARMADA_DOTNET_FRAMEWORK_ARGS%", publishServerBatContents, "publish-server.bat should publish with the resolved framework");
                AssertContains("set \"LOCAL_TSC=%LOCAL_BIN_DIR%\\tsc.cmd\"", deployDashboardBatContents, "deploy-dashboard.bat should resolve the local TypeScript compiler");
                AssertContains("set \"LOCAL_VITE=%LOCAL_BIN_DIR%\\vite.cmd\"", deployDashboardBatContents, "deploy-dashboard.bat should resolve the local Vite CLI");
                AssertContains("call \"%LOCAL_TSC%\"", deployDashboardBatContents, "deploy-dashboard.bat should build with the local TypeScript compiler instead of npm run");
                AssertContains("call \"%LOCAL_VITE%\" build", deployDashboardBatContents, "deploy-dashboard.bat should build with the local Vite CLI instead of npm run");
                AssertContains("if exist \"%USERPROFILE%\\.armada\\dashboard\\index.html\" (", publishServerBatContents, "publish-server.bat should only continue after dashboard deploy errors when a React dashboard is already deployed");
                AssertContains("echo ERROR: Dashboard deploy failed and no deployed React dashboard is available.", publishServerBatContents, "publish-server.bat should fail fresh installs when the React dashboard is unavailable");
                AssertContains("call \"%SCRIPT_DIR%\\publish-server.bat\" %ARMADA_FORWARD_FRAMEWORK_ARGS%", installWindowsTaskBatContents, "install-windows-task.bat should forward the resolved framework to publish-server explicitly");
                AssertContains("call \"%SCRIPT_DIR%\\install-windows-task.bat\" %ARMADA_FORWARD_FRAMEWORK_ARGS%", updateWindowsTaskBatContents, "update-windows-task.bat should forward the resolved framework explicitly");
                AssertContains("call \"%SCRIPT_DIR%\\resolve-framework.bat\" %*", removeWindowsTaskBatContents, "remove-windows-task.bat should accept framework overrides for parity");
                AssertContains("string targetFramework = GetCliTargetFramework();", serverStartCommandContents, "ServerStartCommand should derive the build framework from the running Helm target");
                AssertContains("buildInfo.ArgumentList.Add(targetFramework);", serverStartCommandContents, "ServerStartCommand should build Armada.Server with the derived framework");
                AssertContains("buildInfo.ArgumentList.Add(\"-p:TargetFramework=\" + targetFramework);", serverStartCommandContents, "ServerStartCommand should lock the server build to the derived framework");
                AssertContains("buildInfo.ArgumentList.Add(\"-p:TargetFrameworks=\" + targetFramework);", serverStartCommandContents, "ServerStartCommand should prevent multi-target evaluation on framework-limited machines");
                AssertContains("string tscPath = Path.Combine(localBinDir, \"tsc\" + toolExtension);", serverStartCommandContents, "ServerStartCommand should use the local TypeScript compiler");
                AssertContains("string vitePath = Path.Combine(localBinDir, \"vite\" + toolExtension);", serverStartCommandContents, "ServerStartCommand should use the local Vite CLI");
                AssertContains("Keeping the existing deployed React dashboard", serverStartCommandContents, "ServerStartCommand should preserve the existing React dashboard instead of silently downgrading to the legacy UI");
                AssertContains("return $\"net{parsed.Version.Major}.{parsed.Version.Minor}\";", serverStartCommandContents, "ServerStartCommand should map the runtime TFM to a dotnet framework moniker");
                AssertContains("if /I \"%~1\"==\"--framework\" set \"BASE_URL=%~3\"", healthcheckServerBatContents, "healthcheck-server.bat should ignore a leading named framework override");
                AssertContains("findstr /R /I \"^net[0-9][0-9]*\\.[0-9][0-9]*$\"", healthcheckServerBatContents, "healthcheck-server.bat should ignore a leading positional framework override");
            }));

            cases.Add(Case("windows_framework_resolver_accepts_positional_and_named_overrides", "Windows Framework Resolver Accepts Positional And Named Overrides", TestTags.Positive, () =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    return;
                }

                string repoRoot = FindRepositoryRoot();
                string resolveFrameworkPath = Path.Combine(repoRoot, "scripts", "windows", "resolve-framework.bat");

                string positionalResult = RunCommandAndCaptureOutput(
                    "cmd.exe",
                    "/v:on /c call \"" + resolveFrameworkPath + "\" net8.0 >nul && echo !ARMADA_TARGET_FRAMEWORK!",
                    repoRoot);
                AssertEqual("net8.0", positionalResult.Trim(), "resolve-framework.bat should accept positional framework values");

                string namedResult = RunCommandAndCaptureOutput(
                    "cmd.exe",
                    "/v:on /c call \"" + resolveFrameworkPath + "\" --framework net8.0 >nul && echo !ARMADA_TARGET_FRAMEWORK!",
                    repoRoot);
                AssertEqual("net8.0", namedResult.Trim(), "resolve-framework.bat should accept named framework values");
            }));

            cases.Add(Case("windows_install_task_script_preserves_explicit_framework_end_to_end", "Windows Install Task Script Preserves Explicit Framework End To End", TestTags.Positive, () =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    return;
                }

                string repoRoot = FindRepositoryRoot();
                string tempRoot = Path.Combine(Path.GetTempPath(), "armada-script-test-" + Guid.NewGuid().ToString("N"));
                string tempRepo = Path.Combine(tempRoot, "repo");
                string tempUserProfile = Path.Combine(tempRoot, "user");
                string toolsDir = Path.Combine(tempRoot, "tools");
                string logPath = Path.Combine(tempRoot, "dotnet.log");

                Directory.CreateDirectory(tempRepo);
                Directory.CreateDirectory(tempUserProfile);
                Directory.CreateDirectory(toolsDir);

                try
                {
                    string windowsScriptsDir = Path.Combine(tempRepo, "scripts", "windows");
                    Directory.CreateDirectory(windowsScriptsDir);
                    CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "windows", "resolve-framework.bat"));
                    CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "windows", "publish-server.bat"));
                    CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "windows", "deploy-dashboard.bat"));
                    CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "windows", "install-windows-task.bat"));
                    CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "windows", "healthcheck-server.bat"));
                    CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "windows", "start-armada-server.ps1"));
                    CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "windows", "stop-armada-server.ps1"));

                    string dashboardDir = Path.Combine(tempRepo, "src", "Armada.Dashboard");
                    Directory.CreateDirectory(dashboardDir);
                    File.WriteAllText(Path.Combine(dashboardDir, "package.json"), "{}");
                    Directory.CreateDirectory(Path.Combine(dashboardDir, "node_modules", ".bin"));
                    File.WriteAllText(
                        Path.Combine(dashboardDir, "node_modules", ".bin", "tsc.cmd"),
                        "@echo off\r\n" +
                        "exit /b 0\r\n");
                    File.WriteAllText(
                        Path.Combine(dashboardDir, "node_modules", ".bin", "vite.cmd"),
                        "@echo off\r\n" +
                        "set \"DIST_DIR=%REPO_ROOT_FOR_TEST%\\src\\Armada.Dashboard\\dist\"\r\n" +
                        "mkdir \"%DIST_DIR%\" >nul 2>nul\r\n" +
                        "type nul > \"%DIST_DIR%\\index.html\"\r\n" +
                        "exit /b 0\r\n");

                    File.WriteAllText(
                        Path.Combine(toolsDir, "dotnet.cmd"),
                        "@echo off\r\n" +
                        "echo %*>>\"%DOTNET_LOG%\"\r\n" +
                        "if /I \"%~1\"==\"publish\" call :publish %*\r\n" +
                        "exit /b 0\r\n" +
                        "\r\n" +
                        ":publish\r\n" +
                        "set \"OUTPUT_DIR=\"\r\n" +
                        ":publish_args\r\n" +
                        "if \"%~1\"==\"\" goto publish_done\r\n" +
                        "if /I \"%~1\"==\"-o\" (\r\n" +
                        "  set \"OUTPUT_DIR=%~2\"\r\n" +
                        "  shift\r\n" +
                        ")\r\n" +
                        "shift\r\n" +
                        "goto publish_args\r\n" +
                        "\r\n" +
                        ":publish_done\r\n" +
                        "if \"%OUTPUT_DIR%\"==\"\" exit /b 1\r\n" +
                        "mkdir \"%OUTPUT_DIR%\" >nul 2>nul\r\n" +
                        "type nul > \"%OUTPUT_DIR%\\Armada.Server.exe\"\r\n" +
                        "exit /b 0\r\n");

                    File.WriteAllText(
                        Path.Combine(toolsDir, "npm.cmd"),
                        "@echo off\r\n" +
                        "exit /b 1\r\n");

                    File.WriteAllText(
                        Path.Combine(toolsDir, "powershell.cmd"),
                        "@echo off\r\n" +
                        "exit /b 0\r\n");

                    string installTaskPath = Path.Combine(tempRepo, "scripts", "windows", "install-windows-task.bat");
                    RunCommandAndCaptureOutput(
                        "cmd.exe",
                        "/c call \"" + installTaskPath + "\" net8.0",
                        tempRepo,
                        new Dictionary<string, string>
                        {
                            ["PATH"] = toolsDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                            ["USERPROFILE"] = tempUserProfile,
                            ["DOTNET_LOG"] = logPath,
                            ["REPO_ROOT_FOR_TEST"] = tempRepo
                        });

                    string dotnetLogContents = File.ReadAllText(logPath);
                    AssertContains("publish \"" + Path.Combine(tempRepo, "src", "Armada.Server") + "\" -c Release --framework net8.0 -p:TargetFramework=net8.0 -p:TargetFrameworks=net8.0 -o \"" + Path.Combine(tempUserProfile, ".armada", "bin") + "\"", dotnetLogContents, "install-windows-task.bat should preserve the explicit framework into publish-server.bat");
                    AssertFalse(dotnetLogContents.Contains("net10.0", StringComparison.Ordinal), "install-windows-task.bat should not fall back to net10.0 when net8.0 is requested");
                }
                finally
                {
                    if (Directory.Exists(tempRoot))
                    {
                        Directory.Delete(tempRoot, recursive: true);
                    }
                }
            }));

            cases.Add(Case("windows_publish_server_script_fails_when_react_dashboard_is_unavailable", "Windows Publish Server Script Fails When React Dashboard Is Unavailable", TestTags.Negative, () =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    return;
                }

                string repoRoot = FindRepositoryRoot();
                string tempRoot = Path.Combine(Path.GetTempPath(), "armada-publish-test-" + Guid.NewGuid().ToString("N"));
                string tempRepo = Path.Combine(tempRoot, "repo");
                string tempUserProfile = Path.Combine(tempRoot, "user");
                string toolsDir = Path.Combine(tempRoot, "tools");
                string logPath = Path.Combine(tempRoot, "dotnet.log");

                Directory.CreateDirectory(tempRepo);
                Directory.CreateDirectory(tempUserProfile);
                Directory.CreateDirectory(toolsDir);

                try
                {
                    CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "windows", "resolve-framework.bat"));
                    CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "windows", "publish-server.bat"));
                    CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "windows", "deploy-dashboard.bat"));

                    string dashboardDir = Path.Combine(tempRepo, "src", "Armada.Dashboard");
                    Directory.CreateDirectory(dashboardDir);
                    File.WriteAllText(Path.Combine(dashboardDir, "package.json"), "{}");
                    File.WriteAllText(Path.Combine(dashboardDir, "package-lock.json"), "{}");

                    File.WriteAllText(
                        Path.Combine(toolsDir, "dotnet.cmd"),
                        "@echo off\r\n" +
                        "echo %*>>\"%DOTNET_LOG%\"\r\n" +
                        "if /I \"%~1\"==\"publish\" call :publish %*\r\n" +
                        "exit /b 0\r\n" +
                        "\r\n" +
                        ":publish\r\n" +
                        "set \"OUTPUT_DIR=\"\r\n" +
                        ":publish_args\r\n" +
                        "if \"%~1\"==\"\" goto publish_done\r\n" +
                        "if /I \"%~1\"==\"-o\" (\r\n" +
                        "  set \"OUTPUT_DIR=%~2\"\r\n" +
                        "  shift\r\n" +
                        ")\r\n" +
                        "shift\r\n" +
                        "goto publish_args\r\n" +
                        "\r\n" +
                        ":publish_done\r\n" +
                        "if \"%OUTPUT_DIR%\"==\"\" exit /b 1\r\n" +
                        "mkdir \"%OUTPUT_DIR%\" >nul 2>nul\r\n" +
                        "type nul > \"%OUTPUT_DIR%\\Armada.Server.exe\"\r\n" +
                        "exit /b 0\r\n");

                    File.WriteAllText(
                        Path.Combine(toolsDir, "npm.cmd"),
                        "@echo off\r\n" +
                        "exit /b 1\r\n");

                    string publishServerPath = Path.Combine(tempRepo, "scripts", "windows", "publish-server.bat");
                    Exception? captured = null;
                    try
                    {
                        RunCommandAndCaptureOutput(
                            "cmd.exe",
                            "/c call \"" + publishServerPath + "\" net8.0",
                            tempRepo,
                            new Dictionary<string, string>
                            {
                                ["PATH"] = toolsDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                                ["USERPROFILE"] = tempUserProfile,
                                ["DOTNET_LOG"] = logPath
                            });
                    }
                    catch (Exception ex)
                    {
                        captured = ex;
                    }

                    AssertTrue(captured != null, "publish-server.bat should fail when React dashboard deployment fails on a fresh install");
                    AssertContains("Dashboard deploy failed and no deployed React dashboard is available.", captured!.Message, "publish-server.bat should report that the React dashboard is required for a fresh install");
                }
                finally
                {
                    if (Directory.Exists(tempRoot))
                    {
                        Directory.Delete(tempRoot, recursive: true);
                    }
                }
            }));

            cases.Add(Case("posix_publish_server_fails_when_react_dashboard_is_unavailable", "POSIX Publish Server Fails When React Dashboard Is Unavailable", TestTags.Negative, () =>
            {
                PosixPublishResult result = RunPosixPublishScenario(createServer: true, dashboardDeploySucceeds: false, existingDashboard: false);

                AssertTrue(result.ExitCode != 0, "publish-server.sh should fail when dashboard deployment fails and no prior dashboard exists");
                AssertContains("Dashboard deploy failed and no deployed React dashboard is available.", result.Output, "the failure should name the missing dashboard");
            }, skip: OperatingSystem.IsWindows(), skipReason: "POSIX shell behavior is covered on Linux and macOS."));

            cases.Add(Case("posix_publish_server_keeps_existing_dashboard_after_deploy_failure", "POSIX Publish Server Keeps Existing Dashboard After Deploy Failure", TestTags.Positive, () =>
            {
                PosixPublishResult result = RunPosixPublishScenario(createServer: true, dashboardDeploySucceeds: false, existingDashboard: true);

                AssertEqual(0, result.ExitCode, "a failed dashboard build may keep a previously deployed dashboard");
                AssertContains("Keeping the previously deployed React dashboard.", result.Output, "the warning should say that the old dashboard stays in place");
                AssertEqual("index.html=existing-dashboard", result.DashboardContent, "the previously deployed dashboard should remain unchanged");
            }, skip: OperatingSystem.IsWindows(), skipReason: "POSIX shell behavior is covered on Linux and macOS."));

            cases.Add(Case("posix_publish_server_succeeds_when_server_and_dashboard_publish", "POSIX Publish Server Succeeds When Server And Dashboard Publish", TestTags.Positive, () =>
            {
                PosixPublishResult result = RunPosixPublishScenario(createServer: true, dashboardDeploySucceeds: true, existingDashboard: false, useRealDashboardDeploy: true);

                AssertEqual(0, result.ExitCode, "server and dashboard publish should complete");
                AssertContains("[publish-server] Completed.", result.Output, "successful publish should report completion");
                AssertEqual(2, result.PublishRuns, "a second publish should prove the deploy lock is released");
                AssertEqual("assets/app.js=build-asset\nindex.html=build-index", result.DashboardContent, "successful deploy should install the new dashboard");
                AssertFalse(result.DeployLockExists, "successful deploys should release their lock");
                AssertEqual(0, result.StagingDirectoryCount, "successful deploys should remove their staging directories");
            }, skip: OperatingSystem.IsWindows(), skipReason: "POSIX shell behavior is covered on Linux and macOS."));

            cases.Add(Case("posix_publish_server_fails_when_published_server_executable_is_missing", "POSIX Publish Server Fails When Published Server Executable Is Missing", TestTags.Negative, () =>
            {
                PosixPublishResult result = RunPosixPublishScenario(createServer: false, dashboardDeploySucceeds: true, existingDashboard: false);

                AssertTrue(result.ExitCode != 0, "publish-server.sh should fail when dotnet did not create the server executable");
                AssertContains("Published server executable not found", result.Output, "the failure should identify the missing server executable");
            }, skip: OperatingSystem.IsWindows(), skipReason: "POSIX shell behavior is covered on Linux and macOS."));

            cases.Add(Case("posix_publish_server_keeps_complete_dashboard_when_copy_fails_partway", "POSIX Publish Server Keeps Complete Dashboard When Copy Fails Partway", TestTags.Positive, () =>
            {
                PosixPublishResult result = RunPosixPublishScenario(createServer: true, dashboardDeploySucceeds: false, existingDashboard: true, useRealDashboardDeploy: true, failDashboardCopy: true);

                AssertEqual(0, result.ExitCode, "a failed staged dashboard copy may keep the previous dashboard");
                AssertContains("Keeping the previously deployed React dashboard.", result.Output, "the publish helper should report the retained dashboard");
                AssertEqual("assets/app.js=old-asset\nindex.html=old-index", result.DashboardContent, "the complete previous dashboard tree should remain unchanged");
                AssertEqual(0, result.StagingDirectoryCount, "a failed staged copy should be cleaned up");
                AssertFalse(result.DeployLockExists, "a failed staged copy should not leave a deploy lock");
            }, skip: OperatingSystem.IsWindows(), skipReason: "POSIX shell behavior is covered on Linux and macOS."));

            cases.Add(Case("posix_publish_server_rejects_partial_dashboard_copy_on_fresh_install", "POSIX Publish Server Rejects Partial Dashboard Copy On Fresh Install", TestTags.Negative, () =>
            {
                PosixPublishResult result = RunPosixPublishScenario(createServer: true, dashboardDeploySucceeds: false, existingDashboard: false, useRealDashboardDeploy: true, failDashboardCopy: true);

                AssertTrue(result.ExitCode != 0, "a failed partial dashboard copy must fail a fresh publish");
                AssertContains("Dashboard deploy failed and no deployed React dashboard is available.", result.Output, "the failure should name the unavailable dashboard");
                AssertEqual(String.Empty, result.DashboardContent, "a partial dashboard must not be installed on a fresh publish");
                AssertEqual(0, result.StagingDirectoryCount, "a failed staged copy should be cleaned up");
                AssertFalse(result.DeployLockExists, "a failed staged copy should not leave a deploy lock");
            }, skip: OperatingSystem.IsWindows(), skipReason: "POSIX shell behavior is covered on Linux and macOS."));

            cases.Add(Case("posix_dashboard_deploy_uses_committed_dist_without_node", "POSIX Dashboard Deploy Uses Committed Dist Without Node", TestTags.Positive, () =>
            {
                PosixPublishResult result = RunPosixPublishScenario(createServer: true, dashboardDeploySucceeds: true, existingDashboard: false, useRealDashboardDeploy: true, nodeAvailable: false);

                AssertEqual(0, result.ExitCode, "the committed dashboard should deploy when Node is unavailable. Output: " + result.Output);
                AssertEqual("assets/app.js=dist-asset\nindex.html=dist-index", result.DashboardContent, "the complete committed dist tree should deploy");
                AssertEqual(2, result.PublishRuns, "the committed dashboard path should also release its lock for the next deploy");
            }, skip: OperatingSystem.IsWindows(), skipReason: "POSIX shell behavior is covered on Linux and macOS."));

            cases.Add(Case("posix_dashboard_deploy_rejects_concurrent_build_before_build_starts", "POSIX Dashboard Deploy Rejects Concurrent Build Before Build Starts", TestTags.Negative, () =>
            {
                ConcurrentDashboardDeployResult result = RunConcurrentDashboardDeployScenario();

                AssertTrue(result.SecondExitCode != 0, "the second dashboard deploy should be refused while the first owns the lock");
                AssertContains("Another dashboard deploy is active", result.SecondOutput, "the refusal should identify the active deploy");
                AssertEqual(0, result.FirstExitCode, "the first dashboard deploy should complete after its build is released");
                AssertEqual(1, result.BuildCount, "the refused deploy should not start a second dashboard build");
                AssertEqual("first-build", result.DashboardContent, "the first deploy should install its complete output after the lock is released");
                AssertFalse(result.LockExists, "the completed deploy should release its lock");
            }, skip: OperatingSystem.IsWindows(), skipReason: "POSIX shell behavior is covered on Linux and macOS."));

            cases.Add(Case("status_health_route_uses_product_version_constant", "Status Health Route Uses ProductVersion Constant", TestTags.Positive, () =>
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
            }));

            cases.Add(Case("versioned_docs_and_examples_match_product_version", "Versioned Docs And Examples Match ProductVersion", TestTags.Positive, () =>
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
            }));

            cases.Add(Case("release_surface_extractors_fail_closed_when_health_samples_drift", "Release Surface Extractors Fail Closed When Health Samples Drift", TestTags.Negative, () =>
            {
                string restApiContents = ReadRepositoryFile("docs", "REST_API.md");
                string postmanContents = ReadRepositoryFile("Armada.postman_collection.json");

                AssertThrows<Exception>(
                    () => ExtractRestHealthResponseSample(
                        restApiContents.Replace("#### GET /api/v1/status/health", "#### GET /api/v1/status", StringComparison.Ordinal)),
                    "REST API extractor should fail when the health section heading is missing");
                AssertThrows<Exception>(
                    () => ExtractPostmanHealthyResponseBody(
                        Regex.Replace(postmanContents, "\"name\":\\s*\"Healthy\"", "\"name\": \"HealthyExample\"")),
                    "Postman extractor should fail when the healthy response example is missing");
                AssertThrows<Exception>(
                    () => AssertNoStaleVersionSurfaces("Version: 0.4.0", "synthetic release surface", staleVersion => staleVersion),
                    "Stale release helper should fail when a prior version literal is present");
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.ReleaseVersion",
                displayName: "Release Version",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src")) &&
                    Directory.Exists(Path.Combine(current.FullName, "docs")) &&
                    File.Exists(Path.Combine(current.FullName, "README.md")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
        }

        private static void AssertNoStaleVersionSurfaces(string contents, string surfaceName, Func<string, string> staleSurfaceFactory)
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

        private static void CopyRepositoryFile(string repoRoot, string tempRepo, string relativePath)
        {
            string sourcePath = Path.Combine(repoRoot, relativePath);
            string destinationPath = Path.Combine(tempRepo, relativePath);
            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        private static PosixPublishResult RunPosixPublishScenario(
            bool createServer,
            bool dashboardDeploySucceeds,
            bool existingDashboard,
            bool useRealDashboardDeploy = false,
            bool failDashboardCopy = false,
            bool nodeAvailable = true)
        {
            string repoRoot = FindRepositoryRoot();
            string tempRoot = TestTemp.NewDirectory("publish-server-posix");
            string tempRepo = Path.Combine(tempRoot, "repo");
            string home = Path.Combine(tempRoot, "home");
            string tools = Path.Combine(tempRoot, "tools");
            string temp = Path.Combine(tempRoot, "tmp");
            string scripts = Path.Combine(tempRepo, "scripts", "common");
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(tools);
            Directory.CreateDirectory(temp);

            try
            {
                CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "common", "publish-server.sh"));
                CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "common", "resolve-framework.sh"));
                CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "common", "resolve-insecure.sh"));
                if (useRealDashboardDeploy)
                {
                    CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "common", "deploy-dashboard.sh"));
                    string dashboardProject = Path.Combine(tempRepo, "src", "Armada.Dashboard");
                    Directory.CreateDirectory(Path.Combine(dashboardProject, "node_modules"));
                    File.WriteAllText(Path.Combine(dashboardProject, "package.json"), "{}");
                    string distDirectory = Path.Combine(dashboardProject, "dist");
                    Directory.CreateDirectory(Path.Combine(distDirectory, "assets"));
                    File.WriteAllText(Path.Combine(distDirectory, "index.html"), "dist-index");
                    File.WriteAllText(Path.Combine(distDirectory, "assets", "app.js"), "dist-asset");
                    if (nodeAvailable)
                    {
                        string npmPath = Path.Combine(tools, "npm");
                        File.WriteAllText(npmPath,
                            "#!/bin/bash\n" +
                            "set -eu\n" +
                            "[ \"${1:-}\" = run ] || exit 2\n" +
                            "shift\n" +
                            "[ \"${1:-}\" = build ] || exit 2\n" +
                            "shift\n" +
                            "while [ \"$#\" -gt 0 ]; do\n" +
                            "  if [ \"$1\" = --outDir ]; then shift; output=\"$1\"; break; fi\n" +
                            "  shift\n" +
                            "done\n" +
                            "[ -n \"${output:-}\" ] || exit 3\n" +
                            "mkdir -p \"$output/assets\"\n" +
                            "printf build-index > \"$output/index.html\"\n" +
                            "printf build-asset > \"$output/assets/app.js\"\n");
                        File.SetUnixFileMode(npmPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    if (!nodeAvailable)
                    {
                        string[] commands = { "bash", "dirname", "mktemp", "mkdir", "cp", "rm", "mv", "rmdir", "chmod" };
                        foreach (string command in commands)
                        {
                            string? commandPath = FindExecutable(command);
                            if (commandPath == null) throw new InvalidOperationException("Required shell command was not found: " + command);
                            File.CreateSymbolicLink(Path.Combine(tools, command), commandPath);
                        }
                    }
                }
                string publishPath = Path.Combine(scripts, "publish-server.sh");
                string dotnetPath = Path.Combine(tools, "dotnet");
                string deployPath = Path.Combine(scripts, "deploy-dashboard.sh");
                string dashboardDirectory = Path.Combine(home, ".armada", "dashboard");

                File.WriteAllText(dotnetPath,
                    "#!/bin/bash\n" +
                    "set -eu\n" +
                    "[ \"${DOTNET_CREATE_SERVER:-1}\" = 1 ] || exit 0\n" +
                    "output=''\n" +
                    "while [ \"$#\" -gt 0 ]; do\n" +
                    "  if [ \"$1\" = -o ]; then shift; output=\"$1\"; fi\n" +
                    "  shift\n" +
                    "done\n" +
                    "[ -n \"$output\" ] || exit 2\n" +
                    "mkdir -p \"$output\"\n" +
                    ": > \"$output/Armada.Server\"\n");
                File.SetUnixFileMode(dotnetPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                if (!useRealDashboardDeploy)
                {
                    File.WriteAllText(deployPath,
                        "#!/bin/bash\n" +
                        "set -eu\n" +
                        "[ \"${DASHBOARD_DEPLOY_SUCCEEDS:-0}\" = 1 ] || exit 1\n" +
                        "mkdir -p \"$HOME/.armada/dashboard\"\n" +
                        "printf new-dashboard > \"$HOME/.armada/dashboard/index.html\"\n");
                    File.SetUnixFileMode(deployPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                else if (failDashboardCopy)
                {
                    string cpPath = Path.Combine(tools, "cp");
                    File.WriteAllText(cpPath,
                        "#!/bin/bash\n" +
                        "set -eu\n" +
                        "source=\"$2\"\n" +
                        "destination=\"$3\"\n" +
                        "mkdir -p \"$destination\"\n" +
                        "/bin/cp -R \"${source%/.}/index.html\" \"$destination/\"\n" +
                        "exit 1\n");
                    File.SetUnixFileMode(cpPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                if (existingDashboard)
                {
                    Directory.CreateDirectory(dashboardDirectory);
                    Directory.CreateDirectory(Path.Combine(dashboardDirectory, "assets"));
                    File.WriteAllText(Path.Combine(dashboardDirectory, "index.html"), useRealDashboardDeploy ? "old-index" : "existing-dashboard");
                    if (useRealDashboardDeploy) File.WriteAllText(Path.Combine(dashboardDirectory, "assets", "app.js"), "old-asset");
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    WorkingDirectory = tempRepo,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                startInfo.ArgumentList.Add(publishPath);
                startInfo.Environment["HOME"] = home;
                string inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
                startInfo.Environment["PATH"] = useRealDashboardDeploy && !nodeAvailable
                    ? tools
                    : tools + Path.PathSeparator + inheritedPath;
                startInfo.Environment["DOTNET_CREATE_SERVER"] = createServer ? "1" : "0";
                startInfo.Environment["DASHBOARD_DEPLOY_SUCCEEDS"] = dashboardDeploySucceeds ? "1" : "0";
                startInfo.Environment["TMPDIR"] = temp;

                int publishRuns = useRealDashboardDeploy && dashboardDeploySucceeds && !failDashboardCopy ? 2 : 1;
                int exitCode = 0;
                List<string> output = new List<string>();
                for (int run = 0; run < publishRuns; run++)
                {
                    using (Process process = new Process { StartInfo = startInfo })
                    {
                        process.Start();
                        string standardOutput = process.StandardOutput.ReadToEnd();
                        string standardError = process.StandardError.ReadToEnd();
                        process.WaitForExit();
                        exitCode = process.ExitCode;
                        output.Add(standardOutput + standardError);
                        if (exitCode != 0) break;
                    }
                }

                string dashboardContent = ReadDashboardSnapshot(dashboardDirectory);
                string armadaRoot = Path.Combine(home, ".armada");
                int stagingCount = Directory.Exists(armadaRoot) ? Directory.GetDirectories(armadaRoot, ".dashboard-stage.*").Length : 0;
                bool deployLockExists = Directory.Exists(Path.Combine(armadaRoot, ".dashboard-deploy.lock"));
                return new PosixPublishResult(exitCode, String.Join(Environment.NewLine, output), dashboardContent, output.Count, stagingCount, deployLockExists);
            }
            finally
            {
                TestTemp.TryDelete(tempRoot);
            }
        }

        private sealed class PosixPublishResult
        {
            public PosixPublishResult(int exitCode, string output, string dashboardContent, int publishRuns, int stagingDirectoryCount, bool deployLockExists)
            {
                ExitCode = exitCode;
                Output = output;
                DashboardContent = dashboardContent;
                PublishRuns = publishRuns;
                StagingDirectoryCount = stagingDirectoryCount;
                DeployLockExists = deployLockExists;
            }

            public int ExitCode { get; }
            public string Output { get; }
            public string DashboardContent { get; }
            public int PublishRuns { get; }
            public int StagingDirectoryCount { get; }
            public bool DeployLockExists { get; }
        }

        private static string ReadDashboardSnapshot(string directory)
        {
            if (!Directory.Exists(directory)) return String.Empty;

            List<string> files = new List<string>(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
            files.Sort(StringComparer.Ordinal);
            List<string> entries = new List<string>();
            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
                entries.Add(relative + "=" + File.ReadAllText(file));
            }
            return String.Join("\n", entries);
        }

        private static ConcurrentDashboardDeployResult RunConcurrentDashboardDeployScenario()
        {
            string repoRoot = FindRepositoryRoot();
            string tempRoot = TestTemp.NewDirectory("dashboard-deploy-concurrent");
            string tempRepo = Path.Combine(tempRoot, "repo");
            string home = Path.Combine(tempRoot, "home");
            string tools = Path.Combine(tempRoot, "tools");
            string temp = Path.Combine(tempRoot, "tmp");
            string scripts = Path.Combine(tempRepo, "scripts", "common");
            string project = Path.Combine(tempRepo, "src", "Armada.Dashboard");
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(tools);
            Directory.CreateDirectory(temp);
            Directory.CreateDirectory(Path.Combine(project, "node_modules"));

            Process? first = null;
            string releasePath = Path.Combine(tempRoot, "release-first-build");
            string startedPath = Path.Combine(tempRoot, "first-build-started");
            string npmLog = Path.Combine(tempRoot, "npm-runs.log");
            string dashboardDirectory = Path.Combine(home, ".armada", "dashboard");
            try
            {
                CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "common", "deploy-dashboard.sh"));
                CopyRepositoryFile(repoRoot, tempRepo, Path.Combine("scripts", "common", "resolve-insecure.sh"));
                File.WriteAllText(Path.Combine(project, "package.json"), "{}");
                string nodePath = Path.Combine(tools, "node");
                File.WriteAllText(nodePath, "#!/bin/bash\nexit 0\n");
                File.SetUnixFileMode(nodePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                string npmPath = Path.Combine(tools, "npm");
                File.WriteAllText(npmPath,
                    "#!/bin/bash\n" +
                    "set -eu\n" +
                    "printf '%s\\n' \"$DASHBOARD_BUILD_NAME\" >> \"$NPM_LOG\"\n" +
                    "if [ \"$DASHBOARD_BUILD_NAME\" = first-build ]; then\n" +
                    "  : > \"$BUILD_STARTED\"\n" +
                    "  while [ ! -f \"$BUILD_RELEASE\" ]; do sleep 0.02; done\n" +
                    "fi\n" +
                    "[ \"${1:-}\" = run ] || exit 2\n" +
                    "shift\n" +
                    "[ \"${1:-}\" = build ] || exit 2\n" +
                    "shift\n" +
                    "while [ \"$#\" -gt 0 ]; do\n" +
                    "  if [ \"$1\" = --outDir ]; then shift; output=\"$1\"; break; fi\n" +
                    "  shift\n" +
                    "done\n" +
                    "[ -n \"${output:-}\" ] || exit 3\n" +
                    "mkdir -p \"$output\"\n" +
                    "printf '%s' \"$DASHBOARD_BUILD_NAME\" > \"$output/index.html\"\n");
                File.SetUnixFileMode(npmPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                ProcessStartInfo firstInfo = CreateDashboardDeployStartInfo(scripts, home, tools, temp);
                firstInfo.Environment["DASHBOARD_BUILD_NAME"] = "first-build";
                firstInfo.Environment["BUILD_STARTED"] = startedPath;
                firstInfo.Environment["BUILD_RELEASE"] = releasePath;
                firstInfo.Environment["NPM_LOG"] = npmLog;
                first = new Process { StartInfo = firstInfo };
                first.Start();

                DateTime waitUntilUtc = DateTime.UtcNow.AddSeconds(10);
                while (!File.Exists(startedPath) && DateTime.UtcNow < waitUntilUtc) Thread.Sleep(20);
                if (!File.Exists(startedPath)) throw new Exception("The first dashboard build did not reach the held build step.");

                ProcessStartInfo secondInfo = CreateDashboardDeployStartInfo(scripts, home, tools, temp);
                secondInfo.Environment["DASHBOARD_BUILD_NAME"] = "second-build";
                secondInfo.Environment["BUILD_STARTED"] = startedPath;
                secondInfo.Environment["BUILD_RELEASE"] = releasePath;
                secondInfo.Environment["NPM_LOG"] = npmLog;
                using (Process second = new Process { StartInfo = secondInfo })
                {
                    second.Start();
                    string secondOutput = second.StandardOutput.ReadToEnd() + second.StandardError.ReadToEnd();
                    second.WaitForExit();

                    File.WriteAllText(releasePath, "release");
                    string firstOutput = first.StandardOutput.ReadToEnd() + first.StandardError.ReadToEnd();
                    first.WaitForExit();

                    string dashboardContent = ReadDashboardSnapshot(dashboardDirectory);
                    int buildCount = File.Exists(npmLog) ? File.ReadAllLines(npmLog).Length : 0;
                    bool lockExists = Directory.Exists(Path.Combine(home, ".armada", ".dashboard-deploy.lock"));
                    return new ConcurrentDashboardDeployResult(second.ExitCode, secondOutput, first.ExitCode, firstOutput, buildCount, dashboardContent, lockExists);
                }
            }
            finally
            {
                File.WriteAllText(releasePath, "release");
                if (first != null)
                {
                    if (!first.HasExited)
                    {
                        if (!first.WaitForExit(10000)) first.Kill(entireProcessTree: true);
                    }
                    first.Dispose();
                }
                TestTemp.TryDelete(tempRoot);
            }
        }

        private static ProcessStartInfo CreateDashboardDeployStartInfo(string scriptsDirectory, string home, string tools, string temp)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(scriptsDirectory)!))!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(Path.Combine(scriptsDirectory, "deploy-dashboard.sh"));
            startInfo.Environment["HOME"] = home;
            startInfo.Environment["PATH"] = tools + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? String.Empty);
            startInfo.Environment["TMPDIR"] = temp;
            return startInfo;
        }

        private sealed class ConcurrentDashboardDeployResult
        {
            public ConcurrentDashboardDeployResult(int secondExitCode, string secondOutput, int firstExitCode, string firstOutput, int buildCount, string dashboardContent, bool lockExists)
            {
                SecondExitCode = secondExitCode;
                SecondOutput = secondOutput;
                FirstExitCode = firstExitCode;
                FirstOutput = firstOutput;
                BuildCount = buildCount;
                DashboardContent = dashboardContent;
                LockExists = lockExists;
            }

            public int SecondExitCode { get; }
            public string SecondOutput { get; }
            public int FirstExitCode { get; }
            public string FirstOutput { get; }
            public int BuildCount { get; }
            public string DashboardContent { get; }
            public bool LockExists { get; }
        }

        private static string? FindExecutable(string name)
        {
            string searchPath = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
            foreach (string directory in searchPath.Split(Path.PathSeparator))
            {
                if (String.IsNullOrWhiteSpace(directory)) continue;
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string RunCommandAndCaptureOutput(string fileName, string arguments, string workingDirectory, IDictionary<string, string>? environmentVariables = null)
        {
            using Process process = new Process();
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (environmentVariables != null)
            {
                foreach (KeyValuePair<string, string> variable in environmentVariables)
                {
                    startInfo.Environment[variable.Key] = variable.Value;
                }
            }

            process.StartInfo = startInfo;

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

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body, bool skip = false, string? skipReason = null)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.ReleaseVersion",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag },
                skip: skip,
                skipReason: skipReason);
        }

        #endregion
    }
}
