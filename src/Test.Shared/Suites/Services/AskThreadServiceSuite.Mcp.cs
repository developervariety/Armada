namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class AskThreadServiceSuite
    {
        private static async Task McpReadToolsAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                AskThreadRuntimeFactory factory = new AskThreadRuntimeFactory(logging) { Lines = new List<string> { ToolStarted, ToolFinished } };
                AskThreadService service = Service(db, factory, logging);
                AskThread thread = await ThreadAsync(db, service).ConfigureAwait(false);
                await service.SendTurnAsync(Owner(), thread.Id, new AskTurnSendRequest { RequestId = "mcp-one", Content = "First question" }).ConfigureAwait(false);

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);
                McpAskThreadTools.Register((name, description, schema, handler) => handlers[name] = handler, service);
                AssertEqual("armada_get_ask_thread,armada_list_ask_threads", String.Join(",", handlers.Keys.OrderBy(name => name, StringComparer.Ordinal)),
                    "saved Ask management over MCP is read-only: no send, decide, change or delete tool exists");

                string listed = await CallAsync(handlers[McpAskThreadTools.ListToolName], Owner(), "{}").ConfigureAwait(false);
                AssertContains(thread.Id, listed);
                AssertContains("\"unreadCount\":1", listed, "the list reports the unread reply count");

                string read = await CallAsync(handlers[McpAskThreadTools.GetToolName], Owner(), "{\"threadId\":\"" + thread.Id + "\"}").ConfigureAwait(false);
                AssertContains("First question", read);
                AssertContains("Saved assistant reply", read);
                AssertFalse(read.Contains("src/File.cs", StringComparison.Ordinal), "tool-call cards are left out by default");
                string withTools = await CallAsync(handlers[McpAskThreadTools.GetToolName], Owner(), "{\"threadId\":\"" + thread.Id + "\",\"includeToolCalls\":true}").ConfigureAwait(false);
                AssertContains("src/File.cs", withTools);

                AuthContext stranger = AuthContext.Authenticated("default", "usr_stranger", true, true, "Session");
                AssertFalse((await CallAsync(handlers[McpAskThreadTools.ListToolName], stranger, "{}").ConfigureAwait(false)).Contains(thread.Id, StringComparison.Ordinal), "another user does not see the conversation");
                AssertContains("not found", await CallAsync(handlers[McpAskThreadTools.GetToolName], stranger, "{\"threadId\":\"" + thread.Id + "\"}").ConfigureAwait(false));

                AuthContext mission = AuthContext.Authenticated("default", "default", true, true, "Session");
                mission.MissionId = "msn_example";
                AssertContains("human owner", await CallAsync(handlers[McpAskThreadTools.ListToolName], mission, "{}").ConfigureAwait(false), "a mission credential is refused");
            }
        }

        private static async Task<string> CallAsync(Func<JsonElement?, Task<object>> handler, AuthContext caller, string arguments)
        {
            using (McpCallerContext.Begin(caller))
            using (JsonDocument document = JsonDocument.Parse(arguments))
            {
                object result = await handler(document.RootElement.Clone()).ConfigureAwait(false);
                return JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }
        }
    }
}
