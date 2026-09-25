namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using Armada.Core;
    using Armada.Test.Common;

    public class ReleaseVersionTests : TestSuite
    {
        public override string Name => "Release Version";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Source MCP Helpers Use Net10 Framework", () =>
            {
                string mcpConfigHelperContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Armada.Helm", "Commands", "McpConfigHelper.cs"));
                string installMcpBatContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "windows", "install-mcp.bat"));
                string installMcpShContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "common", "install-mcp.sh"));
                string removeMcpBatContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "windows", "remove-mcp.bat"));
                string removeMcpShContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "common", "remove-mcp.sh"));
                string resolveFrameworkBatContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "windows", "resolve-framework.bat"));
                string resolveFrameworkShContents = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "common", "resolve-framework.sh"));

                AssertContains("private const string SourceMcpFramework = \"net10.0\";", mcpConfigHelperContents, "McpConfigHelper should pin source MCP installs to net10.0");
                AssertFalse(mcpConfigHelperContents.Contains("\"net8.0\""), "McpConfigHelper should not pin source MCP installs to net8.0");
                AssertContains("ARMADA_TARGET_FRAMEWORK=\"${ARMADA_TARGET_FRAMEWORK:-net10.0}\"", resolveFrameworkShContents, "The shell framework resolver should default to net10.0");
                if (OperatingSystem.IsWindows())
                {
                    AssertContains("export ARMADA_DOTNET_MSBUILD_FRAMEWORK_ARGS=", resolveFrameworkShContents, "The shell framework resolver should expose reusable msbuild framework arguments");
                }
                else
                {
                    // Source the resolver the way the scripts do and read what it exports.
                    string resolveFrameworkShPath = Path.Combine(FindRepositoryRoot(), "scripts", "common", "resolve-framework.sh");
                    string printMsbuildArgs = "-c \". \\\"$0\\\"; armada_resolve_framework \\\"$@\\\"; printf '%s' \\\"$ARMADA_DOTNET_MSBUILD_FRAMEWORK_ARGS\\\"\" \"" + resolveFrameworkShPath + "\"";
                    Dictionary<string, string> noFrameworkOverride = new Dictionary<string, string> { ["ARMADA_TARGET_FRAMEWORK"] = "" };
                    AssertEqual("-p:TargetFramework=net10.0 -p:TargetFrameworks=net10.0",
                        RunCommandAndCaptureOutput("bash", printMsbuildArgs, FindRepositoryRoot(), noFrameworkOverride),
                        "The shell framework resolver should export msbuild framework arguments for the default framework");
                    AssertEqual("-p:TargetFramework=net8.0 -p:TargetFrameworks=net8.0",
                        RunCommandAndCaptureOutput("bash", printMsbuildArgs + " --framework net8.0", FindRepositoryRoot(), noFrameworkOverride),
                        "The shell framework resolver should export msbuild framework arguments for an explicit framework");
                }
                AssertContains("armada_resolve_framework \"$@\"", installMcpShContents, "install-mcp.sh should resolve the framework override");
                AssertContains("-f \"${ARMADA_TARGET_FRAMEWORK}\" -- mcp install --yes", installMcpShContents, "install-mcp.sh should honor the resolved framework");
                AssertContains("armada_resolve_framework \"$@\"", removeMcpShContents, "remove-mcp.sh should resolve the framework override");
                AssertContains("-f \"${ARMADA_TARGET_FRAMEWORK}\" -- mcp remove --yes", removeMcpShContents, "remove-mcp.sh should honor the resolved framework");
                AssertContains("set \"FRAMEWORK=net10.0\"", resolveFrameworkBatContents, "Windows framework resolver should default to net10.0");
                AssertContains("set \"ARMADA_FORWARD_FRAMEWORK_ARGS=%FORWARD_ARGS%\"", resolveFrameworkBatContents, "Windows framework resolver should expose reusable wrapper forwarding arguments");
                AssertContains("set \"ARMADA_DOTNET_FRAMEWORK_ARGS=%DOTNET_FRAMEWORK_ARGS%\"", resolveFrameworkBatContents, "Windows framework resolver should expose reusable dotnet framework arguments");
                AssertContains("set \"ARMADA_DOTNET_MSBUILD_FRAMEWORK_ARGS=%DOTNET_MSBUILD_FRAMEWORK_ARGS%\"", resolveFrameworkBatContents, "Windows framework resolver should expose reusable msbuild framework arguments");
                AssertContains("call \"%SCRIPT_DIR%\\resolve-framework.bat\" %*", installMcpBatContents, "install-mcp.bat should resolve the Windows framework override");
                AssertContains("%ARMADA_DOTNET_FRAMEWORK_ARGS% -- mcp install --yes", installMcpBatContents, "install-mcp.bat should honor the resolved framework override");
                AssertContains("call \"%SCRIPT_DIR%\\resolve-framework.bat\" %*", removeMcpBatContents, "remove-mcp.bat should resolve the Windows framework override");
                AssertContains("%ARMADA_DOTNET_FRAMEWORK_ARGS% -- mcp remove --yes", removeMcpBatContents, "remove-mcp.bat should honor the resolved framework override");
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
    }
}
