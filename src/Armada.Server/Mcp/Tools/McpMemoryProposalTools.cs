namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Registers the operator tools for memory proposals: list them and dismiss one. A proposal is a
    /// typed-decision nomination of a durable lesson for the owner's AI-Memory. These are operator
    /// tools only: they are not caller-scoped, so a mission caller can neither list nor call them, and
    /// each handler refuses any caller other than a global administrator. Nothing here writes AI-Memory,
    /// and the model never dismisses.
    /// </summary>
    public static class McpMemoryProposalTools
    {
        #region Public-Members

        /// <summary>Registered name of the list tool.</summary>
        public const string ListToolName = "armada_list_memory_proposals";

        /// <summary>Registered name of the dismiss tool.</summary>
        public const string DismissToolName = "armada_dismiss_memory_proposal";

        /// <summary>Refusal reason returned to a caller other than a global administrator.</summary>
        public const string GlobalAdministratorRequiredReason = "global_administrator_required";

        #endregion

        #region Private-Members

        private const int _DefaultLimit = 25;
        private const int _MaxLimit = 200;

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the memory proposal tools.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="database">Database driver holding the proposal store.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));

            register(
                ListToolName,
                "List memory proposals: durable lessons the typed-decision system nominated for the owner's AI-Memory, from the weekly papercut sweep (D18) or the Recorder memory review (D23). Armada never writes AI-Memory; the owner promotes a proposal by hand and an operator dismisses it. Newest first.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        state = new { type = "string", description = "Open (default), Dismissed, or All" },
                        limit = new { type = "integer", description = "Maximum rows returned (default 25, maximum 200)" }
                    },
                    required = new string[] { }
                },
                async (args) =>
                {
                    AuthContext caller = McpCallerContext.Require();
                    if (!caller.IsAdmin) return Refused();

                    ListArgs request = args.HasValue
                        ? JsonSerializer.Deserialize<ListArgs>(args.Value, _JsonOptions) ?? new ListArgs()
                        : new ListArgs();

                    MemoryProposalStateEnum? state = MemoryProposalStateEnum.Open;
                    if (!String.IsNullOrWhiteSpace(request.State))
                    {
                        string requested = request.State!.Trim();
                        if (String.Equals(requested, "All", StringComparison.OrdinalIgnoreCase))
                            state = null;
                        else if (Enum.TryParse(requested, true, out MemoryProposalStateEnum parsed))
                            state = parsed;
                        else
                            return (object)new { Error = "Unknown state: " + request.State + ". Use Open, Dismissed, or All." };
                    }

                    int limit = Math.Clamp(request.Limit ?? _DefaultLimit, 1, _MaxLimit);
                    List<MemoryProposal> proposals = await database.MemoryProposals.EnumerateAsync(null, state, limit).ConfigureAwait(false);
                    return (object)new
                    {
                        State = state.HasValue ? state.Value.ToString() : "All",
                        Count = proposals.Count,
                        Proposals = proposals
                    };
                });

            register(
                DismissToolName,
                "Dismiss one open memory proposal after the owner promoted it by hand or decided against it. Requires the proposal id, a reason, and the operator name. The proposal is kept with its dismissal; nothing is deleted and AI-Memory is not touched.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        id = new { type = "string", description = "Memory proposal id (mpr_ prefix)" },
                        reason = new { type = "string", description = "Why the proposal is dismissed, for example promoted to shared memory or not a durable lesson" },
                        @operator = new { type = "string", description = "Who dismissed it" }
                    },
                    required = new[] { "id", "reason", "operator" }
                },
                async (args) =>
                {
                    AuthContext caller = McpCallerContext.Require();
                    if (!caller.IsAdmin) return Refused();

                    DismissArgs request = args.HasValue
                        ? JsonSerializer.Deserialize<DismissArgs>(args.Value, _JsonOptions) ?? new DismissArgs()
                        : new DismissArgs();
                    if (String.IsNullOrWhiteSpace(request.Id)) return (object)new { Error = "id is required" };
                    if (String.IsNullOrWhiteSpace(request.Reason)) return (object)new { Error = "reason is required" };
                    if (String.IsNullOrWhiteSpace(request.Operator)) return (object)new { Error = "operator is required" };

                    MemoryProposal? existing = await database.MemoryProposals.ReadAsync(request.Id!).ConfigureAwait(false);
                    if (existing == null) return (object)new { Error = "Memory proposal not found: " + request.Id };
                    if (existing.State != MemoryProposalStateEnum.Open)
                        return (object)new { Error = "Memory proposal " + existing.Id + " is already " + existing.State + ".", Proposal = existing };

                    bool dismissed = await database.MemoryProposals.DismissAsync(existing.Id, request.Operator!.Trim(), request.Reason!.Trim(), DateTime.UtcNow).ConfigureAwait(false);
                    MemoryProposal? after = await database.MemoryProposals.ReadAsync(existing.Id).ConfigureAwait(false);
                    if (!dismissed)
                        return (object)new { Error = "Memory proposal " + existing.Id + " changed while it was being dismissed.", Proposal = after };
                    return (object)new { Dismissed = true, Proposal = after };
                });
        }

        #endregion

        #region Private-Methods

        private static object Refused()
        {
            return new { Error = "Memory proposal tools require a global administrator.", Reason = GlobalAdministratorRequiredReason };
        }

        #endregion

        #region Private-Types

        private sealed class ListArgs
        {
            public string? State { get; set; }

            public int? Limit { get; set; }
        }

        private sealed class DismissArgs
        {
            public string? Id { get; set; }

            public string? Reason { get; set; }

            public string? Operator { get; set; }
        }

        #endregion
    }
}
