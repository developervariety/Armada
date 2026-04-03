namespace Armada.Helm.Commands
{
    using System.Diagnostics;
    using System.Text.Json;
    using System.Text.Json.Nodes;

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
            string? ManualRemoveCommand = null);

        internal sealed record ApplyResult(string ClientName, string FilePath, bool Changed, string Message, bool IsProjectScoped = false);
        internal sealed record InstructionTarget(string ClientName, string FilePath, string Content, bool IsProjectScoped = false);

        internal static JsonSerializerOptions JsonOptions { get; } = new JsonSerializerOptions { WriteIndented = true };
        private const string ManagedBlockStart = "<!-- armada:mcp:begin -->";
        private const string ManagedBlockEnd = "<!-- armada:mcp:end -->";
        private const string SourceMcpFramework = "net10.0";

        internal static string GetMcpRpcUrl(int mcpPort)
        {
            return $"http://localhost:{mcpPort}/rpc";
        }

        internal static string GetClaudeJsonPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        }

        internal static string GetClaudeAgentPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "agents", "armada.md");
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

        internal static List<ConfigTarget> BuildTargets(int mcpPort)
        {
            string mcpRpcUrl = GetMcpRpcUrl(mcpPort);
            string codexCommand = ResolveCliCommand("codex");
            string geminiCommand = ResolveCliCommand("gemini");

            return new List<ConfigTarget>
            {
                new(
                    "Claude Code",
                    GetClaudeJsonPath(),
                    new JsonObject
                    {
                        ["type"] = "http",
                        ["url"] = mcpRpcUrl,
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
                    ManualRemoveCommand: codexCommand + " mcp remove armada"),
                new(
                    "Gemini CLI",
                    GetGeminiConfigPath(),
                    CliCommand: geminiCommand,
                    InstallArgs: new[] { "mcp", "add", "--scope", "user", "--transport", "http", "armada", mcpRpcUrl },
                    RemoveArgs: new[] { "mcp", "remove", "armada" },
                    ManualInstallCommand: geminiCommand + " mcp add --scope user --transport http armada " + mcpRpcUrl,
                    ManualRemoveCommand: geminiCommand + " mcp remove armada"),
                new(
                    "Cursor",
                    GetCursorConfigPath(),
                    new JsonObject
                    {
                        ["url"] = mcpRpcUrl,
                        ["transport"] = "http",
                    },
                    IsProjectScoped: true),
            };
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
            if (!String.IsNullOrEmpty(target.CliCommand) && target.InstallArgs != null)
            {
                if (!String.IsNullOrEmpty(target.RemoveBeforeInstallName))
                    await RunCliCommandAsync(target.CliCommand, new[] { "mcp", "remove", target.RemoveBeforeInstallName }).ConfigureAwait(false);

                bool success = await RunCliCommandAsync(target.CliCommand, target.InstallArgs).ConfigureAwait(false);
                return new ApplyResult(
                    target.ClientName,
                    target.FilePath,
                    success,
                    success
                        ? "Configured Armada MCP entry via native CLI."
                        : "Failed to configure Armada MCP entry via native CLI.",
                    target.IsProjectScoped);
            }

            if (target.ArmadaConfig == null)
                throw new InvalidOperationException("Config target does not define ArmadaConfig for file-based installation.");

            JsonObject root = await ReadOrCreateRootAsync(target.FilePath).ConfigureAwait(false);
            if (root["mcpServers"] is not JsonObject)
            {
                root["mcpServers"] = new JsonObject();
            }

            JsonObject mcpServers = root["mcpServers"]!.AsObject();
            JsonNode? existing = mcpServers["armada"];
            bool changed = existing == null || !JsonNode.DeepEquals(existing, target.ArmadaConfig);
            mcpServers["armada"] = target.ArmadaConfig.DeepClone();

            Directory.CreateDirectory(Path.GetDirectoryName(target.FilePath)!);
            await File.WriteAllTextAsync(target.FilePath, root.ToJsonString(JsonOptions)).ConfigureAwait(false);

            return new ApplyResult(
                target.ClientName,
                target.FilePath,
                changed,
                changed ? "Configured Armada MCP entry." : "Armada MCP entry already matched the expected configuration.",
                target.IsProjectScoped);
        }

        internal static async Task<ApplyResult> RemoveTargetAsync(ConfigTarget target)
        {
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

            JsonObject root = await ReadOrCreateRootAsync(target.FilePath).ConfigureAwait(false);
            JsonObject? mcpServers = root["mcpServers"] as JsonObject;
            if (mcpServers == null || !mcpServers.ContainsKey("armada"))
            {
                return new ApplyResult(
                    target.ClientName,
                    target.FilePath,
                    false,
                    "No Armada MCP entry was present.",
                    target.IsProjectScoped);
            }

            mcpServers.Remove("armada");
            if (mcpServers.Count == 0)
            {
                root.Remove("mcpServers");
            }

            await File.WriteAllTextAsync(target.FilePath, root.ToJsonString(JsonOptions)).ConfigureAwait(false);

            return new ApplyResult(
                target.ClientName,
                target.FilePath,
                true,
                "Removed Armada MCP entry.",
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

            return "Remove the `armada` object from the `mcpServers` section.";
        }

        internal static string BuildClaudeCliCommand(int mcpPort)
        {
            return $"claude mcp add --transport http --scope user armada {GetMcpRpcUrl(mcpPort)}";
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
            if (TryGetSourceHelmProjectPath(out string? helmProjectPath))
            {
                return new[]
                {
                    "dotnet",
                    "run",
                    "--project",
                    helmProjectPath!,
                    "-f",
                    SourceMcpFramework,
                    "--",
                    "mcp",
                    "stdio"
                };
            }

            return new[] { "armada", "mcp", "stdio" };
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
            catch
            {
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

        private static async Task<JsonObject> ReadOrCreateRootAsync(string path)
        {
            if (!File.Exists(path))
            {
                return new JsonObject();
            }

            string text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new JsonObject();
            }

            JsonNode? node = JsonNode.Parse(text);
            return node as JsonObject ?? new JsonObject();
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

                using Process? process = Process.Start(startInfo);
                if (process == null)
                    return false;

                await process.WaitForExitAsync().ConfigureAwait(false);
                return process.ExitCode == 0;
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

                You are an Armada orchestrator. Your sole purpose is to control and monitor the Armada multi-agent system through its MCP tools. You are NOT tied to any project - you operate purely as a control plane.

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
                2. **Start with status** - when asked to check on things, call `armada_status` first for an overview.
                3. **Be concise** - show results in clean tables or bullet points, not walls of JSON.
                4. **Confirm destructive actions** - before deleting, cancelling, stopping, or purging, confirm with the user.
                5. **Proactive monitoring** - when checking on missions/voyages, show progress, any failures, and suggest next steps.

                ## Common Workflows

                **Dispatch work:**
                1. Ensure fleet and vessels exist (`armada_enumerate` with entityType 'fleets' or 'vessels')
                2. Ensure captains are registered (`armada_enumerate` with entityType 'captains')
                3. Dispatch a voyage with missions (`armada_dispatch`) or create standalone missions (`armada_create_mission`)

                **Monitor progress:**
                1. `armada_status` for overview
                2. `armada_voyage_status` for voyage summary (returns mission counts by status; set summary=false and includeMissions=true for full mission objects) or `armada_mission_status` for individual mission details
                3. `armada_get_mission_log` / `armada_get_captain_log` to see what an agent is doing
                4. `armada_get_mission_diff` to review code changes

                **Review and land:**
                1. Check mission diffs (`armada_get_mission_diff`)
                2. Transition status (`armada_transition_mission_status` to Review/Complete)
                3. Enqueue for merge (`armada_enqueue_merge`)
                4. Process merge queue (`armada_process_merge_queue`)

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

                - When the user asks about Armada state or operations, prefer `armada_*` MCP tools over shell commands or local file inspection.
                - Start with `armada_status` for broad "what is happening?" questions.
                - Use `armada_enumerate` to browse fleets, vessels, captains, missions, voyages, docks, signals, events, merge queue entries, personas, prompt templates, and pipelines.
                - Use `armada_voyage_status` and `armada_mission_status` for status checks.
                - Use `armada_get_mission_log`, `armada_get_captain_log`, and `armada_get_mission_diff` when investigating progress or failures.
                - Confirm destructive actions before deleting, purging, cancelling, stopping captains, or stopping the server.

                ## Common Flow

                1. Check `armada_status`.
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

                - When the user asks about Armada state or operations, prefer `armada_*` MCP tools over shell commands or local file inspection.
                - Start with `armada_status` for broad "what is happening?" questions.
                - Use `armada_enumerate` to browse fleets, vessels, captains, missions, voyages, docks, signals, events, merge queue entries, personas, prompt templates, and pipelines.
                - Use `armada_voyage_status` and `armada_mission_status` for status checks.
                - Use `armada_get_mission_log`, `armada_get_captain_log`, and `armada_get_mission_diff` when investigating progress or failures.
                - Confirm destructive actions before deleting, purging, cancelling, stopping captains, or stopping the server.

                ## Common Flow

                1. Check `armada_status`.
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
