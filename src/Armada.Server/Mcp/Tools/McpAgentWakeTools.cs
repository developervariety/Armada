namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>Registers AgentWake orchestration tools.</summary>
    public static class McpAgentWakeTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>Registers AgentWake MCP tools.</summary>
        public static void Register(RegisterToolDelegate register, IRemoteTriggerService? remoteTriggerService)
        {
            register(
                "armada_register_agentwake_session",
                "Register the current orchestrator session so AgentWake can start the right runtime when Armada has work.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        runtime = new { type = "string", description = "Concrete runtime: Codex, OpenCode, or Claude. Auto is not valid here." },
                        sessionId = new { type = "string", description = "Optional session ID for Claude or Codex resume. OpenCode always starts fresh and ignores resume state." },
                        command = new { type = "string", description = "Optional command override for this runtime." },
                        workingDirectory = new { type = "string", description = "Optional working directory to use when Armada wakes the session." },
                        clientName = new { type = "string", description = "Optional client name/version for diagnostics." },
                        participantKey = new { type = "string", description = "Optional coordination-board participant key. Addressed notes for this key can start this registered session." }
                    },
                    required = new[] { "runtime" }
                },
                (args) =>
                {
                    if (remoteTriggerService == null) return Task.FromResult((object)new { Error = "Remote trigger service not configured" });
                    if (!args.HasValue) return Task.FromResult((object)new { Error = "missing args" });

                    AgentWakeSessionArgs request = JsonSerializer.Deserialize<AgentWakeSessionArgs>(args.Value, _JsonOptions)!;
                    if (request.Runtime == AgentWakeRuntime.Auto)
                        return Task.FromResult((object)new { Error = "runtime must be Codex, OpenCode, or Claude" });

                    AgentWakeSessionRegistration registered;
                    try
                    {
                        registered = remoteTriggerService.RegisterAgentWakeSession(new AgentWakeSessionRegistration
                        {
                            Runtime = request.Runtime,
                            SessionId = request.SessionId,
                            Command = request.Command,
                            WorkingDirectory = request.WorkingDirectory,
                            ClientName = request.ClientName,
                            ParticipantKey = request.ParticipantKey
                        });
                    }
                    catch (Exception ex)
                    {
                        return Task.FromResult((object)new { Error = ex.Message });
                    }

                    return Task.FromResult((object)new
                    {
                        Status = "registered",
                        Session = registered
                    });
                });

            register(
                "armada_agentwake_status",
                "Return the effective AgentWake configuration, persistent participant key, and last transient session registration.",
                new
                {
                    type = "object",
                    properties = new { }
                },
                (args) =>
                {
                    if (remoteTriggerService == null) return Task.FromResult((object)new { Error = "Remote trigger service not configured" });
                    AgentWakeStatusSnapshot status = remoteTriggerService.GetAgentWakeStatus();
                    return Task.FromResult((object)new
                    {
                        status.Configured,
                        status.DeliveryMode,
                        status.Runtime,
                        status.ProcessDeliveryEnabled,
                        status.ConfiguredParticipantKey,
                        status.EffectiveParticipantKey,
                        HasSession = status.Session != null,
                        status.Session
                    });
                });
        }
    }
}
