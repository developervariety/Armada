namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;

    public static partial class CaptainLaunchIsolationPlanner
    {
        /// <summary>Build the existing runtime isolation strategy for an explicit remote MCP endpoint.</summary>
        /// <param name="runtime">Captain runtime.</param>
        /// <param name="mcpUrl">Explicit endpoint reachable from the checkout host.</param>
        /// <param name="scopedDirectory">Verified host-local configuration directory beneath the owned checkout.</param>
        /// <param name="credential">Mission-scoped credential, never an operator credential.</param>
        /// <returns>Token-reference files and runtime arguments for the checkout host.</returns>
        public static CaptainLaunchIsolationPlan PlanForHarbor(AgentRuntimeEnum runtime, string mcpUrl,
            string scopedDirectory, McpCredentialReference credential)
        {
            if (!Uri.TryCreate(mcpUrl, UriKind.Absolute, out Uri? endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttps
                    && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
                || endpoint.UserInfo.Length > 0 || endpoint.Fragment.Length > 0 || endpoint.Query.Length > 0)
                throw new InvalidOperationException("harbor_captain_mcp_endpoint_invalid");
            if (credential == null || !credential.HasToken)
                throw new InvalidOperationException("harbor_captain_mcp_credential_missing");
            const int placeholderPort = 1;
            CaptainLaunchIsolationPlan plan = Plan(runtime, placeholderPort, scopedDirectory, credential);
            string placeholder = ArmadaMcpConfigBuilder.GetMcpUrl(placeholderPort);
            foreach (IsolationConfigFile file in plan.FilesToWrite)
                file.Contents = file.Contents.Replace(placeholder, endpoint.AbsoluteUri, StringComparison.Ordinal);
            for (int index = 0; index < plan.ExtraArguments.Count; index++)
                plan.ExtraArguments[index] = plan.ExtraArguments[index].Replace(placeholder, endpoint.AbsoluteUri, StringComparison.Ordinal);
            if (runtime == AgentRuntimeEnum.OpenCode)
                plan.EnvironmentOverrides["OPENCODE_CONFIG"] = scopedDirectory.TrimEnd('/', '\\') + "/opencode.json";
            return plan;
        }
    }
}
