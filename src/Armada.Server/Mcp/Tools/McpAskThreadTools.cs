namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using Armada.Core.Models;

    /// <summary>
    /// Registers the read-only tools for saved Ask conversations: list the caller's conversations and read one
    /// with its stored turns. There is deliberately no tool that sends a message, decides a proposal, changes a
    /// conversation or deletes one: those stay human actions in the Dashboard and the REST API. Every call
    /// reads through <see cref="AskThreadService"/> as the authenticated caller, so a caller sees only the
    /// conversations it owns, and a mission or Ask-turn credential, which is not a human owner, is refused.
    /// </summary>
    public static class McpAskThreadTools
    {
        #region Public-Members

        /// <summary>Registered name of the list tool.</summary>
        public const string ListToolName = "armada_list_ask_threads";

        /// <summary>Registered name of the read tool.</summary>
        public const string GetToolName = "armada_get_ask_thread";

        #endregion

        #region Private-Members

        private const int _DefaultThreadPageSize = 25;
        private const int _MaxThreadPageSize = 100;
        private const int _DefaultTurnPageSize = 10;
        private const int _MaxTurnPageSize = 50;

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register the saved Ask conversation read tools.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="threads">The saved conversation service that also backs the REST routes.</param>
        public static void Register(RegisterToolDelegate register, AskThreadService threads)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (threads == null) throw new ArgumentNullException(nameof(threads));

            register(
                ListToolName,
                "List the caller's saved Ask conversations, pinned first and then newest activity first. Read-only. Each row carries the title, captain, pinned and archived flags, activity time, the active turn id while a turn runs, and the unread reply count.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        pageNumber = new { type = "integer", description = "One-based page number (default 1)" },
                        pageSize = new { type = "integer", description = "Conversations per page (default 25, maximum 100)" },
                        includeArchived = new { type = "boolean", description = "Include archived conversations (default false)" }
                    },
                    required = new string[] { }
                },
                async (args) =>
                {
                    ListArgs request = Parse<ListArgs>(args);
                    try
                    {
                        EnumerationResult<AskThread> page = await threads.EnumerateThreadsAsync(
                            McpCallerContext.Require(),
                            Math.Max(1, request.PageNumber ?? 1),
                            Math.Clamp(request.PageSize ?? _DefaultThreadPageSize, 1, _MaxThreadPageSize),
                            request.IncludeArchived ?? false,
                            McpCallerContext.CallToken).ConfigureAwait(false);
                        return (object)page;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        return (object)new { Error = ex.Message };
                    }
                });

            register(
                GetToolName,
                "Read one saved Ask conversation the caller owns: its metadata and one page of stored turns, oldest first within the page. Read-only. Page back with beforeSequence set to the first sequence of the previous page. Tool-call cards are left out unless includeToolCalls is true.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        threadId = new { type = "string", description = "Ask thread id" },
                        beforeSequence = new { type = "integer", description = "Return turns older than this sequence number (default newest)" },
                        pageSize = new { type = "integer", description = "Turns per page (default 10, maximum 50)" },
                        includeToolCalls = new { type = "boolean", description = "Include the tool calls each turn made (default false)" }
                    },
                    required = new[] { "threadId" }
                },
                async (args) =>
                {
                    GetArgs request = Parse<GetArgs>(args);
                    if (String.IsNullOrWhiteSpace(request.ThreadId)) return (object)new { Error = "threadId is required." };
                    try
                    {
                        AuthContext caller = McpCallerContext.Require();
                        AskThread? thread = await threads.GetThreadAsync(caller, request.ThreadId.Trim(), McpCallerContext.CallToken).ConfigureAwait(false);
                        if (thread == null) return (object)new { Error = "Ask thread not found: " + request.ThreadId.Trim() };
                        List<AskTurn> turns = await threads.EnumerateTurnsAsync(
                            caller, thread.Id, request.BeforeSequence,
                            Math.Clamp(request.PageSize ?? _DefaultTurnPageSize, 1, _MaxTurnPageSize),
                            McpCallerContext.CallToken).ConfigureAwait(false) ?? new List<AskTurn>();
                        if (!(request.IncludeToolCalls ?? false))
                        {
                            foreach (AskTurn turn in turns)
                            {
                                turn.LiveToolCalls = new List<ChatToolCall>();
                                if (turn.Response != null) turn.Response.ToolCalls = new List<ChatToolCall>();
                            }
                        }
                        return (object)new { Thread = thread, Turns = turns };
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        return (object)new { Error = ex.Message };
                    }
                });
        }

        #endregion

        #region Private-Methods

        private static T Parse<T>(JsonElement? args) where T : new()
        {
            return args.HasValue ? JsonSerializer.Deserialize<T>(args.Value, _JsonOptions) ?? new T() : new T();
        }

        #endregion

        #region Private-Types

        private sealed class ListArgs
        {
            public int? PageNumber { get; set; }
            public int? PageSize { get; set; }
            public bool? IncludeArchived { get; set; }
        }

        private sealed class GetArgs
        {
            public string ThreadId { get; set; } = String.Empty;
            public long? BeforeSequence { get; set; }
            public int? PageSize { get; set; }
            public bool? IncludeToolCalls { get; set; }
        }

        #endregion
    }
}
