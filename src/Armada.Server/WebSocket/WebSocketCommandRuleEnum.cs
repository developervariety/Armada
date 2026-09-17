namespace Armada.Server.WebSocket
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// The authorization rule a WebSocket command declares. A command's rule is never looser than the matching REST
    /// route or MCP tool: where the two differ, the command takes the stricter of them.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum WebSocketCommandRuleEnum
    {
        /// <summary>
        /// Any authenticated caller. The command reads one owned record through the shared caller scope, so a
        /// record the caller may not read is refused as not found and no data is returned.
        /// </summary>
        ReadScoped,

        /// <summary>
        /// Any authenticated caller. The command lists through the shared caller-scoped query, so the result holds
        /// only records the caller may read.
        /// </summary>
        ListScoped,

        /// <summary>
        /// A global administrator or a tenant administrator. The command finds its record through the shared caller
        /// scope and changes it only when the shared ownership rule lets the caller edit it, as the REST route and
        /// MCP tool do, so a tenant administrator changes only their own tenant's records.
        /// </summary>
        TenantAdminScoped,

        /// <summary>
        /// A global administrator only. The command acts on fleet-wide state or on records the MCP surface reserves
        /// for global administrators.
        /// </summary>
        GlobalAdmin
    }
}
