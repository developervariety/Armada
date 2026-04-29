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
        private const string DefaultProjectClaudePath = @"C:\Users\Owner\RiderProjects\project\CLAUDE.md";
        private const string DefaultArchitectModel = "claude-opus-4-7";

        /// <summary>Registers Architect MCP tools with the server.</summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for data access.</param>
        /// <param name="parser">Parser for Architect captain output.</param>
        /// <param name="admiral">Admiral service for voyage dispatch.</param>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            IArchitectOutputParser parser,
            IAdmiralService admiral)
        {
            register(
                "armada_decompose_plan",
                "Dispatches an Architect captain to decompose a spec into a structured implementation plan with dispatchable mission blocks",
                new
                {
                    type = "object",
                    properties = new
                    {
                        specPath = new { type = "string", description = "Absolute path on the admiral host to the spec markdown file" },
                        vesselId = new { type = "string", description = "Target vessel for the Architect captain and downstream Worker missions" },
                        preferredModel = new { type = "string", description = "Architect model. Default 'claude-opus-4-7'." },
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
    }
}
