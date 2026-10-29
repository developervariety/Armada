namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using SyslogLogging;

    /// <summary>
    /// Native tool permission prompts of attended Ask turns. A saved turn's runtime asks here before it uses a native tool;
    /// the request waits for the thread owner's decision, or for a rule remembered for the thread, and is denied when the
    /// short expiry passes. Only the turn's own scoped credential reaches the prompt, and only the thread owner decides.
    /// </summary>
    public sealed class AskToolPermissionService
    {
        private const int _PollMilliseconds = 500;
        private readonly DatabaseDriver _Database;
        private readonly AskSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly string _Header = "[AskToolPermissionService] ";

        /// <summary>Create the service.</summary>
        /// <param name="database">Durable store.</param>
        /// <param name="settings">Live Ask settings, read on every request.</param>
        /// <param name="logging">Logging module.</param>
        public AskToolPermissionService(DatabaseDriver database, AskSettings settings, LoggingModule? logging = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? new LoggingModule();
        }

        /// <summary>Delay between reads of a waiting request. Tests shorten it.</summary>
        internal int PollMilliseconds { get; set; } = _PollMilliseconds;

        /// <summary>
        /// Answer one permission prompt from a saved turn's runtime. The answer is an allow with the original tool input, or a
        /// deny with a reason; it is never an exception, so the runtime always gets a decision it can act on.
        /// </summary>
        /// <param name="caller">Caller of the prompt tool; must carry the saved turn's own credential.</param>
        /// <param name="toolName">Native tool that needs permission.</param>
        /// <param name="inputJson">The tool input as JSON, returned unchanged on allow.</param>
        /// <param name="toolUseId">The runtime's identifier for the call.</param>
        /// <param name="token">Cancellation token; cancelling expires the request.</param>
        /// <returns>The prompt answer.</returns>
        public async Task<Armada.Server.Mcp.AskToolPermissionPromptResult> RequestAsync(AuthContext caller, string? toolName, string? inputJson, string? toolUseId, CancellationToken token = default)
        {
            if (caller == null || !caller.IsAuthenticated || !AskToolPolicy.IsAllowed(caller, AskToolPolicy.PermissionPromptToolName))
                return AskToolPermissionPromptResult.Deny("Permission prompts are available only to a saved Ask turn.");
            if (!await AskTurnBinding.IsBoundRunningTurnAsync(_Database, caller, token).ConfigureAwait(false))
                return AskToolPermissionPromptResult.Deny("This turn is no longer running.");
            if (String.IsNullOrWhiteSpace(toolName) || toolName.Length > 128)
                return AskToolPermissionPromptResult.Deny("A tool name of 1 to 128 characters is required.");

            string tenantId = caller.TenantId!;
            string userId = caller.UserId!;
            string threadId = caller.AskThreadId!;
            string normalizedInput = NormalizeInput(inputJson);
            AskToolPermissionRequest request = new AskToolPermissionRequest
            {
                TenantId = tenantId, UserId = userId, ThreadId = threadId, RequestId = caller.AskTurnId!,
                ToolName = toolName.Trim(), InputJson = normalizedInput, ToolUseId = toolUseId,
                CreatedUtc = DateTime.UtcNow
            };
            request.ExpiresUtc = request.CreatedUtc.AddSeconds(_Settings.ToolApprovalTimeoutSeconds);

            AskToolPermissionRule? rule = await _Database.AskToolPermissions.ReadRuleAsync(tenantId, userId, threadId, request.ToolName, token).ConfigureAwait(false);
            if (rule != null)
            {
                bool allow = rule.Action == AskToolPermissionRuleActionEnum.Allow;
                request.State = allow ? AskToolPermissionStateEnum.Allowed : AskToolPermissionStateEnum.Denied;
                request.DecisionSource = AskToolPermissionSourceEnum.Rule;
                request.RuleId = rule.Id;
                request.DecidedUtc = request.CreatedUtc;
                await _Database.AskToolPermissions.CreateRequestAsync(request, token).ConfigureAwait(false);
                return allow ? AskToolPermissionPromptResult.Allow(inputJson) : AskToolPermissionPromptResult.Deny("A rule of this conversation denies " + request.ToolName + ".");
            }

            await _Database.AskToolPermissions.CreateRequestAsync(request, token).ConfigureAwait(false);
            try
            {
                return await WaitForDecisionAsync(request, inputJson, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ExpireAsync(request).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>List the newest permission requests of an owned conversation.</summary>
        /// <param name="caller">Authenticated human owner.</param>
        /// <param name="threadId">Thread identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The requests, newest first, or null when the owner has no such conversation.</returns>
        public async Task<List<AskToolPermissionRequest>?> ListRequestsAsync(AuthContext caller, string threadId, CancellationToken token = default)
        {
            RequireOwner(caller);
            if (await _Database.AskThreads.ReadAsync(caller.TenantId!, caller.UserId!, threadId, token).ConfigureAwait(false) == null) return null;
            return await _Database.AskToolPermissions.EnumerateRequestsAsync(caller.TenantId!, caller.UserId!, threadId, 100, token).ConfigureAwait(false);
        }

        /// <summary>Allow or deny one pending request, and optionally remember the decision for the tool in this conversation.</summary>
        /// <param name="caller">Authenticated human owner.</param>
        /// <param name="threadId">Thread identifier.</param>
        /// <param name="requestId">Permission request identifier.</param>
        /// <param name="allow">True to allow, false to deny.</param>
        /// <param name="remember">True to remember the decision as a rule.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The decided request, or null when the owner has no such request.</returns>
        /// <exception cref="InvalidOperationException">The request was already decided or has expired.</exception>
        public async Task<AskToolPermissionRequest?> DecideAsync(AuthContext caller, string threadId, string requestId, bool allow, bool remember, CancellationToken token = default)
        {
            RequireOwner(caller);
            AskToolPermissionRequest? request = await _Database.AskToolPermissions.ReadRequestAsync(caller.TenantId!, caller.UserId!, threadId, requestId, token).ConfigureAwait(false);
            if (request == null) return null;
            bool decided = await _Database.AskToolPermissions.TryDecideAsync(caller.TenantId!, caller.UserId!, threadId, requestId, allow, AskToolPermissionSourceEnum.Owner, DateTime.UtcNow, token).ConfigureAwait(false);
            if (!decided) throw new InvalidOperationException("The request was already decided or has expired.");
            if (remember)
            {
                AskToolPermissionRule rule = new AskToolPermissionRule
                {
                    TenantId = caller.TenantId!, UserId = caller.UserId!, ThreadId = threadId, ToolName = request.ToolName,
                    Action = allow ? AskToolPermissionRuleActionEnum.Allow : AskToolPermissionRuleActionEnum.Deny
                };
                await _Database.AskToolPermissions.UpsertRuleAsync(rule, token).ConfigureAwait(false);
            }
            await RecordDecisionAsync(caller, request, allow, remember, token).ConfigureAwait(false);
            return await _Database.AskToolPermissions.ReadRequestAsync(caller.TenantId!, caller.UserId!, threadId, requestId, token).ConfigureAwait(false);
        }

        /// <summary>List the rules remembered for an owned conversation.</summary>
        /// <param name="caller">Authenticated human owner.</param>
        /// <param name="threadId">Thread identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The rules, or null when the owner has no such conversation.</returns>
        public async Task<List<AskToolPermissionRule>?> ListRulesAsync(AuthContext caller, string threadId, CancellationToken token = default)
        {
            RequireOwner(caller);
            if (await _Database.AskThreads.ReadAsync(caller.TenantId!, caller.UserId!, threadId, token).ConfigureAwait(false) == null) return null;
            return await _Database.AskToolPermissions.EnumerateRulesAsync(caller.TenantId!, caller.UserId!, threadId, token).ConfigureAwait(false);
        }

        /// <summary>Forget one remembered rule of an owned conversation.</summary>
        /// <param name="caller">Authenticated human owner.</param>
        /// <param name="threadId">Thread identifier.</param>
        /// <param name="ruleId">Rule identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the owner had that rule.</returns>
        public Task<bool> DeleteRuleAsync(AuthContext caller, string threadId, string ruleId, CancellationToken token = default)
        {
            RequireOwner(caller);
            return _Database.AskToolPermissions.DeleteRuleAsync(caller.TenantId!, caller.UserId!, threadId, ruleId, token);
        }

        private async Task<Armada.Server.Mcp.AskToolPermissionPromptResult> WaitForDecisionAsync(AskToolPermissionRequest request, string? inputJson, CancellationToken token)
        {
            while (true)
            {
                AskToolPermissionRequest? current = await _Database.AskToolPermissions.ReadRequestAsync(request.TenantId, request.UserId, request.ThreadId, request.Id, token).ConfigureAwait(false);
                if (current == null) return AskToolPermissionPromptResult.Deny("The conversation was deleted.");
                if (current.State == AskToolPermissionStateEnum.Allowed) return AskToolPermissionPromptResult.Allow(inputJson);
                if (current.State == AskToolPermissionStateEnum.Denied) return AskToolPermissionPromptResult.Deny("The conversation owner denied " + request.ToolName + ".");
                if (current.State == AskToolPermissionStateEnum.Expired) return AskToolPermissionPromptResult.Deny(ExpiredText(request.ToolName));
                if (DateTime.UtcNow >= request.ExpiresUtc)
                {
                    bool expired = await _Database.AskToolPermissions.TryExpireAsync(request.TenantId, request.UserId, request.ThreadId, request.Id, DateTime.UtcNow, token).ConfigureAwait(false);
                    if (expired) return AskToolPermissionPromptResult.Deny(ExpiredText(request.ToolName));
                    continue;
                }
                await Task.Delay(PollMilliseconds, token).ConfigureAwait(false);
            }
        }

        private async Task ExpireAsync(AskToolPermissionRequest request)
        {
            try { await _Database.AskToolPermissions.TryExpireAsync(request.TenantId, request.UserId, request.ThreadId, request.Id, DateTime.UtcNow).ConfigureAwait(false); }
            catch (Exception ex) { _Logging.Warn(_Header + "could not expire permission request " + request.Id + ": " + ex.Message); }
        }

        private async Task RecordDecisionAsync(AuthContext caller, AskToolPermissionRequest request, bool allow, bool remember, CancellationToken token)
        {
            ArmadaEvent evt = new ArmadaEvent("ask_tool_permission.decided", (allow ? "Allowed " : "Denied ") + request.ToolName + " in an Ask conversation")
            {
                TenantId = caller.TenantId,
                UserId = caller.UserId,
                EntityType = "ask_thread",
                EntityId = request.ThreadId,
                Payload = JsonSerializer.Serialize(new { threadId = request.ThreadId, requestId = request.Id, toolName = request.ToolName, allowed = allow, remembered = remember })
            };
            try { await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false); }
            catch (Exception ex) { _Logging.Warn(_Header + "could not record the permission decision: " + ex.Message); }
        }

        private static string ExpiredText(string toolName) => "No decision arrived in time, so " + toolName + " was denied.";

        private static string NormalizeInput(string? inputJson)
        {
            return String.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson;
        }

        private static void RequireOwner(AuthContext caller)
        {
            if (caller == null || !caller.IsAuthenticated || !caller.IsAdmin || !String.IsNullOrWhiteSpace(caller.MissionId)
                || !String.IsNullOrWhiteSpace(caller.AskThreadId) || !String.IsNullOrWhiteSpace(caller.AskTurnId)
                || String.IsNullOrWhiteSpace(caller.TenantId) || String.IsNullOrWhiteSpace(caller.UserId))
                throw new UnauthorizedAccessException("Tool permission decisions require the authenticated conversation owner.");
        }
    }
}
