namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading.Tasks;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Sends persona create, update, and delete over one surface (REST, MCP, or WebSocket) as the global admin and
    /// reduces the answer to ok, invalid, notfound, or forbidden, so one test can compare the surfaces.
    /// </summary>
    public sealed class PersonaSurface
    {
        #region Private-Members

        private readonly SecurityTestServer _Server;
        private readonly string _Surface;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="server">Running test server.</param>
        /// <param name="surface">rest, mcp, or ws.</param>
        /// <exception cref="ArgumentNullException">Thrown when server is null.</exception>
        public PersonaSurface(SecurityTestServer server, string surface)
        {
            _Server = server ?? throw new ArgumentNullException(nameof(server));
            _Surface = surface;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Create a persona.
        /// </summary>
        /// <param name="persona">Persona fields (PascalCase).</param>
        /// <returns>ok, invalid, notfound, or forbidden.</returns>
        public async Task<string> CreateAsync(object persona)
        {
            switch (_Surface)
            {
                case "rest":
                {
                    using HttpClient rest = _Server.CreateRestClient(true);
                    using HttpResponseMessage response = await rest.PostAsync("/api/v1/personas", Json(persona)).ConfigureAwait(false);
                    return FromStatus(response.StatusCode);
                }
                case "mcp":
                    return FromText(await CallAsync("create_persona", Fields(persona)).ConfigureAwait(false));
                default:
                    return FromText(await SendAsync("create_persona", null, persona).ConfigureAwait(false));
            }
        }

        /// <summary>
        /// Update a persona by name.
        /// </summary>
        /// <param name="name">Persona name.</param>
        /// <param name="update">Fields to change (PascalCase); a key with a null value is sent as an explicit null.</param>
        /// <returns>ok, invalid, notfound, or forbidden.</returns>
        public async Task<string> UpdateAsync(string name, object update)
        {
            switch (_Surface)
            {
                case "rest":
                {
                    using HttpClient rest = _Server.CreateRestClient(true);
                    using HttpResponseMessage response = await rest.PutAsync("/api/v1/personas/" + Uri.EscapeDataString(name), Json(update)).ConfigureAwait(false);
                    return FromStatus(response.StatusCode);
                }
                case "mcp":
                {
                    Dictionary<string, object?> args = Fields(update);
                    args["name"] = name;
                    return FromText(await CallAsync("update_persona", args).ConfigureAwait(false));
                }
                default:
                    return FromText(await SendAsync("update_persona", name, update).ConfigureAwait(false));
            }
        }

        /// <summary>
        /// Delete a persona by name.
        /// </summary>
        /// <param name="name">Persona name.</param>
        /// <returns>ok, invalid, notfound, or forbidden.</returns>
        public async Task<string> DeleteAsync(string name)
        {
            switch (_Surface)
            {
                case "rest":
                {
                    using HttpClient rest = _Server.CreateRestClient(true);
                    using HttpResponseMessage response = await rest.DeleteAsync("/api/v1/personas/" + Uri.EscapeDataString(name)).ConfigureAwait(false);
                    return FromStatus(response.StatusCode);
                }
                case "mcp":
                    return FromText(await CallAsync("delete_persona", new Dictionary<string, object?> { ["name"] = name }).ConfigureAwait(false));
                default:
                    return FromText(await SendAsync("delete_persona", name, null).ConfigureAwait(false));
            }
        }

        #endregion

        #region Private-Methods

        private async Task<string> CallAsync(string tool, Dictionary<string, object?> args)
        {
            using HttpClient mcp = _Server.CreateMcpClient(true);
            return await McpTestCalls.CallToolAsync(mcp, tool, args).ConfigureAwait(false);
        }

        private async Task<string> SendAsync(string action, string? id, object? data)
        {
            Uri rest = new Uri(_Server.BaseUrl);
            using WebSocketTestClient ws = await WebSocketTestClient.ConnectAsync(rest.Port, null, new Dictionary<string, string> { ["X-Api-Key"] = _Server.ApiKey }).ConfigureAwait(false);
            await ws.SendAsync(new { Route = "command", action = action, id = id, data = data }).ConfigureAwait(false);
            string? reply = await ws.WaitForAsync(m => m.Contains("\"" + action + "\"") && (m.Contains("command.result") || m.Contains("command.error"))).ConfigureAwait(false);
            AssertNotNull(reply, "No WebSocket reply to " + action);
            return reply!;
        }

        /// <summary>
        /// PascalCase fields to camelCase MCP arguments, keeping explicit nulls.
        /// </summary>
        private static Dictionary<string, object?> Fields(object value)
        {
            Dictionary<string, object?> source = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(value)) ?? new Dictionary<string, object?>();
            Dictionary<string, object?> args = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> kvp in source) args[Char.ToLowerInvariant(kvp.Key[0]) + kvp.Key.Substring(1)] = kvp.Value;
            return args;
        }

        private static StringContent Json(object value)
        {
            // Nulls are kept: an explicit null field is part of what the update means.
            return new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
        }

        private static string FromStatus(HttpStatusCode status)
        {
            int code = (int)status;
            if (code >= 200 && code < 300) return "ok";
            if (status == HttpStatusCode.BadRequest) return "invalid";
            if (status == HttpStatusCode.NotFound) return "notfound";
            if (status == HttpStatusCode.Forbidden) return "forbidden";
            return "http " + code;
        }

        private static string FromText(string reply)
        {
            if (reply.Contains("InvalidArgument")) return "invalid";
            if (reply.Contains("NotFound")) return "notfound";
            if (reply.Contains("Forbidden")) return "forbidden";
            if (reply.Contains("command.error") || reply.Contains("ErrorCode")) return "error: " + reply;
            return "ok";
        }

        #endregion
    }
}
