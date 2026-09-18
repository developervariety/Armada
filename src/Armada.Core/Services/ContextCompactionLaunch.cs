namespace Armada.Core.Services
{
    /// <summary>
    /// The environment a launched captain's harness plugin reads to ask the admiral which earlier tool
    /// results are still load-bearing. One set of names, shared by the launch that sets them and every
    /// plugin that reads them, so they cannot drift apart.
    /// <para>
    /// None of these is a secret. The plugin authenticates with the MCP credential the launch already
    /// carries, and the typed-decision provider key never enters a captain's environment: the admiral
    /// redacts and forwards.
    /// </para>
    /// </summary>
    public static class ContextCompactionLaunch
    {
        /// <summary>The calling mission's id, which scopes the call and selects the vessel's egress rule.</summary>
        public const string MissionIdVariable = "ARMADA_MISSION_ID";

        /// <summary>The admiral's MCP endpoint, which the plugin calls as the captain.</summary>
        public const string McpUrlVariable = "ARMADA_MCP_URL";

        /// <summary>The MCP tool a plugin calls.</summary>
        public const string ToolName = "armada_context_compaction";
    }
}
