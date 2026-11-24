namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>Caller-scoped operator access to independent automatic-work permissions.</summary>
    public static class McpMissionAutomaticWorkPermissionTools
    {
        /// <summary>Register the permission tool without granting captain credentials operator authority.</summary>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, IMissionService missions)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (missions == null) throw new ArgumentNullException(nameof(missions));
            JsonSerializerOptions options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            register("armada_mission_action_permissions",
                "Inspect or change one mission's optional automatic-work barrier. Enable holds verification and dependent admission separately. Grant binds the current launch and source and expires within one hour. Revoke removes one action grant. Operator-only; no captain restart, manual Review approval, work launch, routing change or product acceptance occurs from this call.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission to read within the caller's scope" },
                        operation = new { type = "string", @enum = new[] { "status", "Enable", "Grant", "Revoke" } },
                        action = new { type = "string", @enum = new[] { "Verification", "DependentAdmission" } },
                        expectedStartedUtc = new { type = "string", format = "date-time" },
                        expectedCommitHash = new { type = "string" },
                        expiresUtc = new { type = "string", format = "date-time" },
                        reason = new { type = "string", description = "Required for changes; recorded with the atomic audit" }
                    },
                    required = new[] { "missionId", "operation" }
                }, async args =>
                {
                    AuthContext caller = McpCallerContext.Require();
                    if (!caller.IsAuthenticated || !String.IsNullOrWhiteSpace(caller.MissionId)
                        || !String.IsNullOrWhiteSpace(caller.AskThreadId) || !String.IsNullOrWhiteSpace(caller.AskTurnId))
                        return (object)new { Error = "This control requires an authenticated operator.", Reason = "operator_required" };
                    try
                    {
                        PermissionArguments request = args.HasValue
                            ? JsonSerializer.Deserialize<PermissionArguments>(args.Value, options) ?? new PermissionArguments()
                            : new PermissionArguments();
                        if (String.IsNullOrWhiteSpace(request.MissionId) || String.IsNullOrWhiteSpace(request.Operation))
                            throw new ArgumentException("missionId and operation are required.");
                        bool status = String.Equals(request.Operation, "status", StringComparison.OrdinalIgnoreCase);
                        string method = status ? "GET" : "POST";
                        if (!new AuthorizationService().IsAuthorized(caller, method, "/api/v1/missions/permission/action-permissions"))
                            return (object)new { Error = "The caller cannot use this permission operation.", Reason = "permission_denied" };
                        if (status)
                        {
                            Mission? found = await CallerScopedRead.ReadMissionAsync(database, caller, request.MissionId).ConfigureAwait(false);
                            return found == null ? (object)new { Error = "Mission not found.", Reason = "mission_not_found" }
                                : new { found.Id, found.StartedUtc, found.CommitHash, found.AutomaticWorkPermission, found.AutomaticWorkPermissionInvalid };
                        }
                        if (!Enum.TryParse(request.Operation, true, out MissionAutomaticPermissionOperationEnum operation)
                            || !Enum.IsDefined(operation)) throw new ArgumentException("Unknown operation.");
                        MissionAutomaticActionEnum? action = null;
                        if (request.Action != null)
                        {
                            if (!Enum.TryParse(request.Action, true, out MissionAutomaticActionEnum parsedAction)
                                || !Enum.IsDefined(parsedAction)) throw new ArgumentException("Unknown action.");
                            action = parsedAction;
                        }
                        Mission? mission = await missions.UpdateAutomaticWorkPermissionAsync(caller, request.MissionId,
                            new MissionAutomaticWorkPermissionRequest
                            {
                                Operation = operation, Action = action, ExpectedStartedUtc = request.ExpectedStartedUtc,
                                ExpectedCommitHash = request.ExpectedCommitHash, ExpiresUtc = request.ExpiresUtc,
                                Reason = request.Reason ?? String.Empty
                            }).ConfigureAwait(false);
                        return mission ?? (object)new { Error = "Mission not found.", Reason = "mission_not_found" };
                    }
                    catch (JsonException) { return (object)new { Error = "Permission arguments could not be read.", Reason = "invalid_arguments" }; }
                    catch (ArgumentException ex) { return (object)new { Error = ex.Message, Reason = "invalid_arguments" }; }
                    catch (UnauthorizedAccessException) { return (object)new { Error = "Operator permission required.", Reason = "permission_denied" }; }
                    catch (InvalidOperationException ex) { return (object)new { Error = ex.Message, Reason = "permission_conflict" }; }
                });
        }

        private sealed class PermissionArguments
        {
            public string? MissionId { get; set; }
            public string? Operation { get; set; }
            public string? Action { get; set; }
            public DateTime? ExpectedStartedUtc { get; set; }
            public string? ExpectedCommitHash { get; set; }
            public DateTime? ExpiresUtc { get; set; }
            public string? Reason { get; set; }
        }
    }
}
