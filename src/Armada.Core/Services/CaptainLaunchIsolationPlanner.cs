namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using Armada.Core.Enums;

    /// <summary>
    /// Pure planner that, for a given runtime, produces the steps to launch a captain in an isolated agent
    /// configuration: what extra CLI arguments to append, what environment overrides to apply, and what
    /// scoped configuration files to write so the agent still reaches the Armada MCP server while being
    /// blocked from the host user's global settings.
    ///
    /// Strategy per runtime:
    /// - Claude Code: --strict-mcp-config + an injected --mcp-config file (strict ignores host servers, so
    ///   the Armada server must be supplied explicitly) plus --setting-sources project,local.
    /// - Codex: a per-process config override registers the Armada server without hiding the
    ///   captain's existing authentication and provider profiles behind a replacement CODEX_HOME.
    /// - Gemini / Cursor: a scoped HOME/USERPROFILE containing the client's settings file, so they
    ///   physically cannot read the host user's configuration.
    /// - Mux: a scoped MUX_CONFIG_DIR containing mcp-servers.json, whose auth object presents the launch
    ///   credential as a bearer token referenced by variable name.
    ///
    /// Every plan puts a chosen MCP credential in the captain's environment, and each client references it
    /// by variable name, so the token never lands in a scoped configuration file. A mission launch carries
    /// the admiral launch credential; a chat launch carries the caller's own session token instead.
    ///
    /// A captain on a subscription account also receives that account's login switch through
    /// <see cref="ApplyAccount"/>, independent of MCP isolation.
    ///
    /// Side-effect free (writes nothing) so it can be unit tested in isolation; the caller materializes the
    /// returned files and applies the environment/arguments.
    /// </summary>
    public static class CaptainLaunchIsolationPlanner
    {
        #region Public-Methods

        /// <summary>
        /// Build the isolation plan for a runtime. Returns an empty plan (nothing to apply) when isolation
        /// cannot be expressed for the runtime or when the MCP port is invalid.
        /// </summary>
        /// <param name="runtime">The captain's runtime.</param>
        /// <param name="mcpPort">The Admiral MCP port (must be positive).</param>
        /// <param name="scopedConfigDirectory">Absolute path to the per-launch scoped configuration directory.</param>
        /// <returns>The isolation plan; never null.</returns>
        public static CaptainLaunchIsolationPlan Plan(AgentRuntimeEnum runtime, int mcpPort, string scopedConfigDirectory)
        {
            return Plan(runtime, mcpPort, scopedConfigDirectory, McpCredentialReference.Launch);
        }

        /// <summary>
        /// Build the isolation plan for a runtime with a chosen MCP credential. The configuration files reference
        /// the credential's environment variable by name; the environment carries the value only when the
        /// credential has one, so an unauthenticated chat turn writes the same configuration but presents no
        /// credential and reaches no MCP tool. Returns an empty plan when isolation cannot be expressed for the
        /// runtime or when the MCP port is invalid.
        /// </summary>
        /// <param name="runtime">The captain's runtime.</param>
        /// <param name="mcpPort">The Admiral MCP port (must be positive).</param>
        /// <param name="scopedConfigDirectory">Absolute path to the per-launch scoped configuration directory.</param>
        /// <param name="credential">The MCP credential the launch references; its value never lands in a file.</param>
        /// <returns>The isolation plan; never null.</returns>
        public static CaptainLaunchIsolationPlan Plan(AgentRuntimeEnum runtime, int mcpPort, string scopedConfigDirectory, McpCredentialReference credential)
        {
            if (credential == null) throw new ArgumentNullException(nameof(credential));

            CaptainLaunchIsolationPlan plan = new CaptainLaunchIsolationPlan();
            if (mcpPort <= 0 || mcpPort > 65535) return plan;
            if (String.IsNullOrWhiteSpace(scopedConfigDirectory)) return plan;

            // The Armada MCP endpoint refuses a request without credentials. A launched captain carries the
            // chosen credential's value in its environment, and the configuration files below reference it by
            // variable name in each client's own syntax. A credential with no value (an unauthenticated chat
            // turn) leaves the variable unset, so the runtime presents nothing and the endpoint refuses it.
            if (credential.HasToken)
                plan.EnvironmentOverrides[credential.EnvironmentVariable] = credential.Token;

            switch (runtime)
            {
                case AgentRuntimeEnum.ClaudeCode:
                    {
                        plan.FilesToWrite.Add(new IsolationConfigFile("armada-mcp.json", ArmadaMcpConfigBuilder.BuildKeyedMcpServersJson(mcpPort, ArmadaMcpConfigBuilder.AuthorizationDollarBrace(credential.EnvironmentVariable))));
                        string mcpConfigPath = Path.Combine(scopedConfigDirectory, "armada-mcp.json");
                        plan.ExtraArguments.Add("--setting-sources");
                        plan.ExtraArguments.Add("project,local");
                        plan.ExtraArguments.Add("--strict-mcp-config");
                        plan.ExtraArguments.Add("--mcp-config");
                        plan.ExtraArguments.Add(mcpConfigPath);
                        break;
                    }
                case AgentRuntimeEnum.Codex:
                    {
                        plan.ExtraArguments.Add("-c");
                        plan.ExtraArguments.Add("mcp_servers.armada.url=\"" + ArmadaMcpConfigBuilder.GetMcpUrl(mcpPort) + "\"");
                        plan.ExtraArguments.Add("-c");
                        plan.ExtraArguments.Add("mcp_servers.armada.bearer_token_env_var=\"" + credential.EnvironmentVariable + "\"");
                        break;
                    }
                case AgentRuntimeEnum.Gemini:
                    {
                        plan.FilesToWrite.Add(new IsolationConfigFile(Path.Combine(".gemini", "settings.json"), ArmadaMcpConfigBuilder.BuildKeyedMcpServersJson(mcpPort, ArmadaMcpConfigBuilder.AuthorizationDollarBrace(credential.EnvironmentVariable))));
                        ApplyHomeOverride(plan, scopedConfigDirectory);
                        break;
                    }
                case AgentRuntimeEnum.Cursor:
                    {
                        plan.FilesToWrite.Add(new IsolationConfigFile(Path.Combine(".cursor", "mcp.json"), ArmadaMcpConfigBuilder.BuildKeyedMcpServersJson(mcpPort, ArmadaMcpConfigBuilder.AuthorizationCursor(credential.EnvironmentVariable))));
                        ApplyHomeOverride(plan, scopedConfigDirectory);
                        break;
                    }
                case AgentRuntimeEnum.OpenCode:
                    {
                        plan.FilesToWrite.Add(new IsolationConfigFile("opencode.json", ArmadaMcpConfigBuilder.BuildOpenCodeMcpJson(mcpPort, ArmadaMcpConfigBuilder.AuthorizationOpenCode(credential.EnvironmentVariable))));
                        break;
                    }
                case AgentRuntimeEnum.Mux:
                    {
                        plan.FilesToWrite.Add(new IsolationConfigFile("mcp-servers.json", ArmadaMcpConfigBuilder.BuildMuxServersJson(mcpPort, credential.EnvironmentVariable)));
                        plan.EnvironmentOverrides["MUX_CONFIG_DIR"] = scopedConfigDirectory;
                        break;
                    }
                default:
                    break;
            }

            return plan;
        }

        /// <summary>
        /// Add a captain's subscription account login switch to a launch plan: CLAUDE_CONFIG_DIR, CODEX_HOME,
        /// XDG_DATA_HOME, or CURSOR_API_KEY. The switch is always the account's own home or key, never a scoped
        /// directory, so the runtime keeps that account's login and provider profiles. A captain with no account, or an
        /// account with no login binding, leaves the plan unchanged.
        /// </summary>
        /// <param name="plan">Plan to extend; returned for chaining.</param>
        /// <param name="captain">Captain being launched.</param>
        /// <param name="account">The captain's account, or null.</param>
        /// <param name="readEnvironment">Reads a server environment variable; defaults to the process environment.</param>
        /// <returns>The same plan.</returns>
        /// <exception cref="CaptainAccountLaunchException">The account cannot supply its login.</exception>
        public static CaptainLaunchIsolationPlan ApplyAccount(CaptainLaunchIsolationPlan plan, Armada.Core.Models.Captain captain, Armada.Core.Settings.UsageAccountSettings? account, Func<string, string?>? readEnvironment = null)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (!CaptainAccountLaunch.HasLaunchIdentity(account)) return plan;
            // Captains that carry their own provider key or endpoint keep their launch unchanged.
            if (!String.IsNullOrWhiteSpace(captain.ApiKey) || !String.IsNullOrWhiteSpace(captain.ApiBaseUrl))
                throw new CaptainAccountLaunchException(CaptainAccountLaunch.ReasonProviderCaptain, account!.Id);
            foreach (System.Collections.Generic.KeyValuePair<string, string> pair in CaptainAccountLaunch.BuildEnvironment(captain.Runtime, account, readEnvironment))
                plan.EnvironmentOverrides[pair.Key] = pair.Value;
            return plan;
        }

        /// <summary>
        /// Build the launch plan for one captain start. When dock MCP delivery is enabled, Claude Code, Codex and
        /// Mux receive their scoped MCP configuration, and every runtime receives the launch credential, because the
        /// dock configuration seeded for Cursor, Gemini and OpenCode references it by variable name.
        /// </summary>
        /// <param name="runtime">Captain runtime.</param>
        /// <param name="seedDockMcpConfig">Whether dock and launch MCP delivery is enabled.</param>
        /// <param name="mcpPort">Local Armada MCP port.</param>
        /// <param name="scopedConfigDirectory">Per-launch scoped configuration directory.</param>
        /// <returns>The plan; empty when nothing applies.</returns>
        public static CaptainLaunchIsolationPlan PlanForLaunch(AgentRuntimeEnum runtime, bool seedDockMcpConfig, int mcpPort, string scopedConfigDirectory)
        {
            if (!seedDockMcpConfig) return new CaptainLaunchIsolationPlan();
            bool scoped = runtime == AgentRuntimeEnum.ClaudeCode || runtime == AgentRuntimeEnum.Codex || runtime == AgentRuntimeEnum.Mux;
            CaptainLaunchIsolationPlan plan = scoped ? Plan(runtime, mcpPort, scopedConfigDirectory) : new CaptainLaunchIsolationPlan();
            // The endpoint refuses a request without credentials, so a runtime that reads only its dock
            // configuration still needs the credential that configuration names.
            plan.EnvironmentOverrides[McpLaunchCredential.EnvironmentVariable] = McpLaunchCredential.Token;
            return plan;
        }

        #endregion

        #region Private-Methods

        private static void ApplyHomeOverride(CaptainLaunchIsolationPlan plan, string scopedConfigDirectory)
        {
            // HOME is honored on POSIX; USERPROFILE and HOMEPATH cover Windows CLIs that resolve the user
            // profile. Setting all three makes the scoped directory the effective home regardless of OS.
            plan.EnvironmentOverrides["HOME"] = scopedConfigDirectory;
            plan.EnvironmentOverrides["USERPROFILE"] = scopedConfigDirectory;
        }

        #endregion
    }
}
