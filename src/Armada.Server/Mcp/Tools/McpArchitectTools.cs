namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// MCP tools for Architect-mode dispatch.
    /// armada_decompose_plan dispatches an Architect captain with a spec prestaged.
    /// armada_parse_architect_output parses the captain's AgentOutput into structured
    /// {plan, missions[]} for orchestrator review.
    /// </summary>
    public static class McpArchitectTools
    {
        private const string SpecDestPath = "_briefing/spec.md";
        private const string ProjectClaudeDestPath = "_briefing/PROJECT-CLAUDE.md";
        private const string CodeContextDestPath = "_briefing/context-pack.md";
        private const string DefaultProjectClaudePath = @"C:\Users\Owner\RiderProjects\project\CLAUDE.md";
        private const string DefaultArchitectModel = "high";
        private const string CodeContextModeAuto = "auto";
        private const string CodeContextModeOff = "off";
        private const string CodeContextModeForce = "force";
        private const int DefaultCodeContextTokenBudget = 3000;
        private const int MaxSpecExcerptChars = 2500;

        /// <summary>Registers Architect MCP tools with the server.</summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for data access.</param>
        /// <param name="parser">Parser for Architect captain output.</param>
        /// <param name="admiral">Admiral service for voyage dispatch.</param>
        /// <param name="codeIndexService">Optional code index service used to auto-attach context packs.</param>
        /// <param name="logging">Optional logging module for best-effort context-pack warnings.</param>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            IArchitectOutputParser parser,
            IAdmiralService admiral,
            ICodeIndexService? codeIndexService = null,
            LoggingModule? logging = null)
        {
            register(
                "armada_decompose_plan",
                "Dispatches an Architect captain to decompose a spec into a structured implementation plan with dispatchable mission blocks. Code-index context packs are attached by default when available; set codeContextMode to off to opt out or force to require generation.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        specPath = new { type = "string", description = "Absolute path on the admiral host to the spec markdown file" },
                        vesselId = new { type = "string", description = "Target vessel for the Architect captain and downstream Worker missions" },
                        preferredModel = new { type = "string", description = "Architect complexity tier. Use 'low', 'mid', or 'high'. Default 'high'." },
                        codeContextMode = new { type = "string", description = "Code context mode: auto (default), off, or force." },
                        codeContextTokenBudget = new { type = "integer", description = "Optional token budget for the generated context pack. Defaults to 3000 tokens." },
                        codeContextMaxResults = new { type = "integer", description = "Optional maximum number of code-index evidence results. Omit to use CodeIndex settings." },
                        codeContextQuery = new { type = "string", description = "Optional code search query. Defaults to spec filename, architect description, and a bounded spec excerpt." },
                        selectedPlaybooks = new
                        {
                            type = "array",
                            description = "Optional playbooks to include. Merged with vessel DefaultPlaybooks; caller entry wins on playbookId collision.",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    playbookId = new { type = "string", description = "Playbook ID (pbk_ prefix)" },
                                    deliveryMode = new { type = "string", description = "InlineFullContent, InstructionWithReference, or AttachIntoWorktree" }
                                },
                                required = new[] { "playbookId", "deliveryMode" }
                            }
                        }
                    },
                    required = new[] { "specPath", "vesselId" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };

                    string specPath = args.Value.GetProperty("specPath").GetString()!;
                    string vesselId = args.Value.GetProperty("vesselId").GetString()!;
                    string preferredModel = DefaultArchitectModel;
                    if (args.Value.TryGetProperty("preferredModel", out JsonElement pm) && pm.ValueKind == JsonValueKind.String)
                        preferredModel = pm.GetString()!;
                    string? codeContextMode = ReadOptionalString(args.Value, "codeContextMode");
                    string? codeContextQuery = ReadOptionalString(args.Value, "codeContextQuery");
                    int? codeContextTokenBudget = ReadOptionalInt(args.Value, "codeContextTokenBudget");
                    int? codeContextMaxResults = ReadOptionalInt(args.Value, "codeContextMaxResults");

                    if (!File.Exists(specPath))
                        return (object)new { Error = "specPath does not exist: " + specPath };

                    string projectClaudePath = Environment.GetEnvironmentVariable("ARMADA_PROJECT_CLAUDE_MD") ?? DefaultProjectClaudePath;
                    if (!File.Exists(projectClaudePath))
                        return (object)new { Error = "project CLAUDE.md not found at " + projectClaudePath };

                    string specBasename = Path.GetFileName(specPath);
                    string title = "architect: decompose " + Path.GetFileNameWithoutExtension(specPath);
                    string description = "Decompose the spec at _briefing/spec.md into a markdown plan + N [ARMADA:MISSION] blocks. " +
                                        "Vessel: " + vesselId + ". Spec: " + specBasename + ". " +
                                        "Full instructions in your persona system prompt.";

                    // Merge vessel DefaultPlaybooks with any caller-supplied selectedPlaybooks.
                    Vessel? dispatchVessel = await database.Vessels.ReadAsync(vesselId).ConfigureAwait(false);
                    List<SelectedPlaybook> callerPlaybooks = new List<SelectedPlaybook>();
                    if (args.Value.TryGetProperty("selectedPlaybooks", out JsonElement spElem) && spElem.ValueKind == JsonValueKind.Array)
                    {
                        List<SelectedPlaybook>? parsed = JsonSerializer.Deserialize<List<SelectedPlaybook>>(
                            spElem.GetRawText(),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
                        if (parsed != null) callerPlaybooks = parsed;
                    }
                    List<SelectedPlaybook> mergedPlaybooks = PlaybookMerge.MergeWithVesselDefaults(dispatchVessel?.GetDefaultPlaybooks(), callerPlaybooks);

                    MissionDescription missionDesc = new MissionDescription(title, description);
                    missionDesc.PreferredModel = preferredModel;
                    missionDesc.PrestagedFiles = new List<PrestagedFile>
                    {
                        new PrestagedFile { SourcePath = specPath, DestPath = SpecDestPath },
                        new PrestagedFile { SourcePath = projectClaudePath, DestPath = ProjectClaudeDestPath },
                    };

                    string? codeContextError = await ApplyArchitectCodeContextAsync(
                        codeIndexService,
                        logging,
                        vesselId,
                        specPath,
                        specBasename,
                        description,
                        missionDesc,
                        codeContextMode,
                        codeContextQuery,
                        codeContextTokenBudget,
                        codeContextMaxResults).ConfigureAwait(false);
                    if (codeContextError != null) return (object)new { Error = codeContextError };

                    Voyage voyage = await admiral.DispatchVoyageAsync(
                        title,
                        "Architect-mode decomposition for " + specBasename,
                        vesselId,
                        new List<MissionDescription> { missionDesc },
                        mergedPlaybooks).ConfigureAwait(false);

                    List<Mission> missions = await database.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    string architectMissionId = "";
                    if (missions.Count > 0)
                    {
                        Mission architectMission = missions[0];
                        architectMission.Persona = "Architect";
                        architectMission = await database.Missions.UpdateAsync(architectMission).ConfigureAwait(false);
                        architectMissionId = architectMission.Id;
                    }

                    return (object)new
                    {
                        voyageId = voyage.Id,
                        architectMissionId,
                        status = "InProgress"
                    };
                });

            register(
                "armada_parse_architect_output",
                "Parses the Architect captain's AgentOutput into a structured plan and mission list",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "The Architect captain's mission ID (msn_ prefix)" }
                    },
                    required = new[] { "missionId" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };
                    string missionId = args.Value.GetProperty("missionId").GetString()!;

                    Mission? mission = await database.Missions.ReadAsync(missionId).ConfigureAwait(false);
                    if (mission == null)
                        return (object)new { Error = "mission not found: " + missionId };

                    if (mission.Status != Armada.Core.Enums.MissionStatusEnum.WorkProduced
                        && mission.Status != Armada.Core.Enums.MissionStatusEnum.Complete)
                    {
                        return (object)new { Error = "mission not in WorkProduced/Complete state: " + mission.Status };
                    }

                    if (string.IsNullOrEmpty(mission.AgentOutput))
                        return (object)new { Error = "mission has no AgentOutput" };

                    ArchitectParseResult result = parser.Parse(mission.AgentOutput);
                    return (object)result;
                });
        }

        private static async Task<string?> ApplyArchitectCodeContextAsync(
            ICodeIndexService? codeIndexService,
            LoggingModule? logging,
            string vesselId,
            string specPath,
            string specBasename,
            string architectDescription,
            MissionDescription mission,
            string? modeValue,
            string? queryValue,
            int? tokenBudget,
            int? maxResults)
        {
            string mode;
            if (!TryNormalizeCodeContextMode(modeValue, CodeContextModeAuto, out mode))
                return "invalid codeContextMode: " + modeValue + ". Expected auto, off, or force.";

            if (String.Equals(mode, CodeContextModeOff, StringComparison.Ordinal))
                return null;

            if (codeIndexService == null)
            {
                if (String.Equals(mode, CodeContextModeForce, StringComparison.Ordinal))
                    return "code context force requested but code index service is unavailable";

                LogCodeContextWarning(logging, "code index service is unavailable; architect dispatch will continue without auto code context");
                return null;
            }

            try
            {
                string query = await BuildArchitectCodeContextQueryAsync(
                    specPath,
                    specBasename,
                    architectDescription,
                    queryValue).ConfigureAwait(false);

                ContextPackResponse contextPack = await codeIndexService.BuildContextPackAsync(new ContextPackRequest
                {
                    VesselId = vesselId,
                    Goal = query,
                    TokenBudget = tokenBudget ?? DefaultCodeContextTokenBudget,
                    MaxResults = maxResults
                }).ConfigureAwait(false);

                if (contextPack.PrestagedFiles == null || contextPack.PrestagedFiles.Count == 0)
                {
                    if (String.Equals(mode, CodeContextModeForce, StringComparison.Ordinal))
                        return "code context generation returned no prestaged files for architect mission";

                    LogCodeContextWarning(logging, "code context generation returned no prestaged files for architect mission");
                    return null;
                }

                MergeGeneratedPrestagedFiles(mission, contextPack.PrestagedFiles, logging);
            }
            catch (Exception ex)
            {
                if (String.Equals(mode, CodeContextModeForce, StringComparison.Ordinal))
                    return "code context generation failed for architect mission: " + ex.Message;

                LogCodeContextWarning(logging, "code context generation failed for architect mission: " + ex.Message);
            }

            return null;
        }

        private static string? ReadOptionalString(JsonElement args, string propertyName)
        {
            if (args.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.String)
                return element.GetString();

            return null;
        }

        private static int? ReadOptionalInt(JsonElement args, string propertyName)
        {
            if (args.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.Number)
            {
                if (element.TryGetInt32(out int value))
                    return value;
            }

            return null;
        }

        private static bool TryNormalizeCodeContextMode(string? value, string fallback, out string normalized)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                normalized = fallback;
                return true;
            }

            string candidate = value.Trim().ToLowerInvariant();
            if (String.Equals(candidate, CodeContextModeAuto, StringComparison.Ordinal)
                || String.Equals(candidate, CodeContextModeOff, StringComparison.Ordinal)
                || String.Equals(candidate, CodeContextModeForce, StringComparison.Ordinal))
            {
                normalized = candidate;
                return true;
            }

            normalized = fallback;
            return false;
        }

        private static async Task<string> BuildArchitectCodeContextQueryAsync(
            string specPath,
            string specBasename,
            string architectDescription,
            string? queryValue)
        {
            if (!String.IsNullOrWhiteSpace(queryValue))
                return queryValue.Trim();

            string excerpt = await ReadBoundedTextAsync(specPath, MaxSpecExcerptChars).ConfigureAwait(false);
            string query = "Spec file: " + specBasename + "\n\nArchitect mission: " + architectDescription;
            if (!String.IsNullOrWhiteSpace(excerpt))
                query += "\n\nSpec excerpt:\n" + excerpt.Trim();

            return query;
        }

        private static async Task<string> ReadBoundedTextAsync(string path, int maxChars)
        {
            if (maxChars <= 0) return "";

            char[] buffer = new char[maxChars];
            using (StreamReader reader = new StreamReader(path))
            {
                int read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                return new string(buffer, 0, read);
            }
        }

        private static void MergeGeneratedPrestagedFiles(
            MissionDescription mission,
            List<PrestagedFile> generatedFiles,
            LoggingModule? logging)
        {
            if (generatedFiles == null || generatedFiles.Count == 0) return;

            List<PrestagedFile> merged = mission.PrestagedFiles ?? new List<PrestagedFile>();
            foreach (PrestagedFile generated in generatedFiles)
            {
                if (generated == null) continue;

                bool duplicateDest = false;
                foreach (PrestagedFile existing in merged)
                {
                    if (existing == null) continue;
                    if (String.Equals(existing.DestPath, generated.DestPath, StringComparison.Ordinal))
                    {
                        duplicateDest = true;
                        break;
                    }
                }

                if (duplicateDest)
                {
                    LogCodeContextWarning(logging, "skipping generated code context prestaged file because destPath already exists: " + generated.DestPath);
                    continue;
                }

                merged.Add(new PrestagedFile(generated.SourcePath ?? "", generated.DestPath ?? CodeContextDestPath));
            }

            mission.PrestagedFiles = merged.Count > 0 ? merged : null;
        }

        private static void LogCodeContextWarning(LoggingModule? logging, string message)
        {
            if (logging == null) return;
            logging.Warn("[McpArchitectTools] " + message);
        }
    }
}
