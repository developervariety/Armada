namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;

    /// <summary>
    /// Registers the permission prompt tool that Claude Code calls before a native tool runs in an attended saved Ask turn.
    /// The tool answers only a caller that holds the turn's own scoped credential; every other caller, including missions
    /// and human credentials, neither sees it nor can call it.
    /// </summary>
    public static class McpAskToolPermissionTools
    {
        #region Public-Methods

        /// <summary>
        /// Register the permission prompt tool.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="permissions">The permission service.</param>
        public static void Register(RegisterToolDelegate register, AskToolPermissionService permissions)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (permissions == null) throw new ArgumentNullException(nameof(permissions));

            register(
                AskToolPolicy.PermissionPromptToolName,
                "Permission prompt for the native tools of an attended saved Ask turn. The runtime calls it before a native tool runs. The result is {\"behavior\":\"allow\",\"updatedInput\":{...}} or {\"behavior\":\"deny\",\"message\":\"...\"}. The request waits for the conversation owner's decision in the Dashboard and is denied when it expires. Only that turn's own credential can call it.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        tool_name = new { type = "string", description = "Native tool that needs permission" },
                        input = new { type = "object", description = "The tool input" },
                        tool_use_id = new { type = "string", description = "The runtime's identifier for the call" }
                    },
                    required = new[] { "tool_name" }
                },
                async (args) =>
                {
                    string? toolName = null;
                    string? inputJson = null;
                    string? toolUseId = null;
                    if (args.HasValue && args.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (args.Value.TryGetProperty("tool_name", out JsonElement name) && name.ValueKind == JsonValueKind.String) toolName = name.GetString();
                        if (args.Value.TryGetProperty("input", out JsonElement input) && input.ValueKind == JsonValueKind.Object) inputJson = input.GetRawText();
                        if (args.Value.TryGetProperty("tool_use_id", out JsonElement use) && use.ValueKind == JsonValueKind.String) toolUseId = use.GetString();
                    }
                    return (object)await permissions.RequestAsync(McpCallerContext.Require(), toolName, inputJson, toolUseId, McpCallerContext.CallToken).ConfigureAwait(false);
                });
        }

        #endregion
    }
}
