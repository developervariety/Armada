namespace Armada.Helm.Commands
{
    using System.IO;
    using System.Diagnostics;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using Armada.Core.Services;

    internal static class McpConfigHelper
    {
        internal sealed record ConfigTarget(
            string ClientName,
            string FilePath,
            JsonObject? ArmadaConfig = null,
            bool IsProjectScoped = false,
            bool InstallAgent = false,
            string? CliCommand = null,
            string[]? InstallArgs = null,
            string[]? RemoveArgs = null,
            string? RemoveBeforeInstallName = null,
            string? ManualInstallCommand = null,
            string? ManualRemoveCommand = null,
            int? StartupTimeoutSeconds = null,
            bool IsMuxServers = false,
            bool IsOpenCodeConfig = false);

        internal sealed record ApplyResult(string ClientName, string FilePath, bool Changed, string Message, bool IsProjectScoped = false);
        internal sealed record InstructionTarget(string ClientName, string FilePath, string Content, bool IsProjectScoped = false);

        internal static JsonSerializerOptions JsonOptions { get; } = new JsonSerializerOptions { WriteIndented = true };
        private const string ManagedBlockStart = "<!-- armada:mcp:begin -->";
        private const string ManagedBlockEnd = "<!-- armada:mcp:end -->";
        private const string SourceMcpFramework = "net10.0";
        private const int CodexMcpStartupTimeoutSeconds = 120;

        internal static string GetMcpUrl(int mcpPort)
        {
            // Armada serves the modern MCP Streamable HTTP transport at /mcp (POST for JSON-RPC,
            // GET for the SSE notification stream, DELETE to terminate the session). This is the
            // endpoint modern MCP clients (Claude Code, Gemini, Cursor, Mux) expect. The legacy
            // The legacy transport is not advertised by Helm.
            return $"http://localhost:{mcpPort}/mcp";
        }

        internal static string GetMcpHttpUrl(int mcpPort) => GetMcpUrl(mcpPort);

        internal static string GetClaudeJsonPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        }

        internal static string GetClaudeAgentPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "agents", "armada.md");
        }

        internal static string GetOpenCodeConfigPath()
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode");
            string jsonc = Path.Combine(directory, "opencode.jsonc");
            return File.Exists(jsonc) ? jsonc : Path.Combine(directory, "opencode.json");
        }

        internal static bool IsOpenCodeAvailable()
        {
            return File.Exists(GetOpenCodeConfigPath()) || Directory.Exists(Path.GetDirectoryName(GetOpenCodeConfigPath())!);
        }

        internal static string GetCodexConfigPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        }

        internal static string GetProjectAgentsPath()
        {
            return Path.Combine(Environment.CurrentDirectory, "AGENTS.md");
        }

        internal static string GetProjectGeminiInstructionsPath()
        {
            return Path.Combine(Environment.CurrentDirectory, "GEMINI.md");
        }

        internal static string GetGeminiConfigPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "settings.json");
        }

        internal static string GetCursorConfigPath()
        {
            return Path.Combine(Environment.CurrentDirectory, ".cursor", "mcp.json");
        }

        /// <summary>
        /// Resolve Mux's config directory: the MUX_CONFIG_DIR environment variable if set, otherwise ~/.mux.
        /// </summary>
        internal static string GetMuxConfigDirectory()
        {
            string? envDir = Environment.GetEnvironmentVariable(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable);
            if (!String.IsNullOrWhiteSpace(envDir))
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(envDir.Trim()));

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mux");
        }

        /// <summary>
        /// Path to Mux's MCP servers file (mcp-servers.json) inside the active Mux config directory.
        /// </summary>
        internal static string GetMuxMcpServersPath()
        {
            return Path.Combine(GetMuxConfigDirectory(), "mcp-servers.json");
        }

        /// <summary>
        /// Resolve a Mux executable on PATH, or null if none is found.
        /// </summary>
        internal static string? ResolveMuxExecutable()
        {
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (String.IsNullOrEmpty(pathEnv))
                return null;

            string[] names = OperatingSystem.IsWindows()
                ? new[] { "mux.exe", "mux.cmd", "mux.bat", "mux" }
                : new[] { "mux" };

            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                if (String.IsNullOrWhiteSpace(dir))
                    continue;

                foreach (string name in names)
                {
                    try
                    {
                        string candidate = Path.Combine(dir.Trim(), name);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Whether Mux appears to be installed on this machine, so `armada mcp install` can configure it.
        /// True when Mux's config directory already exists (created on Mux's first run) or a Mux
        /// executable is resolvable on PATH.
        /// </summary>
        internal static bool IsMuxAvailable()
        {
            try
            {
                if (Directory.Exists(GetMuxConfigDirectory()))
                    return true;
            }
            catch (Exception)
            {
                return ResolveMuxExecutable() != null;
            }

            return ResolveMuxExecutable() != null;
        }

        /// <summary>
        /// Environment variable an installed MCP client reads its Armada API key from. The endpoint refuses a
        /// request without a credential, so each client entry references this variable by name in that client's
        /// own expansion syntax and no key value is written to a configuration file.
        /// </summary>
        internal const string ApiKeyEnvironmentVariable = "ARMADA_API_KEY";

        /// <summary>
        /// Request header that carries the Armada API key.
        /// </summary>
        internal const string ApiKeyHeaderName = "X-Api-Key";

        /// <summary>
        /// Header value for clients that expand <c>${VAR}</c> (Claude Code, Gemini CLI).
        /// </summary>
        internal static readonly string ApiKeyForDollarBraceExpansion = "${" + ApiKeyEnvironmentVariable + "}";

        /// <summary>
        /// Header value for Cursor, which expands <c>${env:VAR}</c>.
        /// </summary>
        internal static readonly string ApiKeyForCursorExpansion = "${env:" + ApiKeyEnvironmentVariable + "}";

        /// <summary>
        /// Header value for OpenCode, which expands <c>{env:VAR}</c>.
        /// </summary>
        internal static readonly string ApiKeyForOpenCodeExpansion = "{env:" + ApiKeyEnvironmentVariable + "}";

        internal static List<ConfigTarget> BuildTargets(int mcpPort)
        {
            string mcpUrl = GetMcpUrl(mcpPort);
            string codexCommand = ResolveCliCommand("codex");
            string geminiCommand = ResolveCliCommand("gemini");

            List<ConfigTarget> targets = new List<ConfigTarget>
            {
                new(
                    "Claude Code",
                    GetClaudeJsonPath(),
                    new JsonObject
                    {
                        ["type"] = "http",
                        ["url"] = mcpUrl,
                        ["headers"] = new JsonObject { [ApiKeyHeaderName] = ApiKeyForDollarBraceExpansion },
                    },
                    InstallAgent: true,
                    ManualInstallCommand: BuildClaudeCliCommand(mcpPort)),
                new(
                    "Codex",
                    GetCodexConfigPath(),
                    CliCommand: codexCommand,
                    InstallArgs: BuildCodexInstallArgs(),
                    RemoveArgs: new[] { "mcp", "remove", "armada" },
                    RemoveBeforeInstallName: "armada",
                    ManualInstallCommand: BuildCodexManualInstallCommand(codexCommand),
                    ManualRemoveCommand: codexCommand + " mcp remove armada",
                    StartupTimeoutSeconds: CodexMcpStartupTimeoutSeconds),
                new(
                    "Gemini CLI",
                    GetGeminiConfigPath(),
                    CliCommand: geminiCommand,
                    InstallArgs: new[] { "mcp", "add", "--scope", "user", "--transport", "http", "--header", ApiKeyHeaderName + ": " + ApiKeyForDollarBraceExpansion, "armada", mcpUrl },
                    RemoveArgs: new[] { "mcp", "remove", "armada" },
                    ManualInstallCommand: geminiCommand + " mcp add --scope user --transport http --header '" + ApiKeyHeaderName + ": " + ApiKeyForDollarBraceExpansion + "' armada " + mcpUrl,
                    ManualRemoveCommand: geminiCommand + " mcp remove armada"),
                new(
                    "Cursor",
                    GetCursorConfigPath(),
                    new JsonObject
                    {
                        ["url"] = mcpUrl,
                        ["transport"] = "http",
                        ["headers"] = new JsonObject { [ApiKeyHeaderName] = ApiKeyForCursorExpansion },
                    },
                    IsProjectScoped: true),
            };

            // Mux stores MCP servers as an array in mcp-servers.json (a different shape from the other
            // clients). Only offer it when Mux is actually present so `armada mcp install` does not
            // create a stray ~/.mux directory on machines without Mux.
            if (IsMuxAvailable())
            {
                targets.Add(new(
                    "Mux",
                    GetMuxMcpServersPath(),
                    new JsonObject
                    {
                        ["name"] = "armada",
                        ["transport"] = "http",
                        ["url"] = $"http://localhost:{mcpPort}",
                        ["mcpPath"] = "/mcp",
                        // Mux reads an HTTP server's credential from its auth object and expands ${VAR}.
                        ["auth"] = new JsonObject
                        {
                            ["scheme"] = "api_key",
                            ["key"] = ApiKeyForDollarBraceExpansion,
                            ["headerName"] = ApiKeyHeaderName,
                        },
                    },
                    IsMuxServers: true));
            }

            if (IsOpenCodeAvailable())
            {
                targets.Add(new(
                    "OpenCode",
                    GetOpenCodeConfigPath(),
                    new JsonObject
                    {
                        ["type"] = "remote",
                        ["url"] = mcpUrl,
                        ["headers"] = new JsonObject { [ApiKeyHeaderName] = ApiKeyForOpenCodeExpansion },
                    },
                    IsOpenCodeConfig: true));
            }

            return targets;
        }

        internal static List<InstructionTarget> BuildInstructionTargets()
        {
            return new List<InstructionTarget>
            {
                new(
                    "Codex/Cursor Rules",
                    GetProjectAgentsPath(),
                    GenerateAgentsInstructions(),
                    IsProjectScoped: true),
                new(
                    "Gemini CLI Instructions",
                    GetProjectGeminiInstructionsPath(),
                    GenerateGeminiInstructions(),
                    IsProjectScoped: true),
            };
        }

        internal static async Task<ApplyResult> InstallTargetAsync(ConfigTarget target)
        {
            if (target.IsMuxServers)
                return await InstallMuxServerAsync(target).ConfigureAwait(false);

            if (target.IsOpenCodeConfig)
                return await InstallOpenCodeAsync(target).ConfigureAwait(false);

            if (!String.IsNullOrEmpty(target.CliCommand) && target.InstallArgs != null)
            {
                if (!String.IsNullOrEmpty(target.RemoveBeforeInstallName))
                    await RunCliCommandAsync(target.CliCommand, new[] { "mcp", "remove", target.RemoveBeforeInstallName }).ConfigureAwait(false);

                bool success = await RunCliCommandAsync(target.CliCommand, target.InstallArgs).ConfigureAwait(false);
                bool timeoutChanged = success && target.StartupTimeoutSeconds.HasValue
                    && await EnsureTomlMcpServerStartupTimeoutAsync(target.FilePath, "armada", target.StartupTimeoutSeconds.Value).ConfigureAwait(false);
                return new ApplyResult(
                    target.ClientName,
                    target.FilePath,
                    success,
                    success || timeoutChanged
                        ? "Configured Armada MCP entry via native CLI."
                        : "Failed to configure Armada MCP entry via native CLI.",
                    target.IsProjectScoped);
            }

            if (target.ArmadaConfig == null)
                throw new InvalidOperationException("Config target does not define ArmadaConfig for file-based installation.");

            Directory.CreateDirectory(Path.GetDirectoryName(target.FilePath)!);
            JsoncFile file = File.Exists(target.FilePath) ? await ReadJsoncFileAsync(target.FilePath).ConfigureAwait(false) : new JsoncFile(String.Empty, false);
            string original = file.Content;
            if (!String.IsNullOrWhiteSpace(original)) ValidateJsoncObject(original);
            string updated = JsoncScopedEditor.Upsert(original, "mcpServers", "armada", target.ArmadaConfig.ToJsonString(JsonOptions), out bool changed);
            if (changed)
            {
                ValidateJsoncObject(updated);
                await WriteJsoncFileAsync(target.FilePath, updated, file.HasUtf8Bom).ConfigureAwait(false);
            }

            return new ApplyResult(
                target.ClientName,
                target.FilePath,
                changed,
                changed ? "Configured Armada MCP entry." : "Armada MCP entry already matched the expected configuration.",
                target.IsProjectScoped);
        }

        internal static async Task<ApplyResult> RemoveTargetAsync(ConfigTarget target)
        {
            if (target.IsMuxServers)
                return await RemoveMuxServerAsync(target).ConfigureAwait(false);

            if (target.IsOpenCodeConfig)
                return await RemoveOpenCodeAsync(target).ConfigureAwait(false);

            if (!String.IsNullOrEmpty(target.CliCommand) && target.RemoveArgs != null)
            {
                bool success = await RunCliCommandAsync(target.CliCommand, target.RemoveArgs).ConfigureAwait(false);
                return new ApplyResult(
                    target.ClientName,
                    target.FilePath,
                    success,
                    success
                        ? "Removed Armada MCP entry via native CLI."
                        : "No Armada MCP entry was present or the native CLI is unavailable.",
                    target.IsProjectScoped);
            }

            if (!File.Exists(target.FilePath))
            {
                return new ApplyResult(
                    target.ClientName,
                    target.FilePath,
                    false,
                    "Configuration file does not exist; nothing to remove.",
                    target.IsProjectScoped);
            }

            JsoncFile file = await ReadJsoncFileAsync(target.FilePath).ConfigureAwait(false);
            string original = file.Content;
            if (!String.IsNullOrWhiteSpace(original)) ValidateJsoncObject(original);
            string updated = JsoncScopedEditor.Remove(original, "mcpServers", "armada", out bool changed);
            if (changed)
            {
                ValidateJsoncObject(updated);
                await WriteJsoncFileAsync(target.FilePath, updated, file.HasUtf8Bom).ConfigureAwait(false);
            }

            return new ApplyResult(
                target.ClientName,
                target.FilePath,
                changed,
                changed ? "Removed Armada MCP entry." : "No Armada MCP entry was present.",
                target.IsProjectScoped);
        }

        /// <summary>
        /// Install the Armada HTTP server into Mux's mcp-servers.json, which stores servers as a
        /// "servers" array of objects (each carrying its own "name"), unlike the keyed "mcpServers"
        /// object used by the other clients.
        /// </summary>
        private static async Task<ApplyResult> InstallMuxServerAsync(ConfigTarget target)
        {
            if (target.ArmadaConfig == null)
                throw new InvalidOperationException("Mux config target does not define ArmadaConfig.");

            Directory.CreateDirectory(Path.GetDirectoryName(target.FilePath)!);
            JsoncFile file = File.Exists(target.FilePath) ? await ReadJsoncFileAsync(target.FilePath).ConfigureAwait(false) : new JsoncFile(String.Empty, false);
            string original = file.Content;
            if (!String.IsNullOrWhiteSpace(original)) ValidateJsoncObject(original);
            string updated = JsoncScopedEditor.UpsertArrayObject(original, "servers", "armada", target.ArmadaConfig.ToJsonString(JsonOptions), out bool changed);
            if (changed)
            {
                ValidateJsoncObject(updated);
                await WriteJsoncFileAsync(target.FilePath, updated, file.HasUtf8Bom).ConfigureAwait(false);
            }

            return new ApplyResult(
                target.ClientName,
                target.FilePath,
                changed,
                changed ? "Configured Armada MCP server (HTTP)." : "Armada MCP server already matched the expected configuration.",
                target.IsProjectScoped);
        }

        /// <summary>
        /// Remove the Armada server entry from Mux's mcp-servers.json "servers" array.
        /// </summary>
        private static async Task<ApplyResult> RemoveMuxServerAsync(ConfigTarget target)
        {
            if (!File.Exists(target.FilePath))
            {
                return new ApplyResult(
                    target.ClientName,
                    target.FilePath,
                    false,
                    "Configuration file does not exist; nothing to remove.",
                    target.IsProjectScoped);
            }

            JsoncFile file = await ReadJsoncFileAsync(target.FilePath).ConfigureAwait(false);
            string original = file.Content;
            if (!String.IsNullOrWhiteSpace(original)) ValidateJsoncObject(original);
            string updated = JsoncScopedEditor.RemoveArrayObject(original, "servers", "armada", out bool changed);
            if (changed)
            {
                ValidateJsoncObject(updated);
                await WriteJsoncFileAsync(target.FilePath, updated, file.HasUtf8Bom).ConfigureAwait(false);
            }

            return new ApplyResult(
                target.ClientName,
                target.FilePath,
                changed,
                changed ? "Removed Armada MCP server." : "No Armada MCP server was present.",
                target.IsProjectScoped);
        }

        internal static async Task<ApplyResult> InstallClaudeAgentAsync()
        {
            string agentPath = GetClaudeAgentPath();
            Directory.CreateDirectory(Path.GetDirectoryName(agentPath)!);
            string content = GenerateAgentDefinition();
            bool changed = !File.Exists(agentPath) || !string.Equals(await File.ReadAllTextAsync(agentPath).ConfigureAwait(false), content, StringComparison.Ordinal);
            await File.WriteAllTextAsync(agentPath, content).ConfigureAwait(false);
            return new ApplyResult("Claude Code Agent", agentPath, changed, changed ? "Installed/updated armada agent definition." : "Armada agent definition already up to date.");
        }

        internal static async Task<ApplyResult> InstallInstructionTargetAsync(InstructionTarget target)
        {
            string managedContent = WrapManagedBlock(target.Content);
            string filePath = target.FilePath;
            string? existing = File.Exists(filePath) ? await File.ReadAllTextAsync(filePath).ConfigureAwait(false) : null;
            string updated = UpsertManagedBlock(existing, managedContent);
            bool changed = !String.Equals(existing ?? String.Empty, updated, StringComparison.Ordinal);

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllTextAsync(filePath, updated).ConfigureAwait(false);

            return new ApplyResult(
                target.ClientName,
                filePath,
                changed,
                changed
                    ? "Installed/updated Armada orchestration instructions."
                    : "Armada orchestration instructions already up to date.",
                target.IsProjectScoped);
        }

        internal static Task<ApplyResult> RemoveClaudeAgentAsync()
        {
            string agentPath = GetClaudeAgentPath();
            if (!File.Exists(agentPath))
            {
                return Task.FromResult(new ApplyResult("Claude Code Agent", agentPath, false, "Agent file does not exist; nothing to remove."));
            }

            File.Delete(agentPath);
            return Task.FromResult(new ApplyResult("Claude Code Agent", agentPath, true, "Removed armada agent definition."));
        }

        internal static async Task<ApplyResult> RemoveInstructionTargetAsync(InstructionTarget target)
        {
            string filePath = target.FilePath;
            if (!File.Exists(filePath))
            {
                return new ApplyResult(target.ClientName, filePath, false, "Instruction file does not exist; nothing to remove.", target.IsProjectScoped);
            }

            string existing = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            string updated = RemoveManagedBlock(existing);
            if (String.Equals(existing, updated, StringComparison.Ordinal))
            {
                return new ApplyResult(target.ClientName, filePath, false, "No Armada-managed instructions were present.", target.IsProjectScoped);
            }

            if (String.IsNullOrWhiteSpace(updated))
            {
                File.Delete(filePath);
                return new ApplyResult(target.ClientName, filePath, true, "Removed Armada-managed instructions and deleted the empty file.", target.IsProjectScoped);
            }

            await File.WriteAllTextAsync(filePath, updated).ConfigureAwait(false);
            return new ApplyResult(target.ClientName, filePath, true, "Removed Armada-managed instructions.", target.IsProjectScoped);
        }

        internal static string BuildManualSnippet(ConfigTarget target)
        {
            if (!String.IsNullOrEmpty(target.ManualInstallCommand))
                return target.ManualInstallCommand;

            if (target.ArmadaConfig == null)
                return "";

            if (target.IsMuxServers)
            {
                JsonObject muxRoot = new JsonObject
                {
                    ["servers"] = new JsonArray(target.ArmadaConfig.DeepClone()),
                };
                return muxRoot.ToJsonString(JsonOptions);
            }

            JsonObject root = new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["armada"] = target.ArmadaConfig.DeepClone(),
                },
            };
            return root.ToJsonString(JsonOptions);
        }

        internal static string BuildManualRemoveSnippet(ConfigTarget target)
        {
            if (!String.IsNullOrEmpty(target.ManualRemoveCommand))
                return target.ManualRemoveCommand;

            if (target.IsMuxServers)
                return "Remove the object with \"name\": \"armada\" from the \"servers\" array in mcp-servers.json (or run /mcp in Mux and remove the armada server).";

            return "Remove the `armada` object from the `mcpServers` section.";
        }

        internal static string BuildClaudeCliCommand(int mcpPort)
        {
            // Single quotes keep the shell from expanding the variable, so Claude Code stores the reference.
            return $"claude mcp add --transport http --scope user armada {GetMcpUrl(mcpPort)} --header '{ApiKeyHeaderName}: {ApiKeyForDollarBraceExpansion}'";
        }

        internal static string BuildClaudeStdioCommand()
        {
            return "claude mcp add --scope user armada -- armada mcp stdio";
        }

        internal static string BuildCodexManualInstallCommand(string codexCommand)
        {
            return String.Join(" ", BuildCliCommandParts(codexCommand, BuildCodexInstallArgs()));
        }

        private static string[] BuildCodexInstallArgs()
        {
            List<string> args = new List<string> { "mcp", "add", "armada", "--" };
            args.AddRange(BuildCodexStdioCommandParts());
            return args.ToArray();
        }

        private static IEnumerable<string> BuildCodexStdioCommandParts()
        {
            if (TryGetSourceHelmAssemblyPath(out string? helmAssemblyPath))
            {
                return new[]
                {
                    "dotnet",
                    helmAssemblyPath!,
                    "mcp",
                    "stdio"
                };
            }

            return new[] { "armada", "mcp", "stdio" };
        }

        private static bool TryGetSourceHelmAssemblyPath(out string? helmAssemblyPath)
        {
            helmAssemblyPath = null;

            try
            {
                string assemblyDir = AppContext.BaseDirectory;
                string candidate = Path.Combine(assemblyDir, "Armada.Helm.dll");
                if (!File.Exists(candidate))
                    return false;

                if (TryGetSourceHelmProjectPath(out _))
                {
                    helmAssemblyPath = candidate;
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }

            return false;
        }

        private static bool TryGetSourceHelmProjectPath(out string? helmProjectPath)
        {
            helmProjectPath = null;

            try
            {
                string assemblyDir = AppContext.BaseDirectory;
                DirectoryInfo? current = new DirectoryInfo(assemblyDir);
                while (current != null)
                {
                    string candidate = Path.Combine(current.FullName, "src", "Armada.Helm", "Armada.Helm.csproj");
                    if (File.Exists(candidate))
                    {
                        helmProjectPath = candidate;
                        return true;
                    }
                    current = current.Parent;
                }
            }
            catch (Exception)
            {
                return false;
            }

            return false;
        }

        private static IEnumerable<string> BuildCliCommandParts(string command, IEnumerable<string> args)
        {
            yield return QuoteIfNeeded(command);
            foreach (string arg in args)
                yield return QuoteIfNeeded(arg);
        }

        private static string QuoteIfNeeded(string value)
        {
            if (String.IsNullOrEmpty(value)) return "\"\"";
            return value.Any(Char.IsWhiteSpace) ? $"\"{value}\"" : value;
        }

        private static string WrapManagedBlock(string content)
        {
            return $"{ManagedBlockStart}{Environment.NewLine}{content.Trim()}{Environment.NewLine}{ManagedBlockEnd}";
        }

        private static string UpsertManagedBlock(string? existing, string managedContent)
        {
            if (String.IsNullOrWhiteSpace(existing))
                return managedContent + Environment.NewLine;

            int start = existing.IndexOf(ManagedBlockStart, StringComparison.Ordinal);
            int end = existing.IndexOf(ManagedBlockEnd, StringComparison.Ordinal);
            if (start >= 0 && end >= start)
            {
                int afterEnd = end + ManagedBlockEnd.Length;
                string prefix = existing[..start].TrimEnd();
                string suffix = existing[afterEnd..].TrimStart();
                return CombineSections(prefix, managedContent, suffix);
            }

            return CombineSections(existing.TrimEnd(), managedContent, String.Empty);
        }

        private static string RemoveManagedBlock(string existing)
        {
            int start = existing.IndexOf(ManagedBlockStart, StringComparison.Ordinal);
            int end = existing.IndexOf(ManagedBlockEnd, StringComparison.Ordinal);
            if (start < 0 || end < start)
                return existing;

            int afterEnd = end + ManagedBlockEnd.Length;
            string prefix = existing[..start].TrimEnd();
            string suffix = existing[afterEnd..].TrimStart();
            return CombineSections(prefix, String.Empty, suffix);
        }

        private static string CombineSections(string prefix, string middle, string suffix)
        {
            List<string> sections = new List<string>();
            if (!String.IsNullOrWhiteSpace(prefix))
                sections.Add(prefix);
            if (!String.IsNullOrWhiteSpace(middle))
                sections.Add(middle);
            if (!String.IsNullOrWhiteSpace(suffix))
                sections.Add(suffix);

            return sections.Count == 0
                ? String.Empty
                : String.Join($"{Environment.NewLine}{Environment.NewLine}", sections) + Environment.NewLine;
        }

        private static async Task<bool> EnsureTomlMcpServerStartupTimeoutAsync(string filePath, string serverName, int timeoutSeconds)
        {
            if (!File.Exists(filePath)) return false;
            List<string> lines = (await File.ReadAllLinesAsync(filePath).ConfigureAwait(false)).ToList();
            string tableHeader = "[mcp_servers." + serverName + "]";
            int tableStart = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (String.Equals(lines[i].Trim(), tableHeader, StringComparison.Ordinal))
                {
                    tableStart = i;
                    break;
                }
            }
            if (tableStart < 0) return false;
            int tableEnd = lines.Count;
            for (int i = tableStart + 1; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    tableEnd = i;
                    break;
                }
            }
            string desiredLine = "startup_timeout_sec = " + timeoutSeconds;
            for (int i = tableStart + 1; i < tableEnd; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith("startup_timeout_sec", StringComparison.Ordinal)) continue;
                int keyIndex = lines[i].IndexOf("startup_timeout_sec", StringComparison.Ordinal);
                string prefix = keyIndex > 0 ? lines[i][..keyIndex] : String.Empty;
                string updated = prefix + desiredLine;
                if (String.Equals(lines[i], updated, StringComparison.Ordinal)) return false;
                lines[i] = updated;
                await File.WriteAllLinesAsync(filePath, lines).ConfigureAwait(false);
                return true;
            }
            lines.Insert(tableEnd, desiredLine);
            await File.WriteAllLinesAsync(filePath, lines).ConfigureAwait(false);
            return true;
        }

        private static async Task<ApplyResult> InstallOpenCodeAsync(ConfigTarget target)
        {
            JsoncFile file = File.Exists(target.FilePath) ? await ReadJsoncFileAsync(target.FilePath).ConfigureAwait(false) : new JsoncFile(String.Empty, false);
            string original = file.Content;
            if (!String.IsNullOrWhiteSpace(original)) ValidateJsoncObject(original);
            Directory.CreateDirectory(Path.GetDirectoryName(target.FilePath)!);
            string updated = JsoncScopedEditor.Upsert(original, "mcp", "armada", target.ArmadaConfig!.ToJsonString(JsonOptions), out bool changed);
            if (changed)
            {
                ValidateJsoncObject(updated);
                await WriteJsoncFileAsync(target.FilePath, updated, file.HasUtf8Bom).ConfigureAwait(false);
            }
            return new ApplyResult(target.ClientName, target.FilePath, changed, changed ? "Configured Armada MCP entry." : "Armada MCP entry already matched the expected configuration.");
        }

        private static async Task<ApplyResult> RemoveOpenCodeAsync(ConfigTarget target)
        {
            if (!File.Exists(target.FilePath)) return new ApplyResult(target.ClientName, target.FilePath, false, "Configuration file does not exist; nothing to remove.");
            JsoncFile file = await ReadJsoncFileAsync(target.FilePath).ConfigureAwait(false);
            string original = file.Content;
            if (!String.IsNullOrWhiteSpace(original)) ValidateJsoncObject(original);
            string updated = JsoncScopedEditor.Remove(original, "mcp", "armada", out bool changed);
            if (changed)
            {
                ValidateJsoncObject(updated);
                await WriteJsoncFileAsync(target.FilePath, updated, file.HasUtf8Bom).ConfigureAwait(false);
            }
            return new ApplyResult(target.ClientName, target.FilePath, changed, changed ? "Removed Armada MCP entry." : "No Armada MCP entry was present.");
        }

        private sealed class JsoncFile
        {
            internal JsoncFile(string content, bool hasUtf8Bom)
            {
                Content = content;
                HasUtf8Bom = hasUtf8Bom;
            }

            internal string Content { get; }
            internal bool HasUtf8Bom { get; }
        }

        private static async Task<JsoncFile> ReadJsoncFileAsync(string path)
        {
            byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            bool hasUtf8Bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            int offset = hasUtf8Bom ? 3 : 0;
            UTF8Encoding encoding = new UTF8Encoding(false, true);
            return new JsoncFile(encoding.GetString(bytes, offset, bytes.Length - offset), hasUtf8Bom);
        }

        private static async Task WriteJsoncFileAsync(string path, string content, bool hasUtf8Bom)
        {
            UTF8Encoding encoding = new UTF8Encoding(false, true);
            byte[] contentBytes = encoding.GetBytes(content);
            byte[] output = hasUtf8Bom ? new byte[contentBytes.Length + 3] : contentBytes;
            if (hasUtf8Bom)
            {
                output[0] = 0xEF;
                output[1] = 0xBB;
                output[2] = 0xBF;
                Buffer.BlockCopy(contentBytes, 0, output, 3, contentBytes.Length);
            }
            await File.WriteAllBytesAsync(path, output).ConfigureAwait(false);
        }

        private static void ValidateJsoncObject(string text)
        {
            JsoncScopedEditor.Validate(text);
        }

        private static string ResolveCliCommand(string baseName)
        {
            if (!OperatingSystem.IsWindows())
                return baseName;

            string candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm",
                baseName + ".cmd");

            return File.Exists(candidate) ? candidate : baseName;
        }

        private static async Task<bool> RunCliCommandAsync(string command, IEnumerable<string> args)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = command,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                foreach (string arg in args)
                    startInfo.ArgumentList.Add(arg);

                using (Process? process = Process.Start(startInfo))
                {
                    if (process == null)
                        return false;

                    await process.WaitForExitAsync().ConfigureAwait(false);
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Generate the Armada agent definition markdown for Claude Code.
        /// </summary>
        internal static string GenerateAgentDefinition()
        {
            return """
                ---
                name: armada
                description: Armada orchestrator - manage fleets, vessels, captains, missions, and voyages via MCP
                model: inherit
                allowedTools: mcp__armada__*
                ---

                You are an Armada orchestrator. Your sole purpose is to control and monitor the Armada multi-agent system through its MCP tools. You are NOT tied to any project - you operate purely as a proxy.

                ## What is Armada

                Armada is a multi-agent orchestration system that scales human developers with AI. It coordinates AI coding agents ("captains") to work on tasks ("missions") across git repositories ("vessels").

                ## Key Concepts

                - **Admiral** - The coordinator server you're connected to via MCP
                - **Fleet** - A collection of repositories
                - **Vessel** - A single git repository registered in a fleet
                - **Captain** - An AI agent worker (Claude Code, Codex, etc.)
                - **Mission** - An atomic work unit assigned to a captain
                - **Voyage** - A batch of related missions dispatched together
                - **Dock** - A git worktree where a captain does its work
                - **Signal** - A message between the user/admiral and a captain

                ## ID Prefixes

                All entity IDs have prefixes: `flt_` (fleet), `vsl_` (vessel), `cpt_` (captain), `msn_` (mission), `vyg_` (voyage), `dck_` (dock), `sig_` (signal), `mrg_` (merge entry).

                ## How to Behave

                1. **Always use MCP tools** - never search local files or run bash commands. All your work happens through `mcp__armada__*` tools.
                2. **Start with status** - when asked to check on things, call `status` first for an overview.
                3. **Be concise** - show results in clean tables or bullet points, not walls of JSON.
                4. **Confirm destructive actions** - before deleting, cancelling, stopping, or purging, confirm with the user.
                5. **Proactive monitoring** - when checking on missions/voyages, show progress, any failures, and suggest next steps.

                ## Common Workflows

                **Dispatch work:**
                1. Ensure fleet and vessels exist (`enumerate` with entityType 'fleets' or 'vessels')
                2. Ensure captains are registered (`enumerate` with entityType 'captains')
                3. Dispatch a voyage with missions (`dispatch`) or create standalone missions (`create_mission`)

                **Monitor progress:**
                1. `status` for overview
                2. `voyage_status` for voyage summary (returns mission counts by status; set summary=false and includeMissions=true for full mission objects) or `mission_status` for individual mission details
                3. `get_mission_log` / `get_captain_log` to see what an agent is doing
                4. `get_mission_diff` to review code changes

                **Review and land:**
                1. Check mission diffs (`get_mission_diff`)
                2. Transition status (`transition_mission_status` to Review/Complete)
                3. Enqueue for merge (`enqueue_merge`)
                4. Process merge queue (`process_merge_queue`)

                ## Mission Status Flow

                Pending -> Assigned -> InProgress -> Testing/Review/Complete/Failed
                Most states allow -> Cancelled
                """;
        }

        internal static string GenerateAgentsInstructions()
        {
            return """
                # Armada Orchestrator

                This project is configured to let Codex and Cursor orchestrate Armada through its MCP tools.

                ## Primary Rules

                - When the user asks about Armada state or operations, prefer Armada MCP tools over shell commands or local file inspection.
                - Start with `status` for broad "what is happening?" questions.
                - Use `enumerate` to browse fleets, vessels, captains, missions, voyages, docks, signals, events, merge queue entries, personas, prompt templates, and pipelines.
                - Use `voyage_status` and `mission_status` for status checks.
                - Use `get_mission_log`, `get_captain_log`, and `get_mission_diff` when investigating progress or failures.
                - Confirm destructive actions before deleting, purging, cancelling, stopping captains, or stopping the server.

                ## Common Flow

                1. Check `status`.
                2. Drill into the relevant voyage, mission, captain, or vessel.
                3. Summarize the state clearly.
                4. Take follow-up actions with MCP tools only when the user has asked for them.

                ## Useful IDs

                - `flt_` fleet
                - `vsl_` vessel
                - `cpt_` captain
                - `msn_` mission
                - `vyg_` voyage
                - `dck_` dock
                - `sig_` signal
                - `mrg_` merge queue entry
                """;
        }

        internal static string GenerateGeminiInstructions()
        {
            return """
                # Armada Orchestrator

                This project is configured to let Gemini CLI orchestrate Armada through its MCP tools.

                ## Primary Rules

                - When the user asks about Armada state or operations, prefer Armada MCP tools over shell commands or local file inspection.
                - Start with `status` for broad "what is happening?" questions.
                - Use `enumerate` to browse fleets, vessels, captains, missions, voyages, docks, signals, events, merge queue entries, personas, prompt templates, and pipelines.
                - Use `voyage_status` and `mission_status` for status checks.
                - Use `get_mission_log`, `get_captain_log`, and `get_mission_diff` when investigating progress or failures.
                - Confirm destructive actions before deleting, purging, cancelling, stopping captains, or stopping the server.

                ## Common Flow

                1. Check `status`.
                2. Drill into the relevant voyage, mission, captain, or vessel.
                3. Summarize the state clearly.
                4. Take follow-up actions with MCP tools only when the user has asked for them.

                ## Useful IDs

                - `flt_` fleet
                - `vsl_` vessel
                - `cpt_` captain
                - `msn_` mission
                - `vyg_` voyage
                - `dck_` dock
                - `sig_` signal
                - `mrg_` merge queue entry
                """;
        }
    }
}
