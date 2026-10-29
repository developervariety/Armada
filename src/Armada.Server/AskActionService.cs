namespace Armada.Server
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server.Mcp;
    using SyslogLogging;

    /// <summary>Saved-chat proposal gate. Human decisions invoke the original registered tool once.</summary>
    public sealed partial class AskActionService
    {
        private readonly DatabaseDriver _Database;
        private readonly LongRunningJobService _Jobs;
        private readonly LoggingModule _Logging;
        private readonly ConcurrentDictionary<string, Func<JsonElement?, Task<object>>> _Handlers = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _Active = new(StringComparer.Ordinal);

        /// <summary>Create the gate with the same job registry used by the MCP handlers.</summary>
        public AskActionService(DatabaseDriver database, LongRunningJobService jobs, LoggingModule? logging = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
            _Logging = logging ?? new LoggingModule();
        }

        /// <summary>Wrap registration after the abbreviated-ID registration wrapper.</summary>
        public RegisterToolDelegate Wrap(RegisterToolDelegate register)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            return (name, description, schema, handler) =>
            {
                _Handlers[name] = handler;
                register(name, description, schema, args => HandleCallAsync(name, args, handler));
            };
        }

        private async Task<object> HandleCallAsync(string toolName, JsonElement? arguments, Func<JsonElement?, Task<object>> handler)
        {
            AuthContext caller = McpCallerContext.Require();
            if (!AskToolPolicy.HasScope(caller)) return await handler(arguments).ConfigureAwait(false);
            if (!AskToolPolicy.IsAllowed(caller, toolName) || !await AskTurnBinding.IsBoundRunningTurnAsync(_Database, caller).ConfigureAwait(false))
                return new { Error = "This saved turn cannot use the requested tool.", Code = "ask_turn_unavailable" };
            if (AskToolPolicy.IsRead(toolName) || AskToolPolicy.IsPermissionPrompt(toolName)) return await handler(arguments).ConfigureAwait(false);
            try
            {
                AskActionProposal proposal = new AskActionProposal
                {
                    TenantId = caller.TenantId!, UserId = caller.UserId!, ThreadId = caller.AskThreadId!,
                    RequestId = caller.AskTurnId!, ToolName = toolName,
                    ArgumentsJson = AskActionArguments.Canonicalize(arguments),
                    CreatedUtc = DateTime.UtcNow, ExpiresUtc = DateTime.UtcNow.AddMinutes(30)
                };
                proposal.Fingerprint = AskActionArguments.Fingerprint(proposal);
                AskActionProposal? saved = await _Database.AskActionProposals.TryCreateAsync(proposal, McpCallerContext.CallToken).ConfigureAwait(false);
                if (saved == null) return new { Error = "The saved turn is no longer active.", Code = "ask_turn_unavailable" };
                return new { Code = "ask_action_proposed", ProposalId = saved.Id, Message = "Waiting for the human owner's approval in this conversation. Do not repeat this action." };
            }
            catch (ArgumentException) { return new { Error = "Protected or unsupported values cannot be saved as a chat action.", Code = "ask_action_not_supported" }; }
            catch (JsonException) { return new { Error = "Action arguments must be a bounded JSON object.", Code = "ask_action_not_supported" }; }
        }
    }
}
