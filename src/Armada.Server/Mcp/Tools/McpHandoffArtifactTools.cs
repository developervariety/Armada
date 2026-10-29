namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Registers the handoff artifact MCP tools: get_handoff_artifact (the complete artifact a rendered handoff points a
    /// captain at) and list_handoff_artifacts (a mission's artifacts). Both enforce the same rules as REST through
    /// <see cref="HandoffArtifactAccessService"/>; its typed exceptions become typed tool errors.
    /// </summary>
    public static class McpHandoffArtifactTools
    {
        #region Private-Members

        private const string UnavailableMessage = "Handoff artifacts are not available on this server.";

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the tools. When <paramref name="service"/> is null both tools are still registered and answer a typed
        /// Unavailable error, so every tool list matches.
        /// </summary>
        /// <param name="register">Registration delegate.</param>
        /// <param name="service">Handoff artifact access service, or null.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="register"/> is null.</exception>
        public static void Register(RegisterToolDelegate register, HandoffArtifactAccessService? service)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));

            register(
                "get_handoff_artifact",
                "Get the complete handoff artifact of a stage attempt: numbered findings (file, line, severity, required fix), acceptance criteria, required actions, check results (passed, failed, skipped with reason, could not run), commit references, the full stage output and the full diff (or where to regenerate it). The prior-stage section of a mission brief names the artifact id.",
                new
                {
                    type = "object",
                    properties = new { artifactId = new { type = "string", description = "Artifact ID (hoa_ prefix)" } },
                    required = new[] { "artifactId" }
                },
                async (args) =>
                {
                    if (service == null) return (object)McpToolError.Unavailable(UnavailableMessage);
                    AuthContext caller = McpToolHelpers.ResolveCallerContext();
                    HandoffArtifactIdArgs request = Read<HandoffArtifactIdArgs>(args);
                    return (object)await service.GetAsync(caller, request.ArtifactId).ConfigureAwait(false);
                });

            register(
                "list_handoff_artifacts",
                "List the handoff artifacts of a mission (one per stage attempt), newest first, one page at a time. Output and diff text are left out (their lengths stay); use get_handoff_artifact for them. Returns an enumeration result (objects, totalRecords, totalPages).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission ID (msn_ prefix)" },
                        pageNumber = new { type = "integer", description = "Page number (default 1)" },
                        pageSize = new { type = "integer", description = "Page size (1-1000, default 25)" }
                    },
                    required = new[] { "missionId" }
                },
                async (args) =>
                {
                    if (service == null) return (object)McpToolError.Unavailable(UnavailableMessage);
                    AuthContext caller = McpToolHelpers.ResolveCallerContext();
                    HandoffArtifactListArgs request = Read<HandoffArtifactListArgs>(args);
                    return (object)await service.ListByMissionAsync(caller, request.MissionId, request.PageNumber, request.PageSize).ConfigureAwait(false);
                });
        }

        #endregion

        #region Private-Methods

        private static T Read<T>(JsonElement? args) where T : class, new()
        {
            if (!args.HasValue || args.Value.ValueKind != JsonValueKind.Object) return new T();
            try
            {
                return JsonSerializer.Deserialize<T>(args.Value, _JsonOptions) ?? new T();
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("Invalid arguments: " + ex.Message, ex);
            }
        }

        #endregion
    }
}
