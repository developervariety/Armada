namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Registers the operator tool that lets one passed Judge, whose landing failed inside a Failed voyage, rerun at a
    /// regenerated candidate without a new voyage.
    /// </summary>
    public static class McpTerminalLandingContinuationTools
    {
        /// <summary>Refusal reason returned when a caller other than a global administrator calls the tool.</summary>
        public const string GlobalAdministratorRequiredReason = "global_administrator_required";

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>Register the terminal landing continuation tool.</summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="missions">Mission service.</param>
        public static void Register(RegisterToolDelegate register, IMissionService missions)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (missions == null) throw new ArgumentNullException(nameof(missions));

            register(
                "armada_continue_terminal_landing",
                "Let one passed Judge whose landing failed inside a Failed voyage rerun at a regenerated candidate, in the same voyage. "
                    + "Every predecessor must be delivered, or Failed only because terminal-voyage reconciliation found its work unlanded "
                    + "(terminal_voyage_work_unlanded) with its commit contained in the Judge's commit. Any other state refuses with a named "
                    + "terminal_continuation_* reason and changes nothing. The grant keeps every status, counter, output and fact; the Judge "
                    + "reruns for real and its candidate lands only after a new PASS and the required Checks at that exact commit. The voyage "
                    + "stays Failed. One grant per Judge. Operator-only.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Judge mission ID (msn_ prefix) in LandingFailed status" },
                        @operator = new { type = "string", description = "Operator granting the continuation (required, recorded on the grant and event)" }
                    },
                    required = new[] { "missionId", "operator" }
                },
                async (args) =>
                {
                    AuthContext caller = McpCallerContext.Require();
                    if (!caller.IsAdmin)
                        return (object)new { Error = "A terminal landing continuation requires a global administrator.", Reason = GlobalAdministratorRequiredReason };
                    ContinueArgs? request = args.HasValue ? JsonSerializer.Deserialize<ContinueArgs>(args.Value, _JsonOptions) : null;
                    if (request == null || String.IsNullOrWhiteSpace(request.MissionId) || String.IsNullOrWhiteSpace(request.Operator))
                        return (object)new { Error = "missionId and operator are required.", Reason = "terminal_continuation_arguments_required" };
                    TerminalLandingContinuationResult result = await missions.ContinueTerminalLandingAsync(
                        request.MissionId.Trim(), request.Operator.Trim()).ConfigureAwait(false);
                    if (!result.Granted) return (object)new { Granted = false, Reason = result.Reason };
                    return (object)new
                    {
                        Granted = true,
                        result.Binding!.CandidateCommit,
                        result.Binding.SourceCommit,
                        result.Binding.TargetBaseCommit,
                        result.Binding.PreparationAttempt
                    };
                });
        }

        private sealed class ContinueArgs
        {
            public string? MissionId { get; set; } = null;

            public string? Operator { get; set; } = null;
        }
    }
}
